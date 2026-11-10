using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Live;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class AnswerScorerTests
{
    private static QuestionSnapshot Choice(bool partial = false, int? limit = null, params bool[] correct) => new(
        1,
        QuestionType.MultipleChoice,
        "Q",
        null,
        null,
        null,
        limit,
        1000,
        correct.Count(c => c) > 1,
        partial,
        1,
        0,
        correct.Select((c, i) => new OptionSnapshot($"O{i}", null, c)).ToList(),
        []);

    private static QuestionSnapshot Blanks(params BlankAnswerSnapshot[] accepted) => new(
        1,
        QuestionType.FillInBlank,
        "{{1}} {{2}}",
        null,
        null,
        null,
        null,
        1000,
        false,
        false,
        1,
        2,
        [],
        accepted);

    private static ScoredAnswer Score(QuestionSnapshot question, AnswerPayload answer, bool scoring = true, bool speed = false, int elapsedMs = 0) =>
        AnswerScorer.Score(question, answer, question.BlankAnswers, scoring, speed, elapsedMs);

    [Fact]
    public void Correct_EarnsFullPoints() =>
        Assert.Equal(new ScoredAnswer(true, 1000), Score(Choice(correct: [true, false]), new AnswerPayload(Options: [0])));

    [Fact]
    public void Wrong_EarnsNothing() =>
        Assert.Equal(new ScoredAnswer(false, 0), Score(Choice(correct: [true, false]), new AnswerPayload(Options: [1])));

    [Fact]
    public void ScoringOff_StillMarksTheAnswerButGivesNoPoints() =>
        Assert.Equal(new ScoredAnswer(true, 0), Score(Choice(correct: [true, false]), new AnswerPayload(Options: [0]), scoring: false));

    [Fact]
    public void MultipleSelect_WithoutPartialCredit_IsAllOrNothing() =>
        Assert.Equal(new ScoredAnswer(false, 0), Score(Choice(correct: [true, true, false]), new AnswerPayload(Options: [0])));

    [Fact]
    public void PartialCredit_GivesAShareForEachCorrectOption() =>
        Assert.Equal(new ScoredAnswer(false, 500), Score(Choice(partial: true, correct: [true, true, false]), new AnswerPayload(Options: [0])));

    [Fact]
    public void PartialCredit_WrongPicksCancelRightOnes() =>
        Assert.Equal(new ScoredAnswer(false, 0), Score(Choice(partial: true, correct: [true, true, false]), new AnswerPayload(Options: [0, 2])));

    [Fact]
    public void PartialCredit_TickingEverythingEarnsLessThanFull() =>
        Assert.Equal(500, Score(Choice(partial: true, correct: [true, true, false]), new AnswerPayload(Options: [0, 1, 2])).Score);

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(5000, 750)]
    [InlineData(10000, 500)]
    [InlineData(99000, 500)]
    public void SpeedBonus_FollowsThePlanFormula(int elapsedMs, int expected) =>
        Assert.Equal(expected, Score(Choice(limit: 10, correct: [true, false]), new AnswerPayload(Options: [0]), speed: true, elapsedMs: elapsedMs).Score);

    [Fact]
    public void SpeedBonus_NeedsATimeLimit() =>
        Assert.Equal(1000, Score(Choice(correct: [true, false]), new AnswerPayload(Options: [0]), speed: true, elapsedMs: 9000).Score);

    [Fact]
    public void SpeedBonus_NeverRewardsAWrongAnswer() =>
        Assert.Equal(0, Score(Choice(limit: 10, correct: [true, false]), new AnswerPayload(Options: [1]), speed: true).Score);

    [Fact]
    public void Blanks_SplitPointsEvenly()
    {
        var question = Blanks(new BlankAnswerSnapshot(1, "cat", MatchMode.CaseInsensitive), new BlankAnswerSnapshot(2, "dog", MatchMode.CaseInsensitive));

        Assert.Equal(new ScoredAnswer(true, 1000), Score(question, new AnswerPayload(Blanks: ["Cat", "dog"])));
        Assert.Equal(new ScoredAnswer(false, 500), Score(question, new AnswerPayload(Blanks: ["cat", "bird"])));
    }

    [Fact]
    public void WordCloud_IsNeverScored()
    {
        var question = Choice(correct: [true]) with { Type = QuestionType.WordCloud };

        Assert.Equal(new ScoredAnswer(null, 0), Score(question, new AnswerPayload(Entries: ["word"])));
    }
}

