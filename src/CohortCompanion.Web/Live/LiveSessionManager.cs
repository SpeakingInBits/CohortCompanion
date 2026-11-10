using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Presentations;
using CohortCompanion.Web.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Live;

public sealed record ParticipantView(Guid Id, string Nickname);

/// <summary>What the instructor's control page shows. It runs on the server, so it may include answers.</summary>
public sealed record HostView(
    Guid Id,
    string Title,
    SessionKind Kind,
    string JoinCode,
    SessionStatus Status,
    QuestionPhase Phase,
    int QuestionIndex,
    int QuestionCount,
    QuestionSnapshot? Question,
    int? RemainingMs,
    bool IsPaused,
    int ResponseCount,
    IReadOnlyList<ParticipantView> Participants,
    QuestionResults? LiveResults,
    bool IsTimed,
    IReadOnlyList<LeaderboardEntry>? Leaderboard,
    DeckView? Deck,
    string ProjectorKey,
    DateTime? EndsByItselfUtc);

/// <summary>What the instructor sees while presenting, including the notes students never receive.</summary>
public sealed record DeckView(string ThemeId, SlideView? Current, SlideView? Next, string SpeakerNotes);

public sealed record SessionListItem(Guid Id, string Title, SessionKind Kind, string JoinCode, SessionStatus Status, int ParticipantCount);

