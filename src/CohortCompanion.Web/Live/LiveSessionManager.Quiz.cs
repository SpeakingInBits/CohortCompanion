using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Live;

/// <summary>
/// The quiz phases: Waiting → Open → Closed → ShowingResults → AnswerRevealed → next question. Timed quizzes
/// run the same phases on their own: when time runs out, results and the answer show briefly, then the
/// next question opens.
/// </summary>
public sealed partial class LiveSessionManager
{
    private static bool IsTimed(LiveRuntime runtime) => runtime.Quiz?.Mode == QuizMode.Timed;

    public Task<bool> StartAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.Status != SessionStatus.Lobby)
            {
                return false;
            }

            runtime.Status = SessionStatus.Running;
            runtime.CurrentIndex = 0;
            EnterPhase(runtime, QuestionPhase.Waiting);

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var now = UtcNow;
                await db.LiveSessions.Where(s => s.Id == runtime.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAtUtc, now));
            }

            if (runtime.Kind == SessionKind.Presentation)
            {
                runtime.Extras.ShownSlides.Add(0);
                await AnnouncePhaseAsync(runtime);
                await AnnounceSlideAsync(runtime);
            }
            else if (IsTimed(runtime))
            {
                // A moment for everyone to look up before the first timed question opens.
                runtime.DeadlineUtc = UtcNow + GetReadyTime;
                await AnnouncePhaseAsync(runtime);
            }
            else
            {
                await AnnouncePhaseAsync(runtime);
            }

            return true;
        });

    public Task<bool> OpenQuestionAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!InPhase(runtime, QuestionPhase.Waiting))
            {
                return false;
            }

            await OpenCurrentQuestionAsync(runtime);
            return true;
        });

    public Task<bool> CloseQuestionAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!InPhase(runtime, QuestionPhase.Open))
            {
                return false;
            }

            await CloseCurrentQuestionAsync(runtime);
            return true;
        });

    /// <summary>Closes the question and shows its results in one step, the usual move in an instructor-paced quiz.</summary>
    public Task<bool> CloseAndShowResultsAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!InPhase(runtime, QuestionPhase.Open))
            {
                return false;
            }

            EnterPhase(runtime, QuestionPhase.ShowingResults);
            await AnnouncePhaseAsync(runtime);
            return true;
        });

    public Task<bool> ShowResultsAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!InPhase(runtime, QuestionPhase.Closed))
            {
                return false;
            }

            EnterPhase(runtime, QuestionPhase.ShowingResults);
            await AnnouncePhaseAsync(runtime);
            return true;
        });

    public Task<bool> RevealAnswerAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!InPhase(runtime, QuestionPhase.ShowingResults))
            {
                return false;
            }

            await RevealAsync(runtime);
            return true;
        });

    /// <summary>Moves to the next question, or to the end of the quiz after the last one.</summary>
    public Task<bool> NextAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.Status != SessionStatus.Running
                || runtime.Kind != SessionKind.Quiz
                || runtime.Phase is QuestionPhase.Open or QuestionPhase.Finished)
            {
                return false;
            }

            await MoveToNextQuestionAsync(runtime);
            return true;
        });

    public Task<bool> PauseTimerAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.DeadlineUtc is not { } deadline || runtime.Extras.PausedRemainingMs is not null)
            {
                return false;
            }

            var now = UtcNow;
            runtime.Extras.PausedRemainingMs = (int)Math.Max(0, (deadline - now).TotalMilliseconds);
            runtime.Extras.PausedAtUtc = now;
            runtime.DeadlineUtc = null;
            await AnnouncePhaseAsync(runtime);
            return true;
        });

    public Task<bool> ResumeTimerAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.Extras.PausedRemainingMs is not { } remaining)
            {
                return false;
            }

            var now = UtcNow;
            runtime.DeadlineUtc = now.AddMilliseconds(remaining);
            // Time spent paused doesn't count against a student's speed bonus.
            if (runtime.Extras.PausedAtUtc is { } pausedAt && runtime.Extras.PhaseStartedUtc is { } started)
            {
                runtime.Extras.PhaseStartedUtc = started + (now - pausedAt);
            }

            runtime.Extras.PausedRemainingMs = null;
            runtime.Extras.PausedAtUtc = null;
            await AnnouncePhaseAsync(runtime);
            return true;
        });

    public Task<bool> ExtendTimerAsync(Guid sessionId, string hostId, int seconds) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (seconds is <= 0 or > 600 || runtime.Phase != QuestionPhase.Open)
            {
                return false;
            }

            if (runtime.Extras.PausedRemainingMs is { } remaining)
            {
                runtime.Extras.PausedRemainingMs = remaining + (seconds * 1000);
            }
            else if (runtime.DeadlineUtc is { } deadline)
            {
                runtime.DeadlineUtc = deadline.AddSeconds(seconds);
            }
            else
            {
                return false;
            }

            await AnnouncePhaseAsync(runtime);
            return true;
        });

    /// <summary>In a timed quiz: skips the countdown, ends the question now, or stops showing its results and moves on.</summary>
    public Task<bool> SkipAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!IsTimed(runtime) || runtime.Status != SessionStatus.Running)
            {
                return false;
            }

            switch (runtime.Phase)
            {
                case QuestionPhase.Waiting:
                    await OpenCurrentQuestionAsync(runtime);
                    return true;
                case QuestionPhase.Open:
                    await RevealBrieflyAsync(runtime);
                    return true;
                case QuestionPhase.AnswerRevealed:
                    await MoveToNextQuestionAsync(runtime);
                    return true;
                default:
                    return false;
            }
        });

    /// <summary>Hides a word from the results, or a blank's answer, for this question.</summary>
    public Task<bool> HideWordAsync(Guid sessionId, string hostId, string word) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.CurrentQuestion is not { } question)
            {
                return false;
            }

            if (!runtime.Extras.HiddenWords.TryGetValue(question.Id, out var hidden))
            {
                runtime.Extras.HiddenWords[question.Id] = hidden = [];
            }

            hidden.Add(question.Type == QuestionType.WordCloud ? TextRules.NormalizeEntry(word) : word.Trim().ToLowerInvariant());
            await AnnounceResultsAsync(runtime);
            return true;
        });

    /// <summary>Marks a common answer as accepted for the current question and rescores everyone.</summary>
    public Task<bool> AcceptAnswerAsync(Guid sessionId, string hostId, int blank, string text) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.CurrentQuestion is not { Type: QuestionType.FillInBlank } question
                || blank < 1
                || blank > question.BlankCount
                || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!runtime.Extras.AcceptedAnswers.TryGetValue(question.Id, out var accepted))
            {
                runtime.Extras.AcceptedAnswers[question.Id] = accepted = [];
            }

            accepted.Add(new BlankAnswerSnapshot(blank, text.Trim(), MatchMode.CaseInsensitive));

            foreach (var response in runtime.Responses.Values.Where(r => r.QuestionId == question.Id))
            {
                var scored = Mark(runtime, question, response.Answer, response.ElapsedMs);
                response.IsCorrect = scored.IsCorrect;
                response.Score = scored.Score;
                response.NeedsUpdate = true;
            }

            runtime.HasUnsavedResponses = true;
            await AnnounceResultsAsync(runtime);
            if (runtime.Phase == QuestionPhase.AnswerRevealed)
            {
                await SendStandingsAsync(runtime);
            }

            return true;
        });

    private bool InPhase(LiveRuntime runtime, QuestionPhase phase) =>
        runtime.Status == SessionStatus.Running && runtime.Kind == SessionKind.Quiz && runtime.Phase == phase;

    private void EnterPhase(LiveRuntime runtime, QuestionPhase phase)
    {
        runtime.Phase = phase;
        runtime.DeadlineUtc = null;
        runtime.Extras.PausedRemainingMs = null;
        runtime.Extras.PausedAtUtc = null;
        runtime.Extras.PhaseStartedUtc = UtcNow;
    }

    private async Task OpenCurrentQuestionAsync(LiveRuntime runtime)
    {
        EnterPhase(runtime, QuestionPhase.Open);
        if (runtime.CurrentQuestion?.TimeLimitSeconds is { } seconds)
        {
            runtime.DeadlineUtc = UtcNow.AddSeconds(seconds);
        }

        await AnnouncePhaseAsync(runtime);
    }

    private async Task CloseCurrentQuestionAsync(LiveRuntime runtime)
    {
        EnterPhase(runtime, QuestionPhase.Closed);
        await AnnouncePhaseAsync(runtime);
    }

    private async Task RevealAsync(LiveRuntime runtime)
    {
        EnterPhase(runtime, QuestionPhase.AnswerRevealed);
        await AnnouncePhaseAsync(runtime);
        await SendStandingsAsync(runtime);
    }

    /// <summary>Timed quizzes: show the results and answer for a few seconds before moving on.</summary>
    private async Task RevealBrieflyAsync(LiveRuntime runtime)
    {
        EnterPhase(runtime, QuestionPhase.AnswerRevealed);
        runtime.DeadlineUtc = UtcNow.AddSeconds(runtime.Quiz!.ResultsDisplaySeconds);
        await AnnouncePhaseAsync(runtime);
        await SendStandingsAsync(runtime);
    }

    private async Task MoveToNextQuestionAsync(LiveRuntime runtime)
    {
        if (runtime.CurrentIndex + 1 >= (runtime.Quiz?.Questions.Count ?? 0))
        {
            EnterPhase(runtime, QuestionPhase.Finished);
            await AnnouncePhaseAsync(runtime);
            await SendStandingsAsync(runtime);
            return;
        }

        runtime.CurrentIndex++;
        if (IsTimed(runtime))
        {
            await OpenCurrentQuestionAsync(runtime);
        }
        else
        {
            EnterPhase(runtime, QuestionPhase.Waiting);
            await AnnouncePhaseAsync(runtime);
        }
    }

    /// <summary>Sends updated totals to whoever can already see them.</summary>
    private async Task AnnounceResultsAsync(LiveRuntime runtime)
    {
        if (runtime.Phase is QuestionPhase.ShowingResults or QuestionPhase.AnswerRevealed && runtime.CurrentQuestion is { } question)
        {
            var results = BuildResults(runtime, question, runtime.Phase == QuestionPhase.AnswerRevealed);
            await broadcaster.ToStudentsAsync(runtime.Id, HubEvents.ResultsUpdated, results);
        }

        await PushViewsAsync(runtime);
    }

    /// <summary>Moves timers along. Returns whether anything changed.</summary>
    private async Task<bool> AdvanceTimersAsync(LiveRuntime runtime, DateTime now)
    {
        if (runtime.DeadlineUtc is not { } deadline)
        {
            return false;
        }

        if (runtime.Phase == QuestionPhase.Open && now >= deadline + DeadlineGrace)
        {
            if (IsTimed(runtime))
            {
                await RevealBrieflyAsync(runtime);
            }
            else
            {
                await CloseCurrentQuestionAsync(runtime);
            }

            return true;
        }

        if (runtime.Phase == QuestionPhase.Waiting && IsTimed(runtime) && now >= deadline)
        {
            await OpenCurrentQuestionAsync(runtime);
            return true;
        }

        if (runtime.Phase == QuestionPhase.AnswerRevealed && IsTimed(runtime) && now >= deadline)
        {
            await MoveToNextQuestionAsync(runtime);
            return true;
        }

        return false;
    }

    // ---- Scoring ----

    private static ScoredAnswer Mark(LiveRuntime runtime, QuestionSnapshot question, AnswerPayload answer, int elapsedMs) =>
        AnswerScorer.Score(
            question,
            answer,
            AcceptedAnswers(runtime, question),
            runtime.Quiz?.ScoringEnabled == true,
            runtime.Quiz?.SpeedBonusEnabled == true,
            elapsedMs);

    /// <summary>
    /// Every student's total and the rank for each score, worked out in one pass. A reveal needs this for the
    /// whole class while holding the session's lock, so it must not rescan the answers once per student.
    /// </summary>
    private sealed record ScoreTable(Dictionary<Guid, int> Totals, Dictionary<int, int> RankByScore, int ParticipantCount);

    private static ScoreTable BuildScoreTable(LiveRuntime runtime)
    {
        var totals = runtime.ActiveParticipants.ToDictionary(p => p.Id, _ => 0);
        foreach (var response in runtime.Responses.Values)
        {
            if (totals.TryGetValue(response.ParticipantId, out var total))
            {
                totals[response.ParticipantId] = total + response.Score;
            }
        }

        // Equal scores share a rank, and the next rank skips past them: 1, 1, 3.
        var rankByScore = new Dictionary<int, int>();
        var position = 0;
        foreach (var score in totals.Values.OrderDescending())
        {
            position++;
            rankByScore.TryAdd(score, position);
        }

        return new ScoreTable(totals, rankByScore, totals.Count);
    }

    private static bool ShowsLeaderboard(LiveRuntime runtime) =>
        runtime.Quiz?.ShowLeaderboard == true && runtime.Phase is QuestionPhase.AnswerRevealed or QuestionPhase.Finished;

    /// <summary>The top of the class.</summary>
    private static List<LeaderboardEntry> BuildLeaderboard(LiveRuntime runtime, int count)
    {
        var table = BuildScoreTable(runtime);
        return runtime.ActiveParticipants
            .Select(p => (p.Nickname, Score: table.Totals[p.Id]))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(p => new LeaderboardEntry(table.RankByScore[p.Score], p.Nickname, p.Score))
            .ToList();
    }

    private static Standing? BuildStanding(LiveRuntime runtime, LiveParticipant participant, ScoreTable table)
    {
        if (runtime.Quiz is not { ScoringEnabled: true } quiz)
        {
            return null;
        }

        var total = table.Totals.GetValueOrDefault(participant.Id);
        int? rank = quiz.ShowLeaderboard ? table.RankByScore.GetValueOrDefault(total, table.ParticipantCount) : null;

        var last = runtime.CurrentQuestion is { } question && runtime.Responses.TryGetValue((participant.Id, question.Id), out var response)
            ? response
            : null;
        return new Standing(total, rank, table.ParticipantCount, last?.Score ?? 0, last?.IsCorrect);
    }

    /// <summary>Tells each student their own position, and nobody else's.</summary>
    private async Task SendStandingsAsync(LiveRuntime runtime)
    {
        if (runtime.Quiz?.ScoringEnabled != true)
        {
            return;
        }

        var table = BuildScoreTable(runtime);
        foreach (var participant in runtime.ActiveParticipants)
        {
            var standing = BuildStanding(runtime, participant, table);
            foreach (var connectionId in participant.Connections)
            {
                await broadcaster.ToConnectionAsync(connectionId, HubEvents.LeaderboardUpdated, standing);
            }
        }
    }

    private static double? AverageScore(LiveRuntime runtime, QuestionSnapshot question)
    {
        if (runtime.Quiz?.ScoringEnabled != true || question.Type == QuestionType.WordCloud)
        {
            return null;
        }

        var scores = runtime.ResponsesFor(question.Id).Select(r => r.Score).ToList();
        return scores.Count == 0 ? null : scores.Average();
    }
}
