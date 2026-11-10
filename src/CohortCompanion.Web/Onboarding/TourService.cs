using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Onboarding;

/// <summary>
/// The signed-in instructor's place in the getting-started tour. One per circuit: each page loads it for its user,
/// and the tour's own buttons change it.
/// </summary>
public sealed class TourService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private string? userId;

    /// <summary>Finished until loaded, so nothing about the tour flashes up before the answer is known.</summary>
    public TourStatus Status { get; private set; } = TourStatus.Finished;

    /// <summary>True while the instructor is being walked through the steps.</summary>
    public bool IsActive => Status == TourStatus.InProgress;

    public async Task LoadAsync(string forUserId, CancellationToken cancellationToken = default)
    {
        userId = forUserId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Users
            .Where(u => u.Id == forUserId)
            .Select(u => (TourStatus?)u.TourStatus)
            .SingleOrDefaultAsync(cancellationToken);
        Status = stored ?? TourStatus.Finished;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => SetAsync(TourStatus.InProgress, cancellationToken);

    /// <summary>Ends the tour, whether it was completed, skipped, or abandoned halfway.</summary>
    public Task FinishAsync(CancellationToken cancellationToken = default) => SetAsync(TourStatus.Finished, cancellationToken);

    private async Task SetAsync(TourStatus status, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.TourStatus, status), cancellationToken);
        Status = status;
    }
}
