using System.Text.RegularExpressions;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Images;

/// <summary>
/// Deletes stored images that nothing uses any more: ones taken off a question or slide, ones whose quiz, deck, or
/// owner was deleted, and uploads that were never saved. Looking for unused images, rather than deleting one at the
/// moment it's removed, handles all of those the same way, and can't delete an image that something else still shows.
/// </summary>
public sealed partial class ImageCleanup(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IImageStore store,
    TimeProvider timeProvider,
    ILogger<ImageCleanup> logger)
{
    /// <summary>An upload isn't used by anything until its quiz or deck is saved, so new images are left alone.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    [GeneratedRegex(@"[0-9a-f]{32}\.(?:png|jpg|gif|webp)")]
    private static partial Regex ImageName();

    /// <summary>Returns how many images were deleted.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow() - GracePeriod;
        var old = new List<string>();
        await foreach (var image in store.ListAsync(cancellationToken))
        {
            if (image.SavedAt < cutoff && ImageService.IsStoredName(image.Name))
            {
                old.Add(image.Name);
            }
        }

        if (old.Count == 0)
        {
            return 0;
        }

        // Read after listing the store, so anything saved in the meantime counts as used.
        var used = await NamesInUseAsync(cancellationToken);
        var deleted = 0;
        foreach (var name in old.Where(name => !used.Contains(name)))
        {
            await store.DeleteAsync(name, cancellationToken);
            deleted++;
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} unused images", deleted);
        }

        return deleted;
    }

    private async Task<HashSet<string>> NamesInUseAsync(CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var onQuestions = db.Questions.AsNoTracking().Where(q => q.ImageUrl != null).Select(q => q.ImageUrl!);
        var onSlides = db.Slides.AsNoTracking().Where(s => s.Content.Contains(ImageService.RoutePrefix)).Select(s => s.Content);

        // A session shows the copy of the quiz or deck it started with, so it can still need an image that has since
        // been taken off the original. Once it ends, nothing shows that copy again.
        var inSessions = db.LiveSessions.AsNoTracking().Where(s => s.Status != SessionStatus.Ended).Select(s => s.SnapshotJson);

        foreach (var source in new[] { onQuestions, onSlides, inSessions })
        {
            await foreach (var text in source.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                foreach (Match match in ImageName().Matches(text))
                {
                    used.Add(match.Value);
                }
            }
        }

        return used;
    }
}

/// <summary>Runs the cleanup shortly after the app starts, and then a few times a day.</summary>
public sealed class ImageCleanupWorker(ImageCleanup cleanup, TimeProvider timeProvider, ILogger<ImageCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    await cleanup.RunAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Unused images only cost storage, so a failed run just waits for the next one.
                    logger.LogError(ex, "Image cleanup failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
