namespace CohortCompanion.Web.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Email that may register without an invite code and becomes the first admin.
    /// Only honored while no admin exists.
    /// </summary>
    public string? BootstrapAdminEmail { get; set; }
}
