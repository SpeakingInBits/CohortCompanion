namespace CohortCompanion.Web.Auth;

/// <summary>
/// A record of administrator actions, written to the application log (and so to Application Insights in
/// Azure) under the <c>CohortCompanion.Audit</c> category. It holds ids only, never names or emails.
/// </summary>
public sealed class AuditLog(ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger("CohortCompanion.Audit");

    public void Record(string actorId, string action, string target) =>
        logger.LogInformation("Admin {ActorId} performed {Action} on {Target}.", actorId, action, target);
}
