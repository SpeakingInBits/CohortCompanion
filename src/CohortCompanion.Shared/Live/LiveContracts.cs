namespace CohortCompanion.Shared.Live;

public enum SessionKind
{
    Quiz = 0,
    Presentation = 1,
}

public enum SessionStatus
{
    Lobby = 0,
    Running = 1,
    Ended = 2,
}

/// <summary>Waiting → Open → Closed → ShowingResults → AnswerRevealed → next question, then Finished.</summary>
public enum QuestionPhase
{
    Waiting = 0,
    Open = 1,
    Closed = 2,
    ShowingResults = 3,
    AnswerRevealed = 4,
    Finished = 5,
}

public enum JoinError
{
    None = 0,
    SessionNotFound = 1,
    NicknameInvalid = 2,
    NicknameNotAllowed = 3,
    NicknameTaken = 4,

    /// <summary>Students can only change their own nickname in the lobby.</summary>
    SessionStarted = 5,
}

/// <summary>A student's answer. Only the fields for the question's type are set.</summary>
public sealed record AnswerPayload(int[]? Options = null, string[]? Entries = null, string[]? Blanks = null);

public sealed record SubmitResult(bool Accepted, string? Reason = null);

public sealed record JoinResult(JoinError Error, string? ParticipantToken = null, StudentSessionState? State = null);

/// <summary>A question as students see it. It never contains the correct answers.</summary>
public sealed record StudentQuestion(
    int Id,
    QuestionType Type,
    string Prompt,
    string? ImageUrl,
    bool AllowMultipleSelect,
    int MaxEntries,
    int BlankCount,
    IReadOnlyList<StudentOption> Options);

public sealed record StudentOption(int Index, string Text);

public sealed record OptionResult(int Index, string Text, int Count, bool? IsCorrect);

public sealed record WordCount(string Word, int Count);

public sealed record BlankAnswerCount(string Text, int Count, bool? IsAccepted);

public sealed record BlankResult(int Blank, IReadOnlyList<BlankAnswerCount> Answers);

/// <summary>
/// Totals for one question, without nicknames. <c>IsCorrect</c> and <c>IsAccepted</c> stay null until
/// the answer is revealed.
/// </summary>
public sealed record QuestionResults(
    int QuestionId,
    QuestionType Type,
    int ResponseCount,
    IReadOnlyList<OptionResult>? Options = null,
    IReadOnlyList<WordCount>? Words = null,
    IReadOnlyList<BlankResult>? Blanks = null,
    double? AverageScore = null);

/// <summary>Sent to every student when the phase changes, so it holds nothing specific to one student.</summary>
public sealed record PhaseUpdate(
    SessionStatus Status,
    QuestionPhase Phase,
    int QuestionIndex,
    int QuestionCount,
    StudentQuestion? Question,
    int? RemainingMs,
    bool IsPaused,
    QuestionResults? Results);

/// <summary>Everything one student needs after joining or reconnecting.</summary>
public sealed record StudentSessionState(
    SessionKind Kind,
    string Title,
    string Nickname,
    PhaseUpdate Phase,
    AnswerPayload? MyAnswer,
    Standing? MyStanding,
    PresentationState? Presentation = null);

/// <summary>The slides a student may see: only those the instructor has already shown.</summary>
public sealed record PresentationState(string ThemeId, int LiveIndex, int SlideCount, IReadOnlyList<SlideView> ShownSlides);

/// <summary>Sent to students when the instructor moves to a slide.</summary>
public sealed record SlideUpdate(int LiveIndex, SlideView Slide);

public sealed record ProjectorState(
    string Title,
    SessionKind Kind,
    string JoinCode,
    IReadOnlyList<string> Nicknames,
    int ParticipantCount,
    int ResponseCount,
    PhaseUpdate Phase,
    IReadOnlyList<LeaderboardEntry>? Leaderboard,
    string? ThemeId = null,
    SlideView? Slide = null);

public sealed record LeaderboardEntry(int Rank, string Nickname, int Score);

/// <summary>
/// One student's own position, sent only to that student. <c>Rank</c> is null unless the quiz shows a leaderboard.
/// </summary>
public sealed record Standing(int Score, int? Rank, int ParticipantCount, int LastQuestionScore, bool? LastAnswerCorrect);

/// <summary>SignalR method names, shared so the server and client can't drift apart.</summary>
public static class HubEvents
{
    public const string HubPath = "/hubs/session";

    public const string SessionState = nameof(SessionState);
    public const string SlideChanged = nameof(SlideChanged);
    public const string PhaseChanged = nameof(PhaseChanged);
    public const string ResultsUpdated = nameof(ResultsUpdated);
    public const string LeaderboardUpdated = nameof(LeaderboardUpdated);
    public const string NicknameChanged = nameof(NicknameChanged);
    public const string ParticipantRemoved = nameof(ParticipantRemoved);
    public const string SessionEnded = nameof(SessionEnded);
    public const string ProjectorUpdated = nameof(ProjectorUpdated);
}

/// <summary>Limits shared by the student form and the server, which enforces them.</summary>
public static class AnswerLimits
{
    public const int MinNickname = 2;
    public const int MaxNickname = 20;
    public const int MaxEntryLength = 40;
    public const int MaxBlankLength = 100;
}
