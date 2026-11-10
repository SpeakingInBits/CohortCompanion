using System.Diagnostics;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Live;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class AbandonedSessionTests : LiveSessionTestBase
{
    [Fact]
    public async Task IdleSession_IsEndedOnceAbandoned_AndItsStudentDataDeleted()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, new AnswerPayload(Options: [0]));
        await Manager.CloseQuestionAsync(id, HostId);

        Time.Advance(LiveSessionManager.AbandonedAfter);
        await Manager.TickAsync();

        await using var db = Db.CreateDbContext();
        Assert.Equal(0, await db.Participants.CountAsync());
        Assert.Equal(0, await db.Responses.CountAsync());
        Assert.Equal(SessionStatus.Ended, (await db.LiveSessions.SingleAsync()).Status);
        Assert.Equal(1, await db.SessionSummaries.CountAsync());
        Assert.Null(await Manager.GetHostViewAsync(id, HostId));
        Assert.Contains(Broadcaster.ToStudents, m => m.Method == HubEvents.SessionEnded);
    }

    [Fact]
    public async Task AbandonedLobby_IsAlsoCleanedUp()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Time.Advance(LiveSessionManager.AbandonedAfter);
        await Manager.TickAsync();

        await using var db = Db.CreateDbContext();
        Assert.Equal(0, await db.Participants.CountAsync());
    }

    [Fact]
    public async Task ActiveSession_IsNotSwept()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz());

        Time.Advance(LiveSessionManager.AbandonedAfter - TimeSpan.FromMinutes(1));
        await Manager.OpenQuestionAsync(id, HostId);
        Time.Advance(LiveSessionManager.AbandonedAfter - TimeSpan.FromMinutes(1));
        await Manager.TickAsync();

        Assert.NotNull(await Manager.GetHostViewAsync(id, HostId));
    }

    [Fact]
    public async Task SessionAbandonedWhileTheServerWasDown_IsSweptAfterRecovery()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Time.Advance(TimeSpan.FromDays(2));
        var restarted = NewManager();
        await restarted.RecoverAsync();
        await restarted.TickAsync();

        Assert.Null(await restarted.GetHostViewAsync(id, HostId));
        await using var db = Db.CreateDbContext();
        Assert.Equal(0, await db.Participants.CountAsync());
    }
}

public sealed class SessionIsolationTests : LiveSessionTestBase
{
    [Fact]
    public async Task OneStuckClass_DoesNotDelayAnotherClassesTimer()
    {
        var (stuck, _) = await RunningSessionAsync(SampleQuiz());
        var (other, _) = await RunningSessionAsync(SampleQuiz(MultipleChoice(timeLimit: 10)));
        await Manager.OpenQuestionAsync(other, HostId);

        // A command for the first class hangs while holding that class's lock.
        var release = new TaskCompletionSource();
        Broadcaster.Hold = (stuck, release.Task);
        var hung = Manager.OpenQuestionAsync(stuck, HostId);

        Time.Advance(TimeSpan.FromSeconds(11));
        var tick = Manager.TickAsync();
        var finished = await Task.WhenAny(tick, Task.Delay(TimeSpan.FromSeconds(5)));

        Broadcaster.Hold = null;
        release.SetResult();
        await hung;
        await tick;

        Assert.Same(tick, finished);
        Assert.Equal(QuestionPhase.Closed, (await Manager.GetHostViewAsync(other, HostId))!.Phase);
    }
}