public sealed class TimedQuizTests : LiveSessionTestBase
{
    private static readonly AnswerPayload Correct = new(Options: [0]);

    private Quiz Timed(params Question[] questions)
    {
        var quiz = SampleQuiz(questions);
        quiz.Mode = QuizMode.Timed;
        quiz.DefaultTimeLimitSeconds = 10;
        quiz.ResultsDisplaySeconds = 5;
        return quiz;
    }

    private Quiz Scored(params Question[] questions)
    {
        var quiz = SampleQuiz(questions);
        quiz.ScoringEnabled = true;
        quiz.ShowLeaderboard = true;
        return quiz;
    }

    private async Task<QuestionPhase> PhaseAsync(Guid id) => (await Manager.GetHostViewAsync(id, HostId))!.Phase;

    /// <summary>Starts a timed quiz and lets its countdown run out, so the first question is open.</summary>
    private async Task<(Guid Id, string Code)> RunningTimedAsync(Quiz quiz)
    {
        var session = await RunningSessionAsync(quiz);
        await GetReadyAsync();
        return session;
    }

    private async Task GetReadyAsync()
    {
        Time.Advance(LiveSessionManager.GetReadyTime);
        await Manager.TickAsync();
    }

    [Fact]
    public async Task Timed_CountsDownBeforeTheFirstQuestion()
    {
        var (id, _) = await RunningSessionAsync(Timed(MultipleChoice()));

        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Equal(QuestionPhase.Waiting, view.Phase);
        Assert.Equal((int)LiveSessionManager.GetReadyTime.TotalMilliseconds, view.RemainingMs);
        Assert.Equal(QuestionPhase.Waiting, Broadcaster.LastPhase.Phase);
        Assert.NotNull(Broadcaster.LastPhase.RemainingMs);
        Assert.Null(Broadcaster.LastPhase.Question);

        Time.Advance(LiveSessionManager.GetReadyTime - TimeSpan.FromMilliseconds(100));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.Waiting, await PhaseAsync(id));

