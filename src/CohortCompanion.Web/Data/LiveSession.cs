using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Data;

public class LiveSession
{
    public Guid Id { get; set; }

    public SessionKind Kind { get; set; }

    public int? QuizId { get; set; }

    public int? PresentationId { get; set; }

    public string Title { get; set; } = "";

    public string HostId { get; set; } = "";

    public ApplicationUser? Host { get; set; }

    /// <summary>Six digits. Unique among sessions that haven't ended.</summary>
    public string JoinCode { get; set; } = "";

    public SessionStatus Status { get; set; }

    public int CurrentIndex { get; set; }

    public QuestionPhase Phase { get; set; }

    public DateTime? PhaseDeadlineUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public DateTime LastActivityUtc { get; set; }

    /// <summary>Content copied when the session was created, so later edits can't change a running session.</summary>
    public string SnapshotJson { get; set; } = "";

    /// <summary>Small bits of live state needed to recover after a restart. Contains no participant data.</summary>
    public string RuntimeJson { get; set; } = "{}";

    public List<Participant> Participants { get; set; } = [];
}

/// <summary>A student in a live session. Deleted when the session ends.</summary>
public class Participant
{
    public Guid Id { get; set; }

    public Guid LiveSessionId { get; set; }

    public string Nickname { get; set; } = "";

    /// <summary>SHA-256 of the participant token. The token itself lives only on the student's device.</summary>
    public string TokenHash { get; set; } = "";

    public DateTime JoinedAtUtc { get; set; }

    public bool IsRemoved { get; set; }

    public List<Response> Responses { get; set; } = [];
}

/// <summary>A student's answer. Deleted when the session ends.</summary>
public class Response
{
    public Guid Id { get; set; }

    public Guid ParticipantId { get; set; }

    /// <summary>The question's id in the session snapshot.</summary>
    public int QuestionId { get; set; }

    public string Payload { get; set; } = "";

    public DateTime SubmittedAtUtc { get; set; }

    public int ElapsedMs { get; set; }

    public bool? IsCorrect { get; set; }

    public int Score { get; set; }
}

/// <summary>Per-question totals kept after a session ends. Holds no participant data.</summary>
public class SessionSummary
{
    public int Id { get; set; }

    public Guid LiveSessionId { get; set; }

    public int QuestionIndex { get; set; }

    public string PromptText { get; set; } = "";

    public int ParticipantCount { get; set; }

    /// <summary>Serialized <see cref="QuestionResults"/>.</summary>
    public string AggregateJson { get; set; } = "";
}
