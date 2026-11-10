using System.Text;
using CohortCompanion.Shared;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Images;
using CohortCompanion.Web.Presentations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class PresentationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private TestDbContextFactory factory = default!;
    private PresentationService service = default!;
    private string ownerId = default!;
    private string otherId = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var owner = new ApplicationUser { UserName = "a@example.com", Email = "a@example.com", DisplayName = "A" };
        var other = new ApplicationUser { UserName = "b@example.com", Email = "b@example.com", DisplayName = "B" };
        db.Users.AddRange(owner, other);
        await db.SaveChangesAsync();
        ownerId = owner.Id;
        otherId = other.Id;
        service = new PresentationService(factory, new ContentSanitizer(), time);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private async Task<PresentationDocument> NewDeckAsync()
    {
        var created = await service.CreateAsync(ownerId, "Deck");
        return (await service.GetAsync(created.Id, ownerId))!;
    }

    [Fact]
    public async Task Create_StartsWithATitleSlide()
    {
        var deck = await NewDeckAsync();

        var slide = Assert.Single(deck.Slides);
        Assert.Equal(SlideLayout.Title, slide.Layout);
        Assert.Equal("Deck", slide.Content.Title);
    }

    [Fact]
    public async Task Instructors_OnlySeeTheirOwnDecks()
    {
        var deck = await NewDeckAsync();

        Assert.Null(await service.GetAsync(deck.Id, otherId));
        Assert.Empty(await service.ListAsync(otherId));
        Assert.False(await service.SaveAsync(deck, otherId));
        Assert.False(await service.DeleteAsync(deck.Id, otherId, isAdmin: false));
    }

    [Fact]
    public async Task OnlyAnAdmin_CanListEveryonesDecks()
    {
        await service.CreateAsync(ownerId, "Mine");
        await service.CreateAsync(otherId, "Theirs");

        var instructorAskingForAll = await service.ListAsync(ownerId, isAdmin: false, everyone: true);
        var adminAskingForAll = await service.ListAsync(ownerId, isAdmin: true, everyone: true);
        var adminsOwn = await service.ListAsync(ownerId, isAdmin: true);

        Assert.Equal(["Mine"], instructorAskingForAll.Select(p => p.Title));
        Assert.Equal(["Mine", "Theirs"], adminAskingForAll.Select(p => p.Title).Order());
        Assert.Equal("B", adminAskingForAll.Single(p => p.Title == "Theirs").OwnerName);
        Assert.Equal(["Mine"], adminsOwn.Select(p => p.Title));
    }

    [Fact]
    public async Task Admin_CanDeleteAnyDeck()
    {
        var deck = await NewDeckAsync();

        Assert.True(await service.DeleteAsync(deck.Id, otherId, isAdmin: true));
    }

    [Fact]
    public async Task Save_SanitizesTextAndKeepsSlideOrder()
    {
        var deck = await NewDeckAsync();
        deck.Slides.Add(new EditableSlide
        {
            Layout = SlideLayout.TwoColumns,
            SpeakerNotes = "Remember the demo",
            Content = new SlideContent { Body = "<p>One<script>x()</script></p>", Body2 = "<img src=x onerror=y()>Two" },
        });

        await service.SaveAsync(deck, ownerId);
        var saved = (await service.GetAsync(deck.Id, ownerId))!;

        Assert.Equal(2, saved.Slides.Count);
        Assert.Equal("<p>One</p>", saved.Slides[1].Content.Body);
        Assert.Equal("Two", saved.Slides[1].Content.Body2);
        Assert.Equal("Remember the demo", saved.Slides[1].SpeakerNotes);
    }

    [Fact]
    public async Task Save_RejectsImagesFromOtherSites()
    {
        var deck = await NewDeckAsync();
        deck.Slides[0].Content.ImageUrl = "https://tracker.example.com/pixel.png";

        await service.SaveAsync(deck, ownerId);

        Assert.Null((await service.GetAsync(deck.Id, ownerId))!.Slides[0].Content.ImageUrl);
    }

    [Fact]
    public async Task Save_KeepsOurOwnImages()
    {
        var deck = await NewDeckAsync();
        var url = $"media/{new string('a', 32)}.png";
        deck.Slides[0].Content.ImageUrl = url;

        await service.SaveAsync(deck, ownerId);

        Assert.Equal(url, (await service.GetAsync(deck.Id, ownerId))!.Slides[0].Content.ImageUrl);
    }

    [Fact]
    public async Task Save_FallsBackFromUnknownThemeAndBackground()
    {
        var deck = await NewDeckAsync();
        deck.ThemeId = "hacked";
        deck.Slides[0].Content.Background = "\" onload=\"x()";

        await service.SaveAsync(deck, ownerId);
        var saved = (await service.GetAsync(deck.Id, ownerId))!;

        Assert.Equal(SlideThemes.DefaultTheme, saved.ThemeId);
        Assert.Equal(SlideThemes.DefaultBackground, saved.Slides[0].Content.Background);
    }

    [Fact]
    public async Task Save_ReplacesSlidesWithoutLeavingOrphans()
    {
        var deck = await NewDeckAsync();
        deck.Slides.Add(new EditableSlide());
        await service.SaveAsync(deck, ownerId);
        deck.Slides.RemoveAt(1);
        await service.SaveAsync(deck, ownerId);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Slides.CountAsync());
    }
}

public sealed class ImageServiceTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string folder = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalImageStore store;
    private readonly ImageService service;

    public ImageServiceTests()
    {
        store = new LocalImageStore(folder);
        service = new ImageService(store);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, null)]
    [InlineData(new byte[0], null)]
    public void DetectExtension_ReadsTheFileItself(byte[] data, string? expected) =>
        Assert.Equal(expected, ImageService.DetectExtension(data));

    [Fact]
    public async Task Upload_StoresAnImageAndReturnsAnOwnUrl()
    {
        var result = await service.UploadAsync(new MemoryStream(Png));

        Assert.Null(result.Error);
        Assert.Equal(result.Url, ImageService.OwnImageOrNull(result.Url));

        var stored = await store.OpenAsync(result.Url![ImageService.RoutePrefix.Length..], default);
        await using var content = stored!.Content;
        Assert.Equal("image/png", stored.ContentType);
    }

    [Fact]
    public async Task Upload_RejectsSvgEvenThoughItLooksLikeAnImage()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        var result = await service.UploadAsync(new MemoryStream(svg));

        Assert.Null(result.Url);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Upload_RejectsFilesOverTheLimit()
    {
        var big = new byte[ImageService.MaxBytes + 1];
        Png.CopyTo(big, 0);

        var result = await service.UploadAsync(new MemoryStream(big));

        Assert.Contains("5 MB", result.Error);
    }

    [Theory]
    [InlineData("../../secrets.png")]
    [InlineData("..\\web.config")]
    [InlineData("abc.png")]
    [InlineData("0123456789abcdef0123456789abcdef.svg")]
    public void StoredNames_CannotEscapeTheFolder(string name) =>
        Assert.False(ImageService.IsStoredName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com/a.png")]
    [InlineData("media/../secrets.png")]
    [InlineData("//evil.example/a.png")]
    public void OwnImageOrNull_DropsAnythingElse(string? url) =>
        Assert.Null(ImageService.OwnImageOrNull(url));
}
