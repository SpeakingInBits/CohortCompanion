using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Live;

/// <summary>The quiz phases: Waiting → Open → Closed → ShowingResults → AnswerRevealed → next question.</summary>
public sealed partial class LiveSessionManager
{
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
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
                    db.LiveSessions.Where(s => s.Id == runtime.Id),
                    s => s.SetProperty(x => x.StartedAtUtc, now));
            }

            await AnnouncePhaseAsync(runtime);
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

            EnterPhase(runtime, QuestionPhase.AnswerRevealed);
            await AnnouncePhaseAsync(runtime);
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

    private bool InPhase(LiveRuntime runtime, QuestionPhase phase) =>
        runtime.Status == SessionStatus.Running && runtime.Kind == SessionKind.Quiz && runtime.Phase == phase;

    private void EnterPhase(LiveRuntime runtime, QuestionPhase phase)
    {
        runtime.Phase = phase;
        runtime.DeadlineUtc = null;
        runtime.Extras.PausedRemainingMs = null;
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

    private async Task MoveToNextQuestionAsync(LiveRuntime runtime)
    {
        if (runtime.CurrentIndex + 1 >= (runtime.Quiz?.Questions.Count ?? 0))
        {
            EnterPhase(runtime, QuestionPhase.Finished);
        }
        else
        {
            runtime.CurrentIndex++;
            EnterPhase(runtime, QuestionPhase.Waiting);
        }

        await AnnouncePhaseAsync(runtime);
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

    /// <summary>Closes questions whose time ran out. Returns whether anything changed.</summary>
    private async Task<bool> AdvanceTimersAsync(LiveRuntime runtime, DateTime now)
    {
        if (runtime.DeadlineUtc is not { } deadline || runtime.Phase != QuestionPhase.Open || now < deadline + DeadlineGrace)
        {
            return false;
        }

        await CloseCurrentQuestionAsync(runtime);
        return true;
    }
}