public sealed class LargeClassTests : LiveSessionTestBase
{
    /// <summary>
    /// Standings are worked out while holding the session's lock. Rescanning every answer for every student
    /// made this quadratic and took tens of seconds at this size, which would freeze a live class.
    /// </summary>
    [Fact]
    public async Task RevealForALargeClass_StaysFast()
    {
        const int students = 600;
        const int questions = 5;

        var quiz = SampleQuiz(Enumerable.Range(0, questions).Select(i => MultipleChoice(i)).ToArray());
        quiz.ScoringEnabled = true;
        quiz.ShowLeaderboard = true;
        var (id, code) = await RunningSessionAsync(quiz);
        for (var i = 0; i < students; i++)
        {
            await JoinAsync(code, $"Student{i}");
        }

        for (var question = 1; question <= questions; question++)
        {
            await Manager.OpenQuestionAsync(id, HostId);
            for (var i = 0; i < students; i++)
            {
                await Manager.SubmitAnswerAsync($"Student{i}", question, new AnswerPayload(Options: [i % 3]));
            }

            await Manager.CloseQuestionAsync(id, HostId);
            await Manager.ShowResultsAsync(id, HostId);

            if (question < questions)
            {
                await Manager.RevealAnswerAsync(id, HostId);
                await Manager.NextAsync(id, HostId);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        await Manager.RevealAnswerAsync(id, HostId);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Reveal took {stopwatch.Elapsed}.");

        var standings = Broadcaster.ToConnections.Where(m => m.Method == HubEvents.LeaderboardUpdated).TakeLast(students).ToList();
        Assert.Equal(students, standings.Count);
    }
}

public sealed class LiveRateLimiterTests
{
    [Fact]
    public void OneConnection_IsLimited()
    {
        using var limiter = new LiveRateLimiter();

        for (var i = 0; i < LiveRateLimiter.AnswersPerConnection; i++)
        {
            Assert.True(limiter.TryAnswer("c1", "10.0.0.1"));
        }

        Assert.False(limiter.TryAnswer("c1", "10.0.0.1"));
    }

    [Fact]
    public void AWholeClassBehindOneAddress_IsNotLimited()
    {
        using var limiter = new LiveRateLimiter();

        for (var student = 0; student < 400; student++)
        {
            Assert.True(limiter.TryJoin($"c{student}", "10.0.0.1"));
            Assert.True(limiter.TryAnswer($"c{student}", "10.0.0.1"));
        }
    }

    [Fact]
    public void AFloodFromOneAddress_IsLimited()
    {
        using var limiter = new LiveRateLimiter();

        for (var i = 0; i < LiveRateLimiter.JoinsPerAddress; i++)
        {
            Assert.True(limiter.TryJoin($"c{i}", "10.0.0.1"));
        }

        Assert.False(limiter.TryJoin("another", "10.0.0.1"));
        Assert.True(limiter.TryJoin("another", "10.0.0.2"));
    }

    [Fact]
    public void AWholeLectureReconnecting_JustAfterJoining_IsNotLimited()
    {
        using var limiter = new LiveRateLimiter();

        for (var student = 0; student < LiveRateLimiter.JoinsPerAddress; student++)
        {
            Assert.True(limiter.TryJoin($"c{student}", "10.0.0.1"));
        }

        for (var student = 0; student < LiveRateLimiter.JoinsPerAddress; student++)
        {
            Assert.True(limiter.TryRejoin($"new{student}", "10.0.0.1"));
        }
    }

    [Fact]
    public void OneConnection_CannotRejoinEndlessly()
    {
        using var limiter = new LiveRateLimiter();

        for (var i = 0; i < LiveRateLimiter.RejoinsPerConnection; i++)
        {
            Assert.True(limiter.TryRejoin("c1", "10.0.0.1"));
        }

        Assert.False(limiter.TryRejoin("c1", "10.0.0.1"));
    }

    [Fact]
    public void JoinsAndAnswers_HaveSeparateAllowances()
    {
        using var limiter = new LiveRateLimiter();

        for (var i = 0; i < LiveRateLimiter.JoinsPerConnection; i++)
        {
            limiter.TryJoin("c1", "10.0.0.1");
        }

        Assert.False(limiter.TryJoin("c1", "10.0.0.1"));
        Assert.True(limiter.TryAnswer("c1", "10.0.0.1"));
    }
}

public sealed class SessionHistoryTests : LiveSessionTestBase
{
    [Fact]
    public async Task EndedQuiz_ShowsTotalsToItsHostOnly()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, new AnswerPayload(Options: [0]));
        await Manager.EndAsync(id, HostId);

        var history = new SessionHistoryService(Db);

        var detail = await history.GetAsync(id, HostId);
        Assert.Equal(1, Assert.Single(detail!.Questions).Results.Options![0].Count);
        Assert.Single(await history.ListAsync(HostId));

        Assert.Null(await history.GetAsync(id, "someone-else"));
        Assert.Empty(await history.ListAsync("someone-else"));
    }

    [Fact]
    public async Task LiveSession_IsNotInHistory()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz());

        var history = new SessionHistoryService(Db);

        Assert.Null(await history.GetAsync(id, HostId));
        Assert.Empty(await history.ListAsync(HostId));
    }

    [Fact]
    public async Task EndedPresentation_IsNotInHistory()
    {
        var deck = new CohortCompanion.Web.Presentations.PresentationDocument
        {
            Id = 3,
            Title = "Deck",
            Slides = [new CohortCompanion.Web.Presentations.EditableSlide()],
        };
        var id = (await Manager.CreatePresentationSessionAsync(deck, HostId))!.Value;
        await Manager.EndAsync(id, HostId);

        Assert.Empty(await new SessionHistoryService(Db).ListAsync(HostId));
    }
}
