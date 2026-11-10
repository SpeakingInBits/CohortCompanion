using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Images;
using CohortCompanion.Web.Presentations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CohortCompanion.Tests;

public sealed class ImageCleanupTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly string folder = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));

    // Files are stamped by the real clock, so the test clock starts there too.
    private readonly ManualTimeProvider time = new(DateTimeOffset.UtcNow);
    private TestDbContextFactory factory = default!;
    private LocalImageStore store = default!;
    private ImageService images = default!;
    private ImageCleanup cleanup = default!;
    private string ownerId = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var owner = new ApplicationUser { UserName = "a@example.com", Email = "a@example.com", DisplayName = "A" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        ownerId = owner.Id;

        store = new LocalImageStore(folder);
        images = new ImageService(store);
        cleanup = new ImageCleanup(factory, store, time, NullLogger<ImageCleanup>.Instance);
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<string> UploadAsync() => (await images.UploadAsync(new MemoryStream(Png))).Url!;

    private void LetImagesGetOld() => time.Advance(ImageCleanup.GracePeriod + TimeSpan.FromHours(1));

    private bool IsStored(string url) => File.Exists(Path.Combine(folder, url[ImageService.RoutePrefix.Length..]));

    private async Task<int> AddQuestionAsync(string imageUrl)
    {
        await using var db = factory.CreateDbContext();
        var quiz = new Quiz
        {
            OwnerId = ownerId,
            Title = "Quiz",
            Questions = [new Question { Prompt = "<p>What is this?</p>", ImageUrl = imageUrl }],
        };
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync();
        return quiz.Id;
    }

    [Fact]
    public async Task AnImageNothingUses_IsDeletedOnceItIsOld()
    {
        var url = await UploadAsync();

        Assert.Equal(0, await cleanup.RunAsync());
        Assert.True(IsStored(url), "A new upload may belong to a quiz that hasn't been saved yet.");

        LetImagesGetOld();

        Assert.Equal(1, await cleanup.RunAsync());
        Assert.False(IsStored(url));
    }

    [Fact]
    public async Task AnImageOnAQuestion_IsKeptUntilItIsTakenOff()
    {
        var url = await UploadAsync();
        var other = await UploadAsync();
        var quizId = await AddQuestionAsync(url);
        LetImagesGetOld();

        Assert.Equal(1, await cleanup.RunAsync());
        Assert.True(IsStored(url));
        Assert.False(IsStored(other));

        await using (var db = factory.CreateDbContext())
        {
            await db.Questions.Where(q => q.QuizId == quizId).ExecuteUpdateAsync(q => q.SetProperty(x => x.ImageUrl, (string?)null));
        }

        Assert.Equal(1, await cleanup.RunAsync());
        Assert.False(IsStored(url));
    }

    [Fact]
    public async Task AnImageOnASlide_IsKeptUntilTheDeckIsDeleted()
    {
        var url = await UploadAsync();
        var decks = new PresentationService(factory, new ContentSanitizer(), time);
        var created = await decks.CreateAsync(ownerId, "Deck");
        var deck = (await decks.GetAsync(created.Id, ownerId))!;
        deck.Slides[0].Content.ImageUrl = url;
        await decks.SaveAsync(deck, ownerId);
        LetImagesGetOld();

        Assert.Equal(0, await cleanup.RunAsync());
        Assert.True(IsStored(url));

        await decks.DeleteAsync(deck.Id, ownerId, isAdmin: false);

        Assert.Equal(1, await cleanup.RunAsync());
        Assert.False(IsStored(url));
    }

    [Fact]
    public async Task AnImageARunningSessionStillShows_IsKeptUntilTheSessionEnds()
    {
        var url = await UploadAsync();
        var sessionId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            // The quiz has since lost the image, but the session runs from the copy it took when it started.
            db.LiveSessions.Add(new LiveSession
            {
                Id = sessionId,
                HostId = ownerId,
                JoinCode = "123456",
                Title = "Quiz",
                Status = SessionStatus.Running,
                SnapshotJson = $$"""{"questions":[{"imageUrl":"{{url}}"}]}""",
            });
            await db.SaveChangesAsync();
        }

        LetImagesGetOld();

        Assert.Equal(0, await cleanup.RunAsync());
        Assert.True(IsStored(url));

        await using (var db = factory.CreateDbContext())
        {
            await db.LiveSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Ended));
        }

        Assert.Equal(1, await cleanup.RunAsync());
        Assert.False(IsStored(url));
    }

    [Fact]
    public async Task FilesThatAreNotUploads_AreLeftAlone()
    {
        Directory.CreateDirectory(folder);
        var stray = Path.Combine(folder, "notes.txt");
        await File.WriteAllTextAsync(stray, "not an upload");
        LetImagesGetOld();

        Assert.Equal(0, await cleanup.RunAsync());
        Assert.True(File.Exists(stray));
    }
}
