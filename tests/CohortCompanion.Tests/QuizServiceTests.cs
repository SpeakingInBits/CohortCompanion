using CohortCompanion.Shared;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Quizzes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class QuizServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private QuizService service = default!;
    private string ownerId = default!;
    private string otherId = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);

        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var owner = new ApplicationUser { UserName = "a@example.com", Email = "a@example.com", DisplayName = "A" };
        var other = new ApplicationUser { UserName = "b@example.com", Email = "b@example.com", DisplayName = "B" };
        db.Users.AddRange(owner, other);
        await db.SaveChangesAsync();
        ownerId = owner.Id;
        otherId = other.Id;

        service = new QuizService(factory, new ContentSanitizer(), time);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task Instructors_OnlySeeTheirOwnQuizzes()
    {
        var quiz = await service.CreateAsync(ownerId, "Mine");

        Assert.Null(await service.GetAsync(quiz.Id, otherId));
        Assert.Empty(await service.ListAsync(otherId));
        Assert.Single(await service.ListAsync(ownerId));
    }

    [Fact]
    public async Task OnlyAnAdmin_CanListEveryonesQuizzes()
    {
        await service.CreateAsync(ownerId, "Mine");
        await service.CreateAsync(otherId, "Theirs");

        var instructorAskingForAll = await service.ListAsync(ownerId, isAdmin: false, everyone: true);
        var adminAskingForAll = await service.ListAsync(ownerId, isAdmin: true, everyone: true);
        var adminsOwn = await service.ListAsync(ownerId, isAdmin: true);

        Assert.Equal(["Mine"], instructorAskingForAll.Select(q => q.Title));
        Assert.Equal(["Mine", "Theirs"], adminAskingForAll.Select(q => q.Title).Order());
        Assert.Equal("B", adminAskingForAll.Single(q => q.Title == "Theirs").OwnerName);
        Assert.Equal(["Mine"], adminsOwn.Select(q => q.Title));
    }

    [Fact]
    public async Task Admins_CannotOpenOrChangeAnotherInstructorsQuiz()
    {
        var quiz = await service.CreateAsync(otherId, "Theirs");

        // Being an admin lets someone list and delete. It never lets them read or rewrite the questions.
        Assert.Null(await service.GetAsync(quiz.Id, ownerId));
        Assert.False(await service.SaveAsync(quiz, ownerId));
    }

    [Fact]
    public async Task List_SaysWhichQuizzesCanRunLive()
    {
        var draft = await service.CreateAsync(ownerId, "Draft");
        var ready = await service.CreateAsync(ownerId, "Ready");
        ready.Questions.Add(new Question { Type = QuestionType.WordCloud, Prompt = "<p>One word</p>", MaxEntries = 1 });
        await service.SaveAsync(ready, ownerId);

        var list = await service.ListAsync(ownerId);

        Assert.False(list.Single(q => q.Id == draft.Id).IsReady);
        Assert.True(list.Single(q => q.Id == ready.Id).IsReady);
        Assert.Equal(1, list.Single(q => q.Id == ready.Id).QuestionCount);
        Assert.All(await service.ListAsync(ownerId, isAdmin: true, everyone: true), q => Assert.Null(q.IsReady));
    }

    [Fact]
    public async Task Save_RejectsAnotherInstructorsQuiz()
    {
        var quiz = await service.CreateAsync(ownerId, "Mine");
        quiz.Title = "Stolen";

        Assert.False(await service.SaveAsync(quiz, otherId));
        Assert.Equal("Mine", (await service.GetAsync(quiz.Id, ownerId))!.Title);
    }

    [Fact]
    public async Task Save_SanitizesPromptAndKeepsQuestionOrder()
    {
        var quiz = await service.CreateAsync(ownerId, "Quiz");
        quiz.Questions =
        [
            new Question { Order = 1, Type = QuestionType.WordCloud, Prompt = "Second" },
            new Question
            {
                Order = 0,
                Type = QuestionType.MultipleChoice,
                Prompt = "<p onclick=\"x()\">First<script>alert(1)</script></p>",
                Options = [new ChoiceOption { Text = "A", IsCorrect = true }, new ChoiceOption { Order = 1, Text = "B" }],
            },
        ];

        await service.SaveAsync(quiz, ownerId);
        var saved = (await service.GetAsync(quiz.Id, ownerId))!;

        Assert.Equal("<p>First</p>", saved.Questions[0].Prompt);
        Assert.Equal(2, saved.Questions[0].Options.Count);
        Assert.Equal("Second", saved.Questions[1].Prompt);
    }

    [Fact]
    public async Task Save_ReplacesQuestionsWithoutLeavingOrphans()
    {
        var quiz = await service.CreateAsync(ownerId, "Quiz");
        quiz.Questions = [new Question { Type = QuestionType.MultipleChoice, Prompt = "Q", Options = [new ChoiceOption { Text = "A" }] }];
        await service.SaveAsync(quiz, ownerId);

        var loaded = (await service.GetAsync(quiz.Id, ownerId))!;
        loaded.Questions.Clear();
        await service.SaveAsync(loaded, ownerId);

        await using var db = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(0, await db.Questions.CountAsync());
        Assert.Equal(0, await db.Set<ChoiceOption>().CountAsync());
    }

    [Fact]
    public async Task Save_DropsSpeedBonusWhenScoringIsOff()
    {
        var quiz = await service.CreateAsync(ownerId, "Quiz");
        quiz.ScoringEnabled = false;
        quiz.SpeedBonusEnabled = true;
        quiz.ShowLeaderboard = true;

        await service.SaveAsync(quiz, ownerId);
        var saved = (await service.GetAsync(quiz.Id, ownerId))!;

        Assert.False(saved.SpeedBonusEnabled);
        Assert.False(saved.ShowLeaderboard);
    }

    [Fact]
    public async Task Delete_OnlyOwnerOrAdmin()
    {
        var quiz = await service.CreateAsync(ownerId, "Quiz");

        Assert.False(await service.DeleteAsync(quiz.Id, otherId, isAdmin: false));
        Assert.True(await service.DeleteAsync(quiz.Id, otherId, isAdmin: true));
    }
}
