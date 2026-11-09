using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Live;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class LiveSessionManagerTests : LiveSessionTestBase
{
    private static readonly AnswerPayload Correct = new(Options: [0]);

    [Fact]
    public async Task InvalidQuiz_CannotStart()
    {
        var quiz = SampleQuiz();
        quiz.Questions[0].Options.ForEach(o => o.IsCorrect = false);

        Assert.Null(await Manager.CreateQuizSessionAsync(quiz, HostId));
    }

    [Fact]
    public async Task AnotherInstructorsQuiz_CannotStart() =>
        Assert.Null(await Manager.CreateQuizSessionAsync(SampleQuiz(), "someone-else"));

    [Fact]
    public async Task JoinCode_IsSixDigits()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());

        Assert.Matches("^[1-9][0-9]{5}$", code);
    }

    [Theory]
    [InlineData("x", JoinError.NicknameInvalid)]
    [InlineData("  ", JoinError.NicknameInvalid)]
    [InlineData("ThisNicknameIsFarTooLongToUse", JoinError.NicknameInvalid)]
    [InlineData("shithead", JoinError.NicknameNotAllowed)]
    public async Task Join_RejectsBadNicknames(string nickname, JoinError expected)
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());

        Assert.Equal(expected, (await Manager.JoinAsync(code, nickname, "c1")).Error);
    }

    [Fact]
    public async Task Join_NicknamesAreUniqueIgnoringCase()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Assert.Equal(JoinError.NicknameTaken, (await Manager.JoinAsync(code, "sam", "c2")).Error);
    }

    [Fact]
    public async Task Join_UnknownCode_IsNotFound() =>
        Assert.Equal(JoinError.SessionNotFound, (await Manager.JoinAsync("000000", "Sam", "c1")).Error);

    [Fact]
    public async Task Join_StoresOnlyAHashOfTheToken()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam");

        await using var db = Db.CreateDbContext();
        var row = await db.Participants.SingleAsync();
        Assert.NotEqual(token, row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash);
    }

    [Fact]
    public async Task Rejoin_RestoresTheSameParticipantAndTheirAnswer()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam", "old");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("old", 1, Correct);

        await Manager.ConnectionClosedAsync("old");
        var state = await Manager.RejoinAsync(token, "new");

        Assert.Equal("Sam", state!.Nickname);
        Assert.NotNull(state.MyAnswer);
        Assert.Single((await Manager.GetHostViewAsync(id, HostId))!.Participants);
    }

    [Fact]
    public async Task Rejoin_BadToken_ReturnsNull() =>
        Assert.Null(await Manager.RejoinAsync("not-a-token", "c1"));

    [Fact]
    public async Task Leave_TakesTheStudentOut_AndTheirTokenStopsWorking()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);

        await Manager.LeaveAsync(token);

        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Equal(["Alex"], view.Participants.Select(p => p.Nickname));
        Assert.Equal(0, view.ResponseCount);
        Assert.Null(await Manager.RejoinAsync(token, "again"));
        Assert.DoesNotContain(Broadcaster.ToConnections, m => m.Method == HubEvents.ParticipantRemoved);
        Assert.Equal("not-joined", (await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Reason);
    }

    [Fact]
    public async Task Leave_FreesTheNickname()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam");

        await Manager.LeaveAsync(token);

        Assert.Equal(JoinError.None, (await Manager.JoinAsync(code, "Sam", "c2")).Error);
    }

    [Fact]
    public async Task Leave_BadToken_DoesNothing()
    {
        var (id, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        await Manager.LeaveAsync("not-a-token");

        Assert.Single((await Manager.GetHostViewAsync(id, HostId))!.Participants);
    }

    [Fact]
    public async Task ChangeNickname_WorksInTheLobby()
    {
        var (id, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Assert.Equal(JoinError.None, await Manager.ChangeNicknameAsync("Sam", "  Samwise "));

        Assert.Equal("Samwise", (await Manager.GetHostViewAsync(id, HostId))!.Participants.Single().Nickname);
        Assert.Contains(Broadcaster.ToConnections, m => m.Connection == "Sam" && m.Method == HubEvents.NicknameChanged && (string)m.Argument! == "Samwise");
    }

    [Fact]
    public async Task ChangeNickname_FollowsTheJoinRules()
    {
        var (_, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");

        Assert.Equal(JoinError.NicknameTaken, await Manager.ChangeNicknameAsync("Sam", "alex"));
        Assert.Equal(JoinError.NicknameInvalid, await Manager.ChangeNicknameAsync("Sam", "x"));
        Assert.Equal(JoinError.NicknameNotAllowed, await Manager.ChangeNicknameAsync("Sam", "shithead"));
        Assert.Equal(JoinError.None, await Manager.ChangeNicknameAsync("Sam", "sam"));
    }

    [Fact]
    public async Task ChangeNickname_IsRefusedOnceTheSessionStarts()
    {
        var (id, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.StartAsync(id, HostId);

        Assert.Equal(JoinError.SessionStarted, await Manager.ChangeNicknameAsync("Sam", "Samwise"));
        Assert.Equal("Sam", (await Manager.GetHostViewAsync(id, HostId))!.Participants.Single().Nickname);
    }

    [Fact]
    public async Task ChangeNickname_FromAConnectionThatHasNotJoined_IsNotFound() =>
        Assert.Equal(JoinError.SessionNotFound, await Manager.ChangeNicknameAsync("stranger", "Sam"));

    [Fact]
    public async Task Phases_MustRunInOrder()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz());

        Assert.False(await Manager.CloseQuestionAsync(id, HostId));
        Assert.False(await Manager.ShowResultsAsync(id, HostId));
        Assert.True(await Manager.OpenQuestionAsync(id, HostId));
        Assert.False(await Manager.OpenQuestionAsync(id, HostId));
        Assert.False(await Manager.RevealAnswerAsync(id, HostId));
        Assert.True(await Manager.CloseQuestionAsync(id, HostId));
        Assert.True(await Manager.ShowResultsAsync(id, HostId));
        Assert.True(await Manager.RevealAnswerAsync(id, HostId));
        Assert.True(await Manager.NextAsync(id, HostId));
        Assert.Equal(QuestionPhase.Finished, Broadcaster.LastPhase.Phase);
    }

    [Fact]
    public async Task Commands_FromAnotherInstructor_AreRejected()
    {
        var (id, _) = await StartSessionAsync(SampleQuiz());

        Assert.False(await Manager.StartAsync(id, "someone-else"));
        Assert.Null(await Manager.GetHostViewAsync(id, "someone-else"));
        Assert.Null(await Manager.WatchProjectorAsync(id, "someone-else", "c1"));
    }

    [Fact]
    public async Task Submit_OnlyWhileOpen_AndOnlyOnce()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Assert.False((await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Accepted);

        await Manager.OpenQuestionAsync(id, HostId);
        Assert.True((await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Accepted);
        Assert.Equal("already-answered", (await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Reason);

        await Manager.CloseQuestionAsync(id, HostId);
        Assert.Equal(1, (await Manager.GetHostViewAsync(id, HostId))!.ResponseCount);
    }

    [Theory]
    [InlineData(new[] { 7 })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new int[0])]
    public async Task Submit_RejectsInvalidOptions(int[] options)
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);

        Assert.Equal("invalid", (await Manager.SubmitAnswerAsync("Sam", 1, new AnswerPayload(Options: options))).Reason);
    }

    [Fact]
    public async Task Submit_WithoutJoining_IsRejected() =>
        Assert.Equal("not-joined", (await Manager.SubmitAnswerAsync("stranger", 1, Correct)).Reason);

    [Fact]
    public async Task StudentsNeverReceiveCorrectAnswers_BeforeTheReveal()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);
        await Manager.CloseQuestionAsync(id, HostId);
        await Manager.ShowResultsAsync(id, HostId);

        var json = ToJson(Broadcaster.ToStudents.Select(m => m.Argument));

        Assert.DoesNotContain("\"isCorrect\":true", json);
        Assert.DoesNotContain("\"isCorrect\":false", json);

        await Manager.RevealAnswerAsync(id, HostId);
        Assert.Contains("\"isCorrect\":true", ToJson(Broadcaster.LastPhase));
    }

    [Fact]
    public async Task Results_StayHiddenFromStudentsUntilShown()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);

        Assert.Null(Broadcaster.LastPhase.Results);

        await Manager.CloseQuestionAsync(id, HostId);
        Assert.Null(Broadcaster.LastPhase.Results);

        await Manager.ShowResultsAsync(id, HostId);
        Assert.Equal(1, Broadcaster.LastPhase.Results!.Options![0].Count);
    }

    [Fact]
    public async Task QuestionStaysHiddenFromStudentsUntilOpened()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");

        Assert.Null(Broadcaster.LastPhase.Question);

        await Manager.OpenQuestionAsync(id, HostId);
        Assert.NotNull(Broadcaster.LastPhase.Question);
    }

    [Fact]
    public async Task Answers_AreNotBroadcastToStudents()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        for (var i = 0; i < 30; i++)
        {
            await JoinAsync(code, $"Student{i}");
        }

        await Manager.OpenQuestionAsync(id, HostId);
        var before = Broadcaster.ToStudents.Count;

        for (var i = 0; i < 30; i++)
        {
            await Manager.SubmitAnswerAsync($"Student{i}", 1, Correct);
        }

        Assert.Equal(before, Broadcaster.ToStudents.Count);
    }

    [Fact]
    public async Task ProjectorUpdates_AreThrottledToTwiceASecond()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        for (var i = 0; i < 20; i++)
        {
            await JoinAsync(code, $"Student{i}");
        }

        await Manager.OpenQuestionAsync(id, HostId);
        Time.Advance(TimeSpan.FromSeconds(1));
        var before = Broadcaster.ToProjector.Count;

        for (var i = 0; i < 20; i++)
        {
            await Manager.SubmitAnswerAsync($"Student{i}", 1, Correct);
            await Manager.TickAsync();
            Time.Advance(TimeSpan.FromMilliseconds(50));
        }

        // 20 answers over a second: at most two or three pushes, not twenty.
        Assert.InRange(Broadcaster.ToProjector.Count - before, 1, 3);
    }

    [Fact]
    public async Task Timer_ClosesTheQuestionAfterTheGracePeriod()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz(MultipleChoice(timeLimit: 10)));
        await Manager.OpenQuestionAsync(id, HostId);

        Time.Advance(TimeSpan.FromSeconds(10));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.Open, (await Manager.GetHostViewAsync(id, HostId))!.Phase);

        Time.Advance(LiveSessionManager.DeadlineGrace);
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.Closed, (await Manager.GetHostViewAsync(id, HostId))!.Phase);
    }

    [Fact]
    public async Task Submit_WithinTheGracePeriod_IsAccepted_ButNotAfter()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz(MultipleChoice(timeLimit: 10)));
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        await Manager.OpenQuestionAsync(id, HostId);

        Time.Advance(TimeSpan.FromMilliseconds(10_400));
        Assert.True((await Manager.SubmitAnswerAsync("Sam", 1, Correct)).Accepted);

        Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False((await Manager.SubmitAnswerAsync("Alex", 1, Correct)).Accepted);
    }

    [Fact]
    public async Task RemovedParticipant_IsDisconnectedAndLeavesTheTotals()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);
        var participant = (await Manager.GetHostViewAsync(id, HostId))!.Participants.Single();

        Assert.True(await Manager.RemoveParticipantAsync(id, HostId, participant.Id));

        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Empty(view.Participants);
        Assert.Equal(0, view.ResponseCount);
        Assert.Contains(Broadcaster.ToConnections, m => m.Method == HubEvents.ParticipantRemoved);
        Assert.Null(await Manager.RejoinAsync(token, "new"));
    }

    [Fact]
    public async Task Rename_RejectsDuplicates_AndTellsTheStudent()
    {
        var (id, code) = await StartSessionAsync(SampleQuiz());
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        var sam = (await Manager.GetHostViewAsync(id, HostId))!.Participants.Single(p => p.Nickname == "Sam");

        Assert.False(await Manager.RenameParticipantAsync(id, HostId, sam.Id, "alex"));
        Assert.True(await Manager.RenameParticipantAsync(id, HostId, sam.Id, "Student 1"));
        Assert.Contains(Broadcaster.ToConnections, m => m.Method == HubEvents.NicknameChanged && (string)m.Argument! == "Student 1");
    }

    [Fact]
    public async Task HideWord_RemovesItFromResults()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz(WordCloud()));
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, new AnswerPayload(Entries: ["Fast!", "typed"]));
        await Manager.CloseQuestionAsync(id, HostId);
        await Manager.ShowResultsAsync(id, HostId);

        await Manager.HideWordAsync(id, HostId, "fast");

        var results = Broadcaster.ToStudents.Last(m => m.Method == HubEvents.ResultsUpdated).Argument as QuestionResults;
        Assert.Equal(["typed"], results!.Words!.Select(w => w.Word));
    }

    [Fact]
    public async Task End_KeepsTotalsAndDeletesEveryNicknameAndAnswer()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "UniqueNickname");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("UniqueNickname", 1, Correct);
        await Manager.CloseQuestionAsync(id, HostId);

        Assert.True(await Manager.EndAsync(id, HostId));

        await using var db = Db.CreateDbContext();
        Assert.Equal(0, await db.Participants.CountAsync());
        Assert.Equal(0, await db.Responses.CountAsync());
        var session = await db.LiveSessions.SingleAsync();
        Assert.Equal(SessionStatus.Ended, session.Status);
        var summary = await db.SessionSummaries.SingleAsync();
        Assert.DoesNotContain("UniqueNickname", summary.AggregateJson);
        Assert.Contains("\"count\":1", summary.AggregateJson);

        Assert.Contains(Broadcaster.ToStudents, m => m.Method == HubEvents.SessionEnded);
        Assert.Null(await Manager.RejoinAsync(token, "new"));
        Assert.Equal(JoinError.SessionNotFound, (await Manager.JoinAsync(code, "Late", "c9")).Error);
    }

    [Fact]
    public async Task JoinCode_IsReleasedWhenTheSessionEnds_SoItCanBeReused()
    {
        var (id, code) = await StartSessionAsync(SampleQuiz());
        await Manager.EndAsync(id, HostId);

        await using var db = Db.CreateDbContext();
        var ended = await db.LiveSessions.SingleAsync();
        db.LiveSessions.Add(new Web.Data.LiveSession { Id = Guid.NewGuid(), HostId = HostId, JoinCode = code, Title = "Next" });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Restart_RecoversPhaseParticipantsAndAnswers()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz());
        var token = await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);
        await Manager.CloseQuestionAsync(id, HostId);

        var restarted = NewManager();
        await restarted.RecoverAsync();

        var view = (await restarted.GetHostViewAsync(id, HostId))!;
        Assert.Equal(QuestionPhase.Closed, view.Phase);
        Assert.Equal(1, view.ResponseCount);
        Assert.Equal("Sam", (await restarted.RejoinAsync(token, "new"))!.Nickname);
    }

    [Fact]
    public async Task Snapshot_IsNotAffectedByEditsAfterStarting()
    {
        var quiz = SampleQuiz();
        var (id, _) = await RunningSessionAsync(quiz);

        quiz.Questions[0].Prompt = "<p>Changed</p>";
        await Manager.OpenQuestionAsync(id, HostId);

        Assert.Contains("Which keyword", Broadcaster.LastPhase.Question!.Prompt);
    }

    [Fact]
    public async Task WordCloud_FiltersProfanityOutOfResults()
    {
        var (id, code) = await RunningSessionAsync(SampleQuiz(WordCloud()));
        await JoinAsync(code, "Sam");
        await Manager.OpenQuestionAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, new AnswerPayload(Entries: ["shit", "great"]));
        await Manager.CloseQuestionAsync(id, HostId);
        await Manager.ShowResultsAsync(id, HostId);

        Assert.Equal(["great"], Broadcaster.LastPhase.Results!.Words!.Select(w => w.Word));
    }
}
