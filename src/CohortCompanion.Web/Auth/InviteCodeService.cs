using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Auth;

public sealed class InviteCodeService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time)
{
    public const int DefaultExpiryDays = 14;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<InviteCode> CreateAsync(
        string createdById,
        string? note,
        string? lockedToEmail,
        int maxUses,
        int expiresInDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUses, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(expiresInDays, 1);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        string code;
        do
        {
            code = InviteCodes.Generate();
        }
        while (await db.InviteCodes.AnyAsync(c => c.Code == code, cancellationToken));

        var now = UtcNow;
        var invite = new InviteCode
        {
            Code = code,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            LockedToEmail = string.IsNullOrWhiteSpace(lockedToEmail) ? null : InviteCodes.NormalizeEmail(lockedToEmail),
            MaxUses = maxUses,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(expiresInDays),
            CreatedById = createdById,
        };

        db.InviteCodes.Add(invite);
        await db.SaveChangesAsync(cancellationToken);
        return invite;
    }

    public async Task<List<InviteCode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InviteCodes
            .AsNoTracking()
            .Include(c => c.CreatedBy)
            .Include(c => c.RedeemedBy)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcNow;
        var updated = await db.InviteCodes
            .Where(c => c.Id == id && c.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAtUtc, now), cancellationToken);
        return updated == 1;
    }

    public async Task<InviteCodeCheck> CheckAsync(string code, string email, CancellationToken cancellationToken = default)
    {
        var normalized = InviteCodes.Normalize(code);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var invite = await db.InviteCodes.AsNoTracking().SingleOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        return invite?.CheckUsableBy(email, UtcNow) ?? InviteCodeCheck.NotFound;
    }

    /// <summary>
    /// Atomically uses up one redemption of <paramref name="code"/> for <paramref name="email"/>.
    /// Safe under concurrent registrations: a code is never redeemed more than <see cref="InviteCode.MaxUses"/> times.
    /// </summary>
    public async Task<(InviteCodeCheck Result, int? InviteCodeId)> TryRedeemAsync(
        string code,
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalized = InviteCodes.Normalize(code);
        var normalizedEmail = InviteCodes.NormalizeEmail(email);
        var now = UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.InviteCodes
            .Where(c => c.Code == normalized
                && c.RevokedAtUtc == null
                && c.ExpiresAtUtc > now
                && c.UseCount < c.MaxUses
                && (c.LockedToEmail == null || c.LockedToEmail == normalizedEmail))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UseCount, c => c.UseCount + 1), cancellationToken);

        if (updated == 1)
        {
            var id = await db.InviteCodes.Where(c => c.Code == normalized).Select(c => c.Id).SingleAsync(cancellationToken);
            return (InviteCodeCheck.Ok, id);
        }

        var reason = await CheckAsync(code, email, cancellationToken);
        // If the code looks fine now, another registration took the last use between the update and the check.
        return (reason == InviteCodeCheck.Ok ? InviteCodeCheck.UsedUp : reason, null);
    }

    /// <summary>Gives back a redemption, used when account creation fails after the code was redeemed.</summary>
    public async Task ReleaseAsync(int inviteCodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.InviteCodes
            .Where(c => c.Id == inviteCodeId && c.UseCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UseCount, c => c.UseCount - 1), cancellationToken);
    }

    public static string Describe(InviteCodeCheck check) => check switch
    {
        InviteCodeCheck.Ok => "The invite code is valid.",
        InviteCodeCheck.NotFound => "That invite code doesn't exist. Check it for typos.",
        InviteCodeCheck.Expired => "That invite code has expired. Ask an administrator for a new one.",
        InviteCodeCheck.Revoked => "That invite code has been revoked. Ask an administrator for a new one.",
        InviteCodeCheck.UsedUp => "That invite code has already been used. Ask an administrator for a new one.",
        InviteCodeCheck.WrongEmail => "That invite code was issued for a different email address.",
        _ => "That invite code can't be used.",
    };
}
