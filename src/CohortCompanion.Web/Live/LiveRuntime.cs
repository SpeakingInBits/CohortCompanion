using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Live;

/// <summary>
/// The in-memory state of one live session. Every read and write of its fields happens while holding
/// <see cref="Gate"/>, so students and the instructor can't interleave half-finished changes.
/// </summary>
internal sealed class LiveRuntime
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public required Guid Id { get; init; }

    public required SessionKind Kind { get; init; }

    public required string HostId { get; init; }

    public required string JoinCode { get; init; }

    public required string Title { get; init; }

    public QuizSnapshot? Quiz { get; init; }

    public PresentationSnapshot? Deck { get; init; }

    /// <summary>The quiz or presentation this session was started from.</summary>
    public int? SourceId { get; set; }

    public SessionStatus Status { get; set; }

    public int CurrentIndex { get; set; }

    public QuestionPhase Phase { get; set; }

    public DateTime? DeadlineUtc { get; set; }

    public DateTime LastActivityUtc { get; set; }

    public RuntimeExtras Extras { get; set; } = new();

    public Dictionary<Guid, LiveParticipant> Participants { get; } = [];

    public Dictionary<(Guid Participant, int Question), LiveResponse> Responses { get; } = [];

    /// <summary>Counts or nicknames changed, but the host and projector haven't been told yet.</summary>
    public bool ViewsAreStale { get; set; }

    public DateTime LastViewPushUtc { get; set; }

    public DateTime LastPersistUtc { get; set; }

    public bool HasUnsavedResponses { get; set; }

    public QuestionSnapshot? CurrentQuestion =>
        Quiz is { } quiz && CurrentIndex >= 0 && CurrentIndex < quiz.Questions.Count ? quiz.Questions[CurrentIndex] : null;

    public IEnumerable<LiveParticipant> ActiveParticipants => Participants.Values.Where(p => !p.IsRemoved);

    public IEnumerable<LiveResponse> ResponsesFor(int questionId) =>
        Responses.Values.Where(r => r.QuestionId == questionId && !Participants[r.ParticipantId].IsRemoved);
}

/// <summary>Live state that isn't worth a column of its own. Saved as JSON and holds no participant data.</summary>
internal sealed class RuntimeExtras
{
    public DateTime? PhaseStartedUtc { get; set; }

    /// <summary>Lets the projector link open without signing in. It shows only what the class already sees.</summary>
    public string? ProjectorKey { get; set; }

    /// <summary>Time left on the clock while paused.</summary>
    public int? PausedRemainingMs { get; set; }

    public DateTime? PausedAtUtc { get; set; }

    /// <summary>Indexes of the slides the instructor has shown, which are all students may see.</summary>
    public HashSet<int> ShownSlides { get; set; } = [];

    /// <summary>Normalized words the instructor hid, by question id.</summary>
    public Dictionary<int, HashSet<string>> HiddenWords { get; set; } = [];

    /// <summary>Answers the instructor accepted during the session, by question id.</summary>
    public Dictionary<int, List<BlankAnswerSnapshot>> AcceptedAnswers { get; set; } = [];
}

internal sealed class LiveParticipant
{
    public required Guid Id { get; init; }

    public required string TokenHash { get; init; }

    public required string Nickname { get; set; }

    public bool IsRemoved { get; set; }

    public HashSet<string> Connections { get; } = [];
}

internal sealed class LiveResponse
{
    public required Guid Id { get; init; }

    public required Guid ParticipantId { get; init; }

    public required int QuestionId { get; init; }

    public required AnswerPayload Answer { get; init; }

    public required DateTime SubmittedAtUtc { get; init; }

    public required int ElapsedMs { get; init; }

    public bool? IsCorrect { get; set; }

    public int Score { get; set; }

    public bool IsSaved { get; set; }

    public bool NeedsUpdate { get; set; }
}
