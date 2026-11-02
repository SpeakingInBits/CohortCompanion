using CohortCompanion.Web.Auth;

namespace CohortCompanion.Web.Data;

public class InviteCode
{
    public int Id { get; set; }

    /// <summary>Normalized code: uppercase, no separator (for example <c>K7M4QXR2</c>).</summary>
    public string Code { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>Lowercased email the code is restricted to, or null for anyone.</summary>
    public string? LockedToEmail { get; set; }

    public int MaxUses { get; set; } = 1;

    public int UseCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string CreatedById { get; set; } = "";

    public ApplicationUser? CreatedBy { get; set; }

    public List<ApplicationUser> RedeemedBy { get; set; } = [];

    public InviteCodeStatus GetStatus(DateTime nowUtc) =>
        RevokedAtUtc is not null ? InviteCodeStatus.Revoked
        : ExpiresAtUtc <= nowUtc ? InviteCodeStatus.Expired
        : UseCount >= MaxUses ? InviteCodeStatus.UsedUp
        : InviteCodeStatus.Active;

    /// <summary>Checks whether this code can be redeemed by <paramref name="email"/> right now.</summary>
    public InviteCodeCheck CheckUsableBy(string email, DateTime nowUtc)
    {
        var status = GetStatus(nowUtc);
        if (status != InviteCodeStatus.Active)
        {
            return (InviteCodeCheck)status;
        }

        if (LockedToEmail is not null && !string.Equals(LockedToEmail, InviteCodes.NormalizeEmail(email), StringComparison.Ordinal))
        {
            return InviteCodeCheck.WrongEmail;
        }

        return InviteCodeCheck.Ok;
    }
}

public enum InviteCodeStatus
{
    Active = 0,
    Revoked = 1,
    Expired = 2,
    UsedUp = 3,
}

/// <summary>Result of checking a code. Values 1–3 deliberately match <see cref="InviteCodeStatus"/>.</summary>
public enum InviteCodeCheck
{
    Ok = 0,
    Revoked = 1,
    Expired = 2,
    UsedUp = 3,
    NotFound = 4,
    WrongEmail = 5,
}
