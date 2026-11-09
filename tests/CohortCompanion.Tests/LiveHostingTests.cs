using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Live;
using CohortCompanion.Web.Presentations;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

/// <summary>What the instructor's host page relies on: resuming, the projector link, and ending a finished quiz.</summary>
public sealed class LiveHostingTests : LiveSessionTestBase
{
    private static readonly AnswerPayload Correct = new(Options: [0]);

    private static PresentationDocument Deck() => new()
    {
        Id = 7,
        Title = "Deck",
        Slides = [new EditableSlide { Layout = SlideLayout.Title, Content = new SlideContent { Title = "Hello" } }],
    };

    [Fact]
    public async Task RunningAQuizAgain_ResumesTheSessionAlreadyRunning()
    {
        var quiz = SampleQuiz();

        var (first, firstResumed) = await Manager.StartOrResumeQuizAsync(quiz, HostId);
        var (second, secondResumed) = await Manager.StartOrResumeQuizAsync(quiz, HostId);

        Assert.False(firstResumed);
        Assert.True(secondResumed);
        Assert.Equal(first, second);
        Assert.Single(Manager.ListLive(HostId));
    }

    [Fact]
    public async Task RunningAQuizAgain_AfterItEnded_StartsAFreshSession()
    {
        var quiz = SampleQuiz();
        var (first, _) = await Manager.StartOrResumeQuizAsync(quiz, HostId);
        await Manager.EndAsync(first!.Value, HostId);

        var (second, resumed) = await Manager.StartOrResumeQuizAsync(quiz, HostId);

        Assert.False(resumed);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task AnotherQuiz_GetsItsOwnSession()
    {
        var other = SampleQuiz();
        other.Id = 2;
        var (first, _) = await Manager.StartOrResumeQuizAsync(SampleQuiz(), HostId);

        var (second, resumed) = await Manager.StartOrResumeQuizAsync(other, HostId);

        Assert.False(resumed);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task PresentingAgain_ResumesTheSessionAlreadyRunning()
    {
        var (first, _) = await Manager.StartOrResumePresentationAsync(Deck(), HostId);

        var (second, resumed) = await Manager.StartOrResumePresentationAsync(Deck(), HostId);

        Assert.True(resumed);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Resuming_SurvivesARestart()
    {
        var quiz = SampleQuiz();
        var (first, _) = await Manager.StartOrResumeQuizAsync(quiz, HostId);

        var restarted = NewManager();
        await restarted.RecoverAsync();

        Assert.Equal((first, true), await restarted.StartOrResumeQuizAsync(quiz, HostId));
    }

    [Fact]
    public async Task ProjectorKey_OpensTheProjectorWithoutSigningIn()
    {
        var (id, _) = await StartSessionAsync(SampleQuiz());
        var key = (await Manager.GetHostViewAsync(id, HostId))!.ProjectorKey;

        Assert.NotEmpty(key);
        Assert.NotNull(await Manager.WatchProjectorAsync(id, null, "room-pc", key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-key")]
    public async Task Projector_WithoutTheKeyOrTheHost_IsRefused(string? key)
    {
        var (id, _) = await StartSessionAsync(SampleQuiz());

        Assert.Null(await Manager.WatchProjectorAsync(id, null, "room-pc", key));
        Assert.Null(await Manager.WatchProjectorAsync(id, "someone-else", "room-pc", key));
    }

    [Fact]
    public async Task ProjectorKey_OnlyOpensItsOwnSession()
    {
        var (first, _) = await StartSessionAsync(SampleQuiz());
        var other = SampleQuiz();
        other.Id = 2;
        var (second, _) = await StartSessionAsync(other);
        var firstKey = (await Manager.GetHostViewAsync(first, HostId))!.ProjectorKey;

        Assert.NotEqual(firstKey, (await Manager.GetHostViewAsync(second, HostId))!.ProjectorKey);
        Assert.Null(await Manager.WatchProjectorAsync(second, null, "room-pc", firstKey));
    }

    [Fact]
    public async Task ProjectorKey_SurvivesARestart()
    {
        var (id, _) = await StartSessionAsync(SampleQuiz());
        var key = (await Manager.GetHostViewAsync(id, HostId))!.ProjectorKey;

        var restarted = NewManager();
        await restarted.RecoverAsync();

        Assert.NotNull(await restarted.WatchProjectorAsync(id, null, "room-pc", key));
    }

    [Fact]
    public async Task CloseAndShowResults_IsOneStep()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Assert.False(await Manager.CloseAndShowResultsAsync(id, HostId));
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);

        Assert.True(await Manager.CloseAndShowResultsAsync(id, HostId));

        Assert.Equal(QuestionPhase.ShowingResults, Broadcaster.LastPhase.Phase);
        Assert.Equal(1, Broadcaster.LastPhase.Results!.ResponseCount);
        Assert.Equal("closed", (await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Reason);
        Assert.True(await Manager.RevealAnswerAsync(id, HostId));
    }

    [Fact]
    public async Task FinishedQuiz_EndsByItself_AfterAWhile()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);
        await Manager.CloseAndShowResultsAsync(id, HostId);
        await Manager.NextAsync(id, HostId);

        var finished = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Equal(QuestionPhase.Finished, finished.Phase);
        Assert.Equal(Time.GetUtcNow().UtcDateTime + LiveSessionManager.EndAfterFinishing, finished.EndsByItselfUtc);

        Time.Advance(LiveSessionManager.EndAfterFinishing - TimeSpan.FromSeconds(1));
        await Manager.TickAsync();
        Assert.NotNull(await Manager.GetHostViewAsync(id, HostId));

        Time.Advance(TimeSpan.FromSeconds(1));
        await Manager.TickAsync();
        Assert.Null(await Manager.GetHostViewAsync(id, HostId));

        await using var db = Db.CreateDbContext();
        Assert.Equal(SessionStatus.Ended, (await db.LiveSessions.SingleAsync()).Status);
        Assert.Empty(await db.Participants.ToListAsync());
        Assert.Equal(1, await db.SessionSummaries.CountAsync());
        Assert.Contains(Broadcaster.ToStudents, m => m.Method == HubEvents.SessionEnded);
    }

    [Fact]
    public async Task UnfinishedQuiz_HasNoAutomaticEnd()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz());

        Assert.Null((await Manager.GetHostViewAsync(id, HostId))!.EndsByItselfUtc);

        Time.Advance(LiveSessionManager.EndAfterFinishing + TimeSpan.FromMinutes(1));
        await Manager.TickAsync();
        Assert.NotNull(await Manager.GetHostViewAsync(id, HostId));
    }
}
