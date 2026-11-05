using System.Text.Json;
using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Live;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CohortCompanion.Tests;

public sealed class FakeBroadcaster : ISessionBroadcaster
{
    public List<(string Method, object? Argument)> ToStudents { get; } = [];

    public List<(string Method, object? Argument)> ToProjector { get; } = [];

    public List<(string Connection, string Method, object? Argument)> ToConnections { get; } = [];

    public Task AddToStudentsAsync(Guid sessionId, string connectionId) => Task.CompletedTask;

    public Task RemoveFromStudentsAsync(Guid sessionId, string connectionId) => Task.CompletedTask;

    public Task AddToProjectorAsync(Guid sessionId, string connectionId) => Task.CompletedTask;

    public Task ToStudentsAsync(Guid sessionId, string method, object? argument)
    {
        ToStudents.Add((method, argument));
        return Task.CompletedTask;
    }

    public Task ToProjectorAsync(Guid sessionId, string method, object? argument)
    {
        ToProjector.Add((method, argument));
        return Task.CompletedTask;
    }

    public Task ToConnectionAsync(string connectionId, string method, object? argument)
    {
        ToConnections.Add((connectionId, method, argument));
        return Task.CompletedTask;
    }

    public PhaseUpdate LastPhase => ToStudents.Where(m => m.Method == HubEvents.PhaseChanged).Select(m => (PhaseUpdate)m.Argument!).Last();
}

public abstract class LiveSessionTestBase : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    protected ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    protected FakeBroadcaster Broadcaster { get; } = new();

    protected TestDbContextFactory Db { get; private set; } = default!;

    protected LiveSessionManager Manager { get; private set; } = default!;

    protected string HostId { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        Db = new TestDbContextFactory(connection);

        await using var db = Db.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var host = new ApplicationUser { UserName = "host@example.com", Email = "host@example.com", DisplayName = "Host" };
        db.Users.Add(host);
        await db.SaveChangesAsync();
        HostId = host.Id;

        Manager = NewManager();
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    protected LiveSessionManager NewManager() =>
        new(Db, Broadcaster, Time, NullLogger<LiveSessionManager>.Instance);

    protected Quiz SampleQuiz(params Question[] questions) => new()
    {
        Id = 1,
        OwnerId = HostId,
        Title = "Quiz",
        Questions = questions.Length > 0 ? [.. questions] : [MultipleChoice()],
    };

    protected static Question MultipleChoice(int order = 0, int? timeLimit = null, bool multi = false, bool partial = false) => new()
    {
        Order = order,
        Type = QuestionType.MultipleChoice,
        Prompt = "<p>Which keyword declares a constant?</p>",
        TimeLimitSeconds = timeLimit,
        AllowMultipleSelect = multi,
        PartialCredit = partial,
        Options =
        [
            new ChoiceOption { Order = 0, Text = "const", IsCorrect = true },
            new ChoiceOption { Order = 1, Text = "var", IsCorrect = multi },
            new ChoiceOption { Order = 2, Text = "let" },
        ],
    };

    protected static Question WordCloud(int order = 0, int maxEntries = 2) => new()
    {
        Order = order,
        Type = QuestionType.WordCloud,
        Prompt = "<p>Describe C#</p>",
        MaxEntries = maxEntries,
    };

    protected static Question FillInBlank(int order = 0) => new()
    {
        Order = order,
        Type = QuestionType.FillInBlank,
        Prompt = "<p>The keyword {{1}} declares a {{2}}.</p>",
        BlankAnswers =
        [
            new BlankAnswer { BlankIndex = 1, AcceptedText = "const", MatchMode = MatchMode.CaseInsensitive },
            new BlankAnswer { BlankIndex = 2, AcceptedText = "constant", MatchMode = MatchMode.Fuzzy },
        ],
    };

    protected async Task<(Guid Id, string Code)> StartSessionAsync(Quiz quiz)
    {
        var id = (await Manager.CreateQuizSessionAsync(quiz, HostId))!.Value;
        var view = (await Manager.GetHostViewAsync(id, HostId))!;
        return (id, view.JoinCode);
    }

    protected async Task<(Guid Id, string Code)> RunningSessionAsync(Quiz quiz)
    {
        var session = await StartSessionAsync(quiz);
        await Manager.StartAsync(session.Id, HostId);
        return session;
    }

    protected async Task<string> JoinAsync(string code, string nickname, string? connection = null)
    {
        var result = await Manager.JoinAsync(code, nickname, connection ?? nickname);
        Assert.Equal(JoinError.None, result.Error);
        return result.ParticipantToken!;
    }

    protected static string ToJson(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
