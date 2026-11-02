using Microsoft.AspNetCore.Identity;

namespace CohortCompanion.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The invite code used to register, if any. The bootstrap admin has none.</summary>
    public int? InviteCodeId { get; set; }

    public InviteCode? InviteCode { get; set; }
}
