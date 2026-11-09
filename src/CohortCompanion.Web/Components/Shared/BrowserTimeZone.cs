using Microsoft.JSInterop;

namespace CohortCompanion.Web.Components.Shared;

/// <summary>
/// The time zone of the browser on the other end of this circuit, asked for once and then remembered, so times
/// can show as the instructor's own clock rather than the server's, which runs on UTC in Azure.
/// </summary>
public sealed class BrowserTimeZone(IJSRuntime js)
{
    // Shared, so a table full of times asks the browser once. A circuit runs one thing at a time, so no lock.
    private Task<TimeZoneInfo?>? asking;

    /// <summary>Null until the browser can be asked, which is only once a component has rendered interactively.</summary>
    public Task<TimeZoneInfo?> GetAsync()
    {
        if (asking is { IsCompletedSuccessfully: true, Result: null })
        {
            // It couldn't be asked last time, for example while prerendering. Try again.
            asking = null;
        }

        return asking ??= AskAsync();
    }

    private async Task<TimeZoneInfo?> AskAsync()
    {
        try
        {
            var id = await js.InvokeAsync<string?>("cc.timeZone");
            return id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or JSDisconnectedException)
        {
            // No browser to ask. Times stay in UTC, labelled as such.
            return null;
        }
    }
}
