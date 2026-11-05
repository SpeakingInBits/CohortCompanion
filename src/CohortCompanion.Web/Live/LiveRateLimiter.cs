using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;

namespace CohortCompanion.Web.Live;

/// <summary>
/// Limits how fast students can call the hub. A whole class usually shares one IP address behind campus
/// Wi-Fi, so the per-IP limits are high and only stop floods; the tight limits are per connection, which
/// is what actually catches one misbehaving client. Everything stays in memory and nothing is logged, so
/// IP addresses are never stored.
/// </summary>
public sealed class LiveRateLimiter : IDisposable
{
    public const int JoinsPerConnection = 10;
    public const int JoinsPerAddress = 1000;

    // Rejoining has its own, larger allowance: when Wi-Fi drops, a whole lecture comes back in the same few
    // seconds, often just after joining. It also needs a valid token, so there's little for a limit to protect.
    public const int RejoinsPerConnection = 10;
    public const int RejoinsPerAddress = 10_000;
    public const int AnswersPerConnection = 30;
    public const int AnswersPerAddress = 5000;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, k => new FixedWindowRateLimiterOptions
        {
            PermitLimit = LimitFor(k),
            Window = Window,
            QueueLimit = 0,
        }));

    private static int LimitFor(string key) => key[..2] switch
    {
        "jc" => JoinsPerConnection,
        "ja" => JoinsPerAddress,
        "rc" => RejoinsPerConnection,
        "ra" => RejoinsPerAddress,
        "ac" => AnswersPerConnection,
        _ => AnswersPerAddress,
    };

    public bool TryJoin(string connectionId, string? address) => TryAcquire("jc", "ja", connectionId, address);

    public bool TryRejoin(string connectionId, string? address) => TryAcquire("rc", "ra", connectionId, address);

    public bool TryAnswer(string connectionId, string? address) => TryAcquire("ac", "aa", connectionId, address);

    private bool TryAcquire(string connectionPrefix, string addressPrefix, string connectionId, string? address)
    {
        using var perConnection = limiter.AttemptAcquire($"{connectionPrefix}:{connectionId}");
        if (!perConnection.IsAcquired)
        {
            return false;
        }

        using var perAddress = limiter.AttemptAcquire($"{addressPrefix}:{address ?? "unknown"}");
        return perAddress.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}

/// <summary>Applies <see cref="LiveRateLimiter"/> to the hub methods students can call.</summary>
public sealed class HubRateLimitFilter(LiveRateLimiter limiter) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var connectionId = invocationContext.Context.ConnectionId;
        var address = invocationContext.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();

        var allowed = invocationContext.HubMethodName switch
        {
            nameof(SessionHub.Join) => limiter.TryJoin(connectionId, address),
            nameof(SessionHub.Rejoin) => limiter.TryRejoin(connectionId, address),
            nameof(SessionHub.SubmitAnswer) => limiter.TryAnswer(connectionId, address),
            _ => true,
        };

        if (!allowed)
        {
            throw new HubException("Too many requests. Wait a moment and try again.");
        }

        return await next(invocationContext);
    }
}