        Time.Advance(TimeSpan.FromMilliseconds(100));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.Open, await PhaseAsync(id));
        Assert.Equal(10_000, (await Manager.GetHostViewAsync(id, HostId))!.RemainingMs);
    }

    [Fact]
    public async Task Skip_DuringTheCountdown_OpensTheQuestion()
    {
        var (id, _) = await RunningSessionAsync(Timed(MultipleChoice()));

        Assert.True(await Manager.SkipAsync(id, HostId));

        Assert.Equal(QuestionPhase.Open, await PhaseAsync(id));
    }

    private async Task AnswerQuestionAsync(Guid id, params (string Connection, AnswerPayload Answer)[] answers)
    {
        await Manager.OpenQuestionAsync(id, HostId);
        foreach (var (connection, answer) in answers)
        {
            await Manager.SubmitAnswerAsync(connection, 1, answer);
        }

        await Manager.CloseQuestionAsync(id, HostId);
        await Manager.ShowResultsAsync(id, HostId);
    }

    [Fact]
    public async Task Timed_RunsThroughEveryQuestionByItself()
    {
        var (id, _) = await RunningTimedAsync(Timed(MultipleChoice(0), MultipleChoice(1)));
        Assert.Equal(QuestionPhase.Open, await PhaseAsync(id));

        Time.Advance(TimeSpan.FromSeconds(11));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.AnswerRevealed, await PhaseAsync(id));

        Time.Advance(TimeSpan.FromSeconds(4));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.AnswerRevealed, await PhaseAsync(id));

        Time.Advance(TimeSpan.FromSeconds(1));
        await Manager.TickAsync();
        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Equal(QuestionPhase.Open, view.Phase);
        Assert.Equal(1, view.QuestionIndex);

        Time.Advance(TimeSpan.FromSeconds(11));
        await Manager.TickAsync();
        Time.Advance(TimeSpan.FromSeconds(5));
        await Manager.TickAsync();
        Assert.Equal(QuestionPhase.Finished, await PhaseAsync(id));
    }

    [Fact]
    public async Task Paused_TimerDoesNotExpire_AndResumesWithTheTimeLeft()
    {
        var (id, _) = await RunningTimedAsync(Timed(MultipleChoice()));
        Time.Advance(TimeSpan.FromSeconds(4));

        Assert.True(await Manager.PauseTimerAsync(id, HostId));
        Time.Advance(TimeSpan.FromMinutes(5));
        await Manager.TickAsync();

        var paused = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.Equal(QuestionPhase.Open, paused.Phase);
        Assert.True(paused.IsPaused);
        Assert.Equal(6000, paused.RemainingMs);

        Assert.True(await Manager.ResumeTimerAsync(id, HostId));
        Assert.Equal(6000, (await Manager.GetHostViewAsync(id, HostId))!.RemainingMs);
    }

    [Fact]
    public async Task PausedTime_DoesNotCostStudentsTheirSpeedBonus()
    {
        var quiz = Timed(MultipleChoice());
        quiz.ScoringEnabled = true;
        quiz.SpeedBonusEnabled = true;
        var (id, code) = await StartSessionAsync(quiz);
        await JoinAsync(code, "Sam");
        await Manager.StartAsync(id, HostId);
        await GetReadyAsync();

        await Manager.PauseTimerAsync(id, HostId);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Manager.ResumeTimerAsync(id, HostId);
        await Manager.SubmitAnswerAsync("Sam", 1, Correct);

        await Manager.SkipAsync(id, HostId);
        var standing = Broadcaster.ToConnections.Last(m => m.Method == HubEvents.LeaderboardUpdated).Argument as Standing;
        Assert.Equal(1000, standing!.Score);
    }

    [Fact]
    public async Task Extend_AddsTime()
    {
        var (id, _) = await RunningTimedAsync(Timed(MultipleChoice()));

        Assert.True(await Manager.ExtendTimerAsync(id, HostId, 20));

        Assert.Equal(30_000, (await Manager.GetHostViewAsync(id, HostId))!.RemainingMs);
    }

    [Fact]
    public async Task Skip_EndsTheQuestionThenMovesOn()
    {
        var (id, _) = await RunningTimedAsync(Timed(MultipleChoice(0), MultipleChoice(1)));

        Assert.True(await Manager.SkipAsync(id, HostId));
        Assert.Equal(QuestionPhase.AnswerRevealed, await PhaseAsync(id));

        Assert.True(await Manager.SkipAsync(id, HostId));
        Assert.Equal(1, (await Manager.GetHostViewAsync(id, HostId))!.QuestionIndex);
    }

    [Fact]
    public async Task Skip_OnlyAppliesToTimedQuizzes()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz());

        Assert.False(await Manager.SkipAsync(id, HostId));
    }

    [Fact]
    public async Task InstructorPaced_TimerOnlyCloses_AndDoesNotAdvance()
    {
        var (id, _) = await RunningSessionAsync(SampleQuiz(MultipleChoice(timeLimit: 10)));
        await Manager.OpenQuestionAsync(id, HostId);

        Time.Advance(TimeSpan.FromMinutes(10));
        await Manager.TickAsync();
        await Manager.TickAsync();

        Assert.Equal(QuestionPhase.Closed, await PhaseAsync(id));
    }

    [Fact]
    public async Task Leaderboard_ShowsTopFiveOnly_AndOnlyFromTheReveal()
    {
        var (id, code) = await RunningSessionAsync(Scored(MultipleChoice()));
        var answers = new List<(string, AnswerPayload)>();
        for (var i = 0; i < 7; i++)
        {
            await JoinAsync(code, $"Student{i}");
            answers.Add(($"Student{i}", Correct));
        }

        await AnswerQuestionAsync(id, [.. answers]);
        var before = Broadcaster.ToProjector.Select(m => m.Argument).OfType<ProjectorState>().Last();
        Assert.Null(before.Leaderboard);

        await Manager.RevealAnswerAsync(id, HostId);
        var after = Broadcaster.ToProjector.Select(m => m.Argument).OfType<ProjectorState>().Last();
        Assert.Equal(5, after.Leaderboard!.Count);
    }

    [Fact]
    public async Task Leaderboard_NeverReachesStudentsAsAGroup()
    {
        var (id, code) = await RunningSessionAsync(Scored(MultipleChoice()));
        await JoinAsync(code, "UniqueSam");
        await AnswerQuestionAsync(id, ("UniqueSam", Correct));
        await Manager.RevealAnswerAsync(id, HostId);

        Assert.DoesNotContain("UniqueSam", ToJson(Broadcaster.ToStudents.Select(m => m.Argument)));
    }

    [Fact]
    public async Task EachStudent_GetsOnlyTheirOwnStanding()
    {
        var (id, code) = await RunningSessionAsync(Scored(MultipleChoice()));
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        await AnswerQuestionAsync(id, ("Sam", Correct), ("Alex", new AnswerPayload(Options: [1])));
        await Manager.RevealAnswerAsync(id, HostId);

        var sam = (Standing)Broadcaster.ToConnections.Single(m => m is { Connection: "Sam", Method: HubEvents.LeaderboardUpdated }).Argument!;
        var alex = (Standing)Broadcaster.ToConnections.Single(m => m is { Connection: "Alex", Method: HubEvents.LeaderboardUpdated }).Argument!;

        Assert.Equal((1000, 1), (sam.Score, sam.Rank!.Value));
        Assert.Equal((0, 2), (alex.Score, alex.Rank!.Value));
        Assert.True(sam.LastAnswerCorrect);
        Assert.False(alex.LastAnswerCorrect);
    }

    [Fact]
    public async Task EqualScores_ShareARank()
    {
        var (id, code) = await RunningSessionAsync(Scored(MultipleChoice()));
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        await AnswerQuestionAsync(id, ("Sam", Correct), ("Alex", Correct));
        await Manager.RevealAnswerAsync(id, HostId);

        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        Assert.All(view.Leaderboard!, entry => Assert.Equal(1, entry.Rank));
    }

    [Fact]
    public async Task ScoresWithoutALeaderboard_ShowNoRank()
    {
        var quiz = Scored(MultipleChoice());
        quiz.ShowLeaderboard = false;
        var (id, code) = await RunningSessionAsync(quiz);
        await JoinAsync(code, "Sam");
        await AnswerQuestionAsync(id, ("Sam", Correct));
        await Manager.RevealAnswerAsync(id, HostId);

        var standing = (Standing)Broadcaster.ToConnections.Single(m => m.Method == HubEvents.LeaderboardUpdated).Argument!;
        Assert.Equal(1000, standing.Score);
        Assert.Null(standing.Rank);
    }

    [Fact]
    public async Task AcceptAnswer_RescoresEveryone_AndSavesIt()
    {
        var quiz = Scored(FillInBlank());
        var (id, code) = await RunningSessionAsync(quiz);
        await JoinAsync(code, "Sam");
        await AnswerQuestionAsync(id, ("Sam", new AnswerPayload(Blanks: ["const", "fixed value"])));
        await Manager.RevealAnswerAsync(id, HostId);

        var before = (Standing)Broadcaster.ToConnections.Last(m => m.Method == HubEvents.LeaderboardUpdated).Argument!;
        Assert.Equal(500, before.Score);

        Assert.True(await Manager.AcceptAnswerAsync(id, HostId, 2, "Fixed Value"));

        var after = (Standing)Broadcaster.ToConnections.Last(m => m.Method == HubEvents.LeaderboardUpdated).Argument!;
        Assert.Equal(1000, after.Score);

        await Manager.NextAsync(id, HostId);
        await using var db = Db.CreateDbContext();
        Assert.Equal(1000, (await db.Responses.SingleAsync()).Score);
    }

    [Fact]
    public async Task End_KeepsAverageScoreInTheSummary()
    {
        var (id, code) = await RunningSessionAsync(Scored(MultipleChoice()));
        await JoinAsync(code, "Sam");
        await JoinAsync(code, "Alex");
        await AnswerQuestionAsync(id, ("Sam", Correct), ("Alex", new AnswerPayload(Options: [1])));

        await Manager.EndAsync(id, HostId);

        await using var db = Db.CreateDbContext();
        Assert.Contains("\"averageScore\":500", (await db.SessionSummaries.SingleAsync()).AggregateJson);
    }
}
