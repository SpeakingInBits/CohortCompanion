using Microsoft.AspNetCore.SignalR.Client;

namespace CohortCompanion.Web.Client.Live;

/// <summary>Keeps trying to reconnect for as long as the page is open: phones sleep and classroom Wi-Fi drops.</summary>
public sealed class ForeverRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount < Delays.Length ? Delays[retryContext.PreviousRetryCount] : TimeSpan.FromSeconds(10);
}