/// <summary>
/// Owns every live session. State lives in memory for speed and is written to the database at each phase
/// change, so a restart can recover it. Answers are buffered and saved in batches rather than one at a
/// time, and the host and projector get counts at most twice a second, so a whole class answering at
/// once costs little.
/// </summary>
public sealed partial class LiveSessionManager(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISessionBroadcaster broadcaster,
    TimeProvider timeProvider,
    ILogger<LiveSessionManager> logger)
{
    private static readonly TimeSpan ViewPushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(3);

    /// <summary>Allowance for network latency before a question closes at its deadline.</summary>
    public static readonly TimeSpan DeadlineGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>A session with no activity for this long is ended, so per-student data can't linger.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// A quiz that reached its end is ended this long afterwards, in case the instructor doesn't, so nicknames and
    /// answers don't stay around for the rest of the day.
    /// </summary>
    public static readonly TimeSpan EndAfterFinishing = TimeSpan.FromMinutes(30);

    /// <summary>The countdown before a timed quiz's first question opens.</summary>
    public static readonly TimeSpan GetReadyTime = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, LiveRuntime> sessions = new();
    private readonly ConcurrentDictionary<string, Guid> sessionByCode = new();
    private readonly ConcurrentDictionary<string, Guid> sessionByTokenHash = new();
    private readonly ConcurrentDictionary<string, (Guid Session, Guid? Participant)> connections = new();

    /// <summary>
    /// Batch writes from different classes take turns. Run together they deadlock in SQL Server, and detecting
    /// a deadlock takes seconds, while each write on its own takes milliseconds. Always taken after a session's
    /// lock, never before it.
    /// </summary>
    private readonly SemaphoreSlim batchWrites = new(1, 1);

    /// <summary>Raised when a session's host view changed. The argument is the session id.</summary>
    public event Action<Guid>? HostViewChanged;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    // ---- Creating sessions ----

    /// <summary>Creates a session in the lobby from a validated quiz. Returns null if the quiz can't run.</summary>
    public async Task<Guid?> CreateQuizSessionAsync(Quiz quiz, string hostId, CancellationToken cancellationToken = default)
    {
        if (quiz.OwnerId != hostId || QuizRules.Validate(quiz).Count > 0)
        {
            return null;
        }

        var snapshot = QuizSnapshot.From(quiz);
        var runtime = new LiveRuntime
        {
            Id = Guid.NewGuid(),
            Kind = SessionKind.Quiz,
            HostId = hostId,
            JoinCode = ReserveJoinCode(),
            Title = snapshot.Title,
            Quiz = snapshot,
            Status = SessionStatus.Lobby,
            Phase = QuestionPhase.Waiting,
            LastActivityUtc = UtcNow,
        };

        return await RegisterAsync(runtime, JsonSerializer.Serialize(snapshot, Json), quiz.Id, null, cancellationToken);
    }

    /// <summary>Creates a presentation session in the lobby. The caller has already loaded the deck as its owner.</summary>
    public async Task<Guid?> CreatePresentationSessionAsync(PresentationDocument document, string hostId, CancellationToken cancellationToken = default)
    {
        if (document.Slides.Count == 0)
        {
            return null;
        }

        var snapshot = PresentationSnapshot.From(document);
        var runtime = new LiveRuntime
        {
            Id = Guid.NewGuid(),
            Kind = SessionKind.Presentation,
            HostId = hostId,
            JoinCode = ReserveJoinCode(),
            Title = snapshot.Title,
            Deck = snapshot,
            Status = SessionStatus.Lobby,
            Phase = QuestionPhase.Waiting,
            LastActivityUtc = UtcNow,
        };

        return await RegisterAsync(runtime, JsonSerializer.Serialize(snapshot, Json), null, document.Id, cancellationToken);
    }

    private async Task<Guid?> RegisterAsync(LiveRuntime runtime, string snapshotJson, int? quizId, int? presentationId, CancellationToken cancellationToken)
    {
        var now = runtime.LastActivityUtc;
        runtime.SourceId = quizId ?? presentationId;
        runtime.Extras.ProjectorKey = NewProjectorKey();
        sessionByCode[runtime.JoinCode] = runtime.Id;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.LiveSessions.Add(new LiveSession
        {
            Id = runtime.Id,
            Kind = runtime.Kind,
            QuizId = quizId,
            PresentationId = presentationId,
            Title = runtime.Title,
            HostId = runtime.HostId,
            JoinCode = runtime.JoinCode,
            Status = runtime.Status,
            Phase = runtime.Phase,
            CreatedAtUtc = now,
            LastActivityUtc = now,
            SnapshotJson = snapshotJson,
            RuntimeJson = JsonSerializer.Serialize(runtime.Extras, Json),
        });
        await db.SaveChangesAsync(cancellationToken);

        sessions[runtime.Id] = runtime;
        return runtime.Id;
    }

    /// <summary>The secret in a projector link. It only shows what the class already sees on the projector.</summary>
    private static string NewProjectorKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>
    /// The session this instructor is already running for a quiz or deck, if any, so starting it again goes back
    /// to that session instead of opening a second one with another join code.
    /// </summary>
    public Guid? FindLiveSession(string hostId, SessionKind kind, int sourceId) =>
        sessions.Values
            .Where(s => s.HostId == hostId && s.Kind == kind && s.SourceId == sourceId && s.Status != SessionStatus.Ended)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefault();

    /// <summary>Starts a session for the quiz, or returns the one already running. Null if the quiz can't run.</summary>
    public async Task<(Guid? Id, bool Resumed)> StartOrResumeQuizAsync(Quiz quiz, string hostId) =>
        quiz.OwnerId == hostId && FindLiveSession(hostId, SessionKind.Quiz, quiz.Id) is { } running
            ? (running, true)
            : (await CreateQuizSessionAsync(quiz, hostId), false);

    /// <summary>Starts a session for the deck, or returns the one already running.</summary>
    public async Task<(Guid? Id, bool Resumed)> StartOrResumePresentationAsync(PresentationDocument document, string hostId) =>
        FindLiveSession(hostId, SessionKind.Presentation, document.Id) is { } running
            ? (running, true)
            : (await CreatePresentationSessionAsync(document, hostId), false);

    /// <summary>The host page for a session that was just started or resumed.</summary>
    public static string HostPath(Guid sessionId, bool resumed) =>
        resumed ? $"sessions/{sessionId}/host?resumed=true" : $"sessions/{sessionId}/host";

    private string ReserveJoinCode()
    {
        while (true)
        {
            var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
            if (sessionByCode.TryAdd(code, Guid.Empty))
            {
                return code;
            }
        }
    }

    // ---- Students ----

    public async Task<JoinResult> JoinAsync(string joinCode, string nickname, string connectionId)
    {
        var name = (nickname ?? "").Trim();
        if (CheckNickname(name) is not JoinError.None and var problem)
        {
            return new JoinResult(problem);
        }

        if (!sessionByCode.TryGetValue((joinCode ?? "").Trim(), out var sessionId) || !sessions.TryGetValue(sessionId, out var runtime))
        {
            return new JoinResult(JoinError.SessionNotFound);
        }

        await runtime.Gate.WaitAsync();
        try
        {
            if (runtime.Status == SessionStatus.Ended)
            {
                return new JoinResult(JoinError.SessionNotFound);
            }

            if (NicknameTaken(runtime, Guid.Empty, name))
            {
                return new JoinResult(JoinError.NicknameTaken);
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var participant = new LiveParticipant { Id = Guid.NewGuid(), TokenHash = HashToken(token), Nickname = name };

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                db.Participants.Add(new Participant
                {
                    Id = participant.Id,
                    LiveSessionId = runtime.Id,
                    Nickname = name,
                    TokenHash = participant.TokenHash,
                    JoinedAtUtc = UtcNow,
                });
                await db.SaveChangesAsync();
            }

            runtime.Participants[participant.Id] = participant;
            sessionByTokenHash[participant.TokenHash] = runtime.Id;
            await AttachAsync(runtime, participant, connectionId);
            Touch(runtime);
            runtime.ViewsAreStale = true;

            return new JoinResult(JoinError.None, token, BuildStudentState(runtime, participant));
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public async Task<StudentSessionState?> RejoinAsync(string token, string connectionId)
    {
        var hash = HashToken(token ?? "");
        if (!sessionByTokenHash.TryGetValue(hash, out var sessionId) || !sessions.TryGetValue(sessionId, out var runtime))
        {
            return null;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            var participant = runtime.Participants.Values.FirstOrDefault(p => p.TokenHash == hash);
            if (participant is null || participant.IsRemoved || runtime.Status == SessionStatus.Ended)
            {
                return null;
            }

            await AttachAsync(runtime, participant, connectionId);
            return BuildStudentState(runtime, participant);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private static JoinError CheckNickname(string name) =>
        name.Length is < AnswerLimits.MinNickname or > AnswerLimits.MaxNickname ? JoinError.NicknameInvalid
        : TextRules.IsProfane(name) ? JoinError.NicknameNotAllowed
        : JoinError.None;

    private static bool NicknameTaken(LiveRuntime runtime, Guid participantId, string name) =>
        runtime.ActiveParticipants.Any(p => p.Id != participantId && string.Equals(p.Nickname, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lets a student fix their own nickname, but only in the lobby: once it starts, only the instructor can.</summary>
    public async Task<JoinError> ChangeNicknameAsync(string connectionId, string nickname)
    {
        if (!connections.TryGetValue(connectionId, out var connection)
            || connection.Participant is not { } participantId
            || !sessions.TryGetValue(connection.Session, out var runtime))
        {
            return JoinError.SessionNotFound;
        }

        var name = (nickname ?? "").Trim();
        if (CheckNickname(name) is not JoinError.None and var problem)
        {
            return problem;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            if (runtime.Status == SessionStatus.Ended
                || !runtime.Participants.TryGetValue(participantId, out var participant)
                || participant.IsRemoved)
            {
                return JoinError.SessionNotFound;
            }

            if (runtime.Status != SessionStatus.Lobby)
            {
                return JoinError.SessionStarted;
            }

            if (NicknameTaken(runtime, participantId, name))
            {
                return JoinError.NicknameTaken;
            }

            await SetNicknameAsync(runtime, participant, name);
            Touch(runtime);
            return JoinError.None;
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>
    /// A student leaves for good, for example to join another class. It works like the instructor removing them:
    /// their answers stop counting and the token stops working.
    /// </summary>
    public async Task LeaveAsync(string token)
    {
        var hash = HashToken(token ?? "");
        if (!sessionByTokenHash.TryGetValue(hash, out var sessionId) || !sessions.TryGetValue(sessionId, out var runtime))
        {
            return;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            var participant = runtime.Participants.Values.FirstOrDefault(p => p.TokenHash == hash);
            if (participant is null || participant.IsRemoved || runtime.Status == SessionStatus.Ended)
            {
                return;
            }

            await DetachParticipantAsync(runtime, participant, notify: false);
            Touch(runtime);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task AttachAsync(LiveRuntime runtime, LiveParticipant participant, string connectionId)
    {
        participant.Connections.Add(connectionId);
        connections[connectionId] = (runtime.Id, participant.Id);
        await broadcaster.AddToStudentsAsync(runtime.Id, connectionId);
    }

    public async Task ConnectionClosedAsync(string connectionId)
    {
        if (!connections.TryRemove(connectionId, out var connection)
            || connection.Participant is not { } participantId
            || !sessions.TryGetValue(connection.Session, out var runtime))
        {
            return;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            if (runtime.Participants.TryGetValue(participantId, out var participant))
            {
                participant.Connections.Remove(connectionId);
            }
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public async Task<SubmitResult> SubmitAnswerAsync(string connectionId, int questionId, AnswerPayload answer)
    {
        if (!connections.TryGetValue(connectionId, out var connection)
            || connection.Participant is not { } participantId
            || !sessions.TryGetValue(connection.Session, out var runtime))
        {
            return new SubmitResult(false, "not-joined");
        }

        await runtime.Gate.WaitAsync();
        try
        {
            var question = runtime.CurrentQuestion;
            var now = UtcNow;
            if (question is null || question.Id != questionId || runtime.Phase != QuestionPhase.Open)
            {
                return new SubmitResult(false, "closed");
            }

            if (runtime.DeadlineUtc is { } deadline && now > deadline + DeadlineGrace)
            {
                return new SubmitResult(false, "closed");
            }

            if (!runtime.Participants.TryGetValue(participantId, out var participant) || participant.IsRemoved)
            {
                return new SubmitResult(false, "removed");
            }

            if (runtime.Responses.ContainsKey((participantId, questionId)))
            {
                return new SubmitResult(false, "already-answered");
            }

            var cleaned = AnswerValidator.Clean(question, answer);
            if (cleaned is null)
            {
                return new SubmitResult(false, "invalid");
            }

            var started = runtime.Extras.PhaseStartedUtc ?? now;
            var elapsedMs = (int)Math.Max(0, (now - started).TotalMilliseconds);
            var scored = Mark(runtime, question, cleaned, elapsedMs);
            runtime.Responses[(participantId, questionId)] = new LiveResponse
            {
                Id = Guid.NewGuid(),
                ParticipantId = participantId,
                QuestionId = questionId,
                Answer = cleaned,
                SubmittedAtUtc = now,
                ElapsedMs = elapsedMs,
                IsCorrect = scored.IsCorrect,
                Score = scored.Score,
            };
            runtime.HasUnsavedResponses = true;
            runtime.ViewsAreStale = true;
            Touch(runtime);
            return new SubmitResult(true);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    // ---- Projector ----

    /// <summary>
    /// Connects a projector. It needs either the instructor running the session, or the session's projector key,
    /// so a room PC can show it without anyone signing in there.
    /// </summary>
    public async Task<ProjectorState?> WatchProjectorAsync(Guid sessionId, string? hostId, string connectionId, string? key = null)
    {
        if (!sessions.TryGetValue(sessionId, out var runtime)
            || !(hostId is not null && runtime.HostId == hostId || KeyMatches(runtime.Extras.ProjectorKey, key)))
        {
            return null;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            connections[connectionId] = (runtime.Id, null);
            await broadcaster.AddToProjectorAsync(runtime.Id, connectionId);
            return BuildProjectorState(runtime);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private static bool KeyMatches(string? expected, string? given) =>
        expected is { Length: > 0 } && given is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    // ---- Instructor views ----

    public async Task<HostView?> GetHostViewAsync(Guid sessionId, string hostId)
    {
        if (!sessions.TryGetValue(sessionId, out var runtime) || runtime.HostId != hostId)
        {
            return null;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            var question = runtime.CurrentQuestion;
            var count = question is null ? 0 : runtime.ResponsesFor(question.Id).Count();
            return new HostView(
                runtime.Id,
                runtime.Title,
                runtime.Kind,
                runtime.JoinCode,
                runtime.Status,
                runtime.Phase,
                runtime.CurrentIndex,
                ItemCount(runtime),
                question,
                RemainingMs(runtime),
                runtime.Extras.PausedRemainingMs is not null,
                count,
                runtime.ActiveParticipants.OrderBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase).Select(p => new ParticipantView(p.Id, p.Nickname)).ToList(),
                question is null ? null : BuildResults(runtime, question, reveal: true),
                runtime.Quiz?.Mode == QuizMode.Timed,
                runtime.Quiz?.ScoringEnabled == true ? BuildLeaderboard(runtime, 5) : null,
                BuildDeckView(runtime),
                runtime.Extras.ProjectorKey ?? "",
                runtime.Phase == QuestionPhase.Finished ? runtime.Extras.PhaseStartedUtc + EndAfterFinishing : null);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>Sessions this instructor is running right now.</summary>
    public IReadOnlyList<SessionListItem> ListLive(string hostId) =>
        sessions.Values
            .Where(s => s.HostId == hostId && s.Status != SessionStatus.Ended)
            .Select(s => new SessionListItem(s.Id, s.Title, s.Kind, s.JoinCode, s.Status, s.Participants.Count))
            .ToList();

    // ---- Instructor commands ----

    /// <summary>Runs an instructor command under the session's lock, after checking they own the session.</summary>
    private async Task<bool> CommandAsync(Guid sessionId, string hostId, Func<LiveRuntime, Task<bool>> command)
    {
        if (!sessions.TryGetValue(sessionId, out var runtime) || runtime.HostId != hostId)
        {
            return false;
        }

        await runtime.Gate.WaitAsync();
        try
        {
            if (runtime.Status == SessionStatus.Ended || !await command(runtime))
            {
                return false;
            }

            Touch(runtime);
        }
        finally
        {
            runtime.Gate.Release();
        }

        HostViewChanged?.Invoke(sessionId);
        return true;
    }

    public Task<bool> RemoveParticipantAsync(Guid sessionId, string hostId, Guid participantId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (!runtime.Participants.TryGetValue(participantId, out var participant) || participant.IsRemoved)
            {
                return false;
            }

            await DetachParticipantAsync(runtime, participant, notify: true);
            return true;
        });

    /// <summary>Takes a student out of the session, after which their answers stop counting.</summary>
    private async Task DetachParticipantAsync(LiveRuntime runtime, LiveParticipant participant, bool notify)
    {
        participant.IsRemoved = true;
        foreach (var connectionId in participant.Connections.ToList())
        {
            if (notify)
            {
                await broadcaster.ToConnectionAsync(connectionId, HubEvents.ParticipantRemoved, null);
            }

            await broadcaster.RemoveFromStudentsAsync(runtime.Id, connectionId);
            connections.TryRemove(connectionId, out _);
        }

        participant.Connections.Clear();
        sessionByTokenHash.TryRemove(participant.TokenHash, out _);

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Participants.Where(p => p.Id == participant.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsRemoved, true));

        await PushViewsAsync(runtime);
    }

    public Task<bool> RenameParticipantAsync(Guid sessionId, string hostId, Guid participantId, string nickname) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            var name = (nickname ?? "").Trim();
            if (name.Length is < AnswerLimits.MinNickname or > AnswerLimits.MaxNickname
                || !runtime.Participants.TryGetValue(participantId, out var participant)
                || participant.IsRemoved
                || NicknameTaken(runtime, participantId, name))
            {
                return false;
            }

            await SetNicknameAsync(runtime, participant, name);
            return true;
        });

    private async Task SetNicknameAsync(LiveRuntime runtime, LiveParticipant participant, string name)
    {
        participant.Nickname = name;
        foreach (var connectionId in participant.Connections)
        {
            await broadcaster.ToConnectionAsync(connectionId, HubEvents.NicknameChanged, name);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Participants.Where(p => p.Id == participant.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Nickname, name));

        await PushViewsAsync(runtime);
    }

    /// <summary>Ends the session: saves totals, deletes per-student data, and releases the join code, all at once.</summary>
    public Task<bool> EndAsync(Guid sessionId, string hostId) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            await FinishAsync(runtime);
            return true;
        });

    private async Task FinishAsync(LiveRuntime runtime)
    {
        var now = UtcNow;

        await batchWrites.WaitAsync();
        try
        {
            await using var strategyContext = await dbFactory.CreateDbContextAsync();

            // All or nothing, and safe to retry after a transient Azure SQL failure: each attempt starts clean.
            await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.SessionSummaries.AddRange(BuildSummaries(runtime));
                await db.SaveChangesAsync();
                // Responses go with their participants through the cascade.
                await db.Participants.Where(p => p.LiveSessionId == runtime.Id).ExecuteDeleteAsync();
                await db.LiveSessions.Where(s => s.Id == runtime.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, SessionStatus.Ended)
                    .SetProperty(x => x.EndedAtUtc, now)
                    .SetProperty(x => x.LastActivityUtc, now)
                    .SetProperty(x => x.PhaseDeadlineUtc, (DateTime?)null));
                await transaction.CommitAsync();
            });
        }
        finally
        {
            batchWrites.Release();
        }

        runtime.Status = SessionStatus.Ended;
        await broadcaster.ToStudentsAsync(runtime.Id, HubEvents.SessionEnded, null);
        await broadcaster.ToProjectorAsync(runtime.Id, HubEvents.SessionEnded, null);

        foreach (var participant in runtime.Participants.Values)
        {
            sessionByTokenHash.TryRemove(participant.TokenHash, out _);
            foreach (var connectionId in participant.Connections)
            {
                connections.TryRemove(connectionId, out _);
            }
        }

        runtime.Participants.Clear();
        runtime.Responses.Clear();
        sessionByCode.TryRemove(runtime.JoinCode, out _);
        sessions.TryRemove(runtime.Id, out _);
    }

    private List<SessionSummary> BuildSummaries(LiveRuntime runtime)
    {
        if (runtime.Quiz is not { } quiz)
        {
            return [];
        }

        var participantCount = runtime.ActiveParticipants.Count();
        var summaries = new List<SessionSummary>();
        for (var index = 0; index < quiz.Questions.Count; index++)
        {
            var question = quiz.Questions[index];
            if (!runtime.ResponsesFor(question.Id).Any())
            {
                continue;
            }

            var prompt = Content.ContentSanitizer.ToPlainText(question.Prompt);
            summaries.Add(new SessionSummary
            {
                LiveSessionId = runtime.Id,
                QuestionIndex = index,
                PromptText = prompt.Length <= 500 ? prompt : prompt[..500],
                ParticipantCount = participantCount,
                AggregateJson = JsonSerializer.Serialize(BuildResults(runtime, question, reveal: true) with { AverageScore = AverageScore(runtime, question) }, Json),
            });
        }

        return summaries;
    }

    // ---- Views ----

    private static int ItemCount(LiveRuntime runtime) => runtime.Quiz?.Questions.Count ?? runtime.Deck?.Slides.Count ?? 0;

    private int? RemainingMs(LiveRuntime runtime)
    {
        if (runtime.Extras.PausedRemainingMs is { } paused)
        {
            return paused;
        }

        return runtime.DeadlineUtc is { } deadline ? (int)Math.Max(0, (deadline - UtcNow).TotalMilliseconds) : null;
    }

    private QuestionResults BuildResults(LiveRuntime runtime, QuestionSnapshot question, bool reveal)
    {
        var answers = runtime.ResponsesFor(question.Id).Select(r => r.Answer).ToList();
        var hidden = runtime.Extras.HiddenWords.GetValueOrDefault(question.Id) ?? [];
        return ResultsBuilder.Build(question, answers, hidden, AcceptedAnswers(runtime, question), reveal);
    }

    private static IReadOnlyList<BlankAnswerSnapshot> AcceptedAnswers(LiveRuntime runtime, QuestionSnapshot question) =>
        runtime.Extras.AcceptedAnswers.TryGetValue(question.Id, out var extra)
            ? [.. question.BlankAnswers, .. extra]
            : question.BlankAnswers;

    private PhaseUpdate BuildPhaseUpdate(LiveRuntime runtime, bool forProjector = false)
    {
        var question = runtime.CurrentQuestion;
        var showQuestion = runtime.Status == SessionStatus.Running
            && (runtime.Phase != QuestionPhase.Waiting || forProjector)
            && runtime.Phase != QuestionPhase.Finished;
        var showResults = runtime.Phase is QuestionPhase.ShowingResults or QuestionPhase.AnswerRevealed;
        return new PhaseUpdate(
            runtime.Status,
            runtime.Phase,
            runtime.CurrentIndex,
            ItemCount(runtime),
            showQuestion ? question?.ToStudent() : null,
            RemainingMs(runtime),
            runtime.Extras.PausedRemainingMs is not null,
            showResults && question is not null ? BuildResults(runtime, question, runtime.Phase == QuestionPhase.AnswerRevealed) : null);
    }

    private StudentSessionState BuildStudentState(LiveRuntime runtime, LiveParticipant participant)
    {
        var question = runtime.CurrentQuestion;
        var mine = question is not null && runtime.Responses.TryGetValue((participant.Id, question.Id), out var response) ? response.Answer : null;
        return new StudentSessionState(runtime.Kind, runtime.Title, participant.Nickname, BuildPhaseUpdate(runtime), mine, BuildStanding(runtime, participant, BuildScoreTable(runtime)), BuildPresentationState(runtime));
    }

    private ProjectorState BuildProjectorState(LiveRuntime runtime)
    {
        var question = runtime.CurrentQuestion;
        return new ProjectorState(
            runtime.Title,
            runtime.Kind,
            runtime.JoinCode,
            runtime.ActiveParticipants.Select(p => p.Nickname).Order(StringComparer.OrdinalIgnoreCase).Take(200).ToList(),
            runtime.ActiveParticipants.Count(),
            question is null ? 0 : runtime.ResponsesFor(question.Id).Count(),
            BuildPhaseUpdate(runtime, forProjector: true),
            ShowsLeaderboard(runtime) ? BuildLeaderboard(runtime, 5) : null,
            runtime.Deck?.ThemeId,
            runtime.Status == SessionStatus.Running ? runtime.Deck?.ToView(runtime.CurrentIndex) : null);
    }

    /// <summary>Tells everyone about a phase change, after saving it so a restart can recover from here.</summary>
    private async Task AnnouncePhaseAsync(LiveRuntime runtime)
    {
        await PersistAsync(runtime);
        await broadcaster.ToStudentsAsync(runtime.Id, HubEvents.PhaseChanged, BuildPhaseUpdate(runtime));
        await PushViewsAsync(runtime);
    }

    private async Task PushViewsAsync(LiveRuntime runtime)
    {
        runtime.ViewsAreStale = false;
        runtime.LastViewPushUtc = UtcNow;
        await broadcaster.ToProjectorAsync(runtime.Id, HubEvents.ProjectorUpdated, BuildProjectorState(runtime));
        HostViewChanged?.Invoke(runtime.Id);
    }

    private void Touch(LiveRuntime runtime) => runtime.LastActivityUtc = UtcNow;

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- Persistence ----

    private async Task PersistAsync(LiveRuntime runtime)
    {
        var now = UtcNow;
        runtime.LastPersistUtc = now;

        await using var db = await dbFactory.CreateDbContextAsync();
        var extras = JsonSerializer.Serialize(runtime.Extras, Json);
        await db.LiveSessions.Where(s => s.Id == runtime.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, runtime.Status)
            .SetProperty(x => x.CurrentIndex, runtime.CurrentIndex)
            .SetProperty(x => x.Phase, runtime.Phase)
            .SetProperty(x => x.PhaseDeadlineUtc, runtime.DeadlineUtc)
            .SetProperty(x => x.LastActivityUtc, runtime.LastActivityUtc)
            .SetProperty(x => x.RuntimeJson, extras));

        if (!runtime.HasUnsavedResponses)
        {
            return;
        }

        foreach (var response in runtime.Responses.Values)
        {
            if (!response.IsSaved)
            {
                db.Responses.Add(ToEntity(response));
            }
            else if (response.NeedsUpdate)
            {
                db.Responses.Update(ToEntity(response));
            }
        }

        await batchWrites.WaitAsync();
        try
        {
            await db.SaveChangesAsync();
        }
        finally
        {
            batchWrites.Release();
        }

        foreach (var response in runtime.Responses.Values)
        {
            response.IsSaved = true;
            response.NeedsUpdate = false;
        }

        runtime.HasUnsavedResponses = false;
    }

    private static Response ToEntity(LiveResponse response) => new()
    {
        Id = response.Id,
        ParticipantId = response.ParticipantId,
        QuestionId = response.QuestionId,
        Payload = JsonSerializer.Serialize(response.Answer, Json),
        SubmittedAtUtc = response.SubmittedAtUtc,
        ElapsedMs = response.ElapsedMs,
        IsCorrect = response.IsCorrect,
        Score = response.Score,
    };

    /// <summary>Reloads sessions that hadn't ended when the server last stopped.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.LiveSessions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Participants).ThenInclude(p => p.Responses)
            .Where(s => s.Status != SessionStatus.Ended)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var runtime = new LiveRuntime
            {
                Id = row.Id,
                Kind = row.Kind,
                HostId = row.HostId,
                JoinCode = row.JoinCode,
                Title = row.Title,
                Quiz = row.Kind == SessionKind.Quiz ? JsonSerializer.Deserialize<QuizSnapshot>(row.SnapshotJson, Json) : null,
                Deck = row.Kind == SessionKind.Presentation ? JsonSerializer.Deserialize<PresentationSnapshot>(row.SnapshotJson, Json) : null,
                Status = row.Status,
                CurrentIndex = row.CurrentIndex,
                Phase = row.Phase,
                DeadlineUtc = row.PhaseDeadlineUtc,
                LastActivityUtc = row.LastActivityUtc,
                Extras = JsonSerializer.Deserialize<RuntimeExtras>(row.RuntimeJson, Json) ?? new RuntimeExtras(),
                SourceId = row.QuizId ?? row.PresentationId,
            };

            // Sessions saved before projector links existed get a key now.
            runtime.Extras.ProjectorKey ??= NewProjectorKey();

            foreach (var participant in row.Participants)
            {
                runtime.Participants[participant.Id] = new LiveParticipant
                {
                    Id = participant.Id,
                    TokenHash = participant.TokenHash,
                    Nickname = participant.Nickname,
                    IsRemoved = participant.IsRemoved,
                };

                if (!participant.IsRemoved)
                {
                    sessionByTokenHash[participant.TokenHash] = row.Id;
                }

                foreach (var response in participant.Responses)
                {
                    runtime.Responses[(participant.Id, response.QuestionId)] = new LiveResponse
                    {
                        Id = response.Id,
                        ParticipantId = participant.Id,
                        QuestionId = response.QuestionId,
                        Answer = JsonSerializer.Deserialize<AnswerPayload>(response.Payload, Json)!,
                        SubmittedAtUtc = response.SubmittedAtUtc,
                        ElapsedMs = response.ElapsedMs,
                        IsCorrect = response.IsCorrect,
                        Score = response.Score,
                        IsSaved = true,
                    };
                }
            }

            sessionByCode[runtime.JoinCode] = runtime.Id;
            sessions[runtime.Id] = runtime;
        }

        if (rows.Count > 0)
        {
            logger.LogInformation("Recovered {Count} live sessions.", rows.Count);
        }
    }

    // ---- Background tick ----

    /// <summary>
    /// Runs a few times a second: enforces deadlines and pushes throttled updates. Sessions tick independently,
    /// so a class that's slow, say waiting on the database, can't hold up another class's timers. A session
    /// that's busy is skipped, and the next tick comes along a moment later.
    /// </summary>
    public Task TickAsync()
    {
        var ticks = new List<Task>();
        foreach (var runtime in sessions.Values)
        {
            if (runtime.Gate.Wait(0))
            {
                ticks.Add(TickSessionAsync(runtime));
            }
        }

        return Task.WhenAll(ticks);
    }

    /// <summary>Called holding the session's lock, which it releases.</summary>
    private async Task TickSessionAsync(LiveRuntime runtime)
    {
        var changed = false;
        try
        {
            var now = UtcNow;
            if (runtime.Status == SessionStatus.Ended)
            {
                return;
            }

            if (now - runtime.LastActivityUtc >= AbandonedAfter)
            {
                await FinishAsync(runtime);
                logger.LogInformation("Ended abandoned session {SessionId}.", runtime.Id);
                changed = true;
                return;
            }

            if (runtime.Phase == QuestionPhase.Finished
                && runtime.Extras.PhaseStartedUtc is { } finishedAt
                && now - finishedAt >= EndAfterFinishing)
            {
                await FinishAsync(runtime);
                logger.LogInformation("Ended finished session {SessionId}.", runtime.Id);
                changed = true;
                return;
            }

            changed = await AdvanceTimersAsync(runtime, now);

            if (runtime.ViewsAreStale && now - runtime.LastViewPushUtc >= ViewPushInterval)
            {
                await PushViewsAsync(runtime);
            }

            if (runtime.HasUnsavedResponses && now - runtime.LastPersistUtc >= PersistInterval)
            {
                await PersistAsync(runtime);
            }
        }
        catch (Exception exception)
        {
            // Log only the session id: never answer content or nicknames.
            logger.LogError(exception, "Tick failed for session {SessionId}.", runtime.Id);
        }
        finally
        {
            runtime.Gate.Release();

            if (changed)
            {
                HostViewChanged?.Invoke(runtime.Id);
            }
        }
    }
}

/// <summary>Drives <see cref="LiveSessionManager.TickAsync"/> and recovers sessions at startup.</summary>
public sealed class LiveSessionTicker(LiveSessionManager manager, ILogger<LiveSessionTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await manager.RecoverAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not recover live sessions.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Not awaited, so one slow session can't delay the next tick for the others. Each session
            // handles its own errors, and one that's still busy is skipped.
            _ = manager.TickAsync();
        }
    }
}
