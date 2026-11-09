using System.Security.Claims;
using CohortCompanion.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CohortCompanion.Web.Auth;

/// <summary>
/// Puts the instructor's display name in their sign-in cookie, so pages can greet them by name rather than by
/// email without a database lookup.
/// </summary>
public sealed class DisplayNameClaimsFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    public const string ClaimType = "cc:display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(ClaimType, user.DisplayName));
        }

        return identity;
    }
}

public static class DisplayNameExtensions
{
    /// <summary>The display name, or the email for a cookie issued before display names were added to it.</summary>
    public static string? DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayNameClaimsFactory.ClaimType) ?? user.Identity?.Name;
}
