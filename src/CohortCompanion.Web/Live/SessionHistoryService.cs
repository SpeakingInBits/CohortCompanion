using System.Text.Json;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Live;

public sealed record PastSession(Guid Id, string Title, SessionKind Kind, DateTime EndedAtUtc);

public sealed record QuestionSummary(int QuestionIndex, string PromptText, int ParticipantCount, QuestionResults Results);

public sealed record SessionDetail(string Title, DateTime EndedAtUtc, IReadOnlyList<QuestionSummary> Questions);

/// <summary>Reads the totals kept after sessions end. There's no per-student data left here to read.</summary>
public sealed class SessionHistoryService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ended quizzes. Presentations keep no totals, so there's nothing of theirs to look back at.</summary>
    public async Task<List<PastSession>> ListAsync(string hostId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.LiveSessions
            .AsNoTracking()
            .Where(s => s.HostId == hostId && s.Status == SessionStatus.Ended && s.Kind == SessionKind.Quiz)
            .Select(s => new PastSession(s.Id, s.Title, s.Kind, s.EndedAtUtc ?? s.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(s => s.EndedAtUtc).ToList();
    }

    public async Task<SessionDetail?> GetAsync(Guid sessionId, string hostId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.LiveSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.HostId == hostId && s.Status == SessionStatus.Ended, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var summaries = await db.SessionSummaries
            .AsNoTracking()
            .Where(s => s.LiveSessionId == sessionId)
            .OrderBy(s => s.QuestionIndex)
            .ToListAsync(cancellationToken);

        return new SessionDetail(
            session.Title,
            session.EndedAtUtc ?? session.CreatedAtUtc,
            summaries.Select(s => new QuestionSummary(
                s.QuestionIndex,
                s.PromptText,
                s.ParticipantCount,
                JsonSerializer.Deserialize<QuestionResults>(s.AggregateJson, Json)!)).ToList());
    }
}
