using CohortCompanion.Web.Auth;
using CohortCompanion.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class InviteCodeServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private TestDbContextFactory dbFactory = default!;
    private InviteCodeService service = default!;
    private string adminId = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        dbFactory = new TestDbContextFactory(connection);

        await using var db = dbFactory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var admin = new ApplicationUser { UserName = "admin@example.com", Email = "admin@example.com", DisplayName = "Admin" };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        adminId = admin.Id;

        service = new InviteCodeService(dbFactory, time);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task Redeem_SingleUseCode_SucceedsOnceThenReportsUsedUp()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 1, expiresInDays: 14);

        var first = await service.TryRedeemAsync(invite.Code, "a@example.com");
        var second = await service.TryRedeemAsync(invite.Code, "b@example.com");

        Assert.Equal(InviteCodeCheck.Ok, first.Result);
        Assert.Equal(invite.Id, first.InviteCodeId);
        Assert.Equal(InviteCodeCheck.UsedUp, second.Result);
        Assert.Null(second.InviteCodeId);
    }

    [Fact]
    public async Task Redeem_AcceptsCodeTypedWithLowercaseAndDash()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 1, expiresInDays: 14);
        var typed = InviteCodes.Format(invite.Code).ToLowerInvariant();

        var (result, _) = await service.TryRedeemAsync(typed, "a@example.com");

        Assert.Equal(InviteCodeCheck.Ok, result);
    }

    [Fact]
    public async Task Redeem_MultiUseCode_StopsAtMaxUses()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 3, expiresInDays: 14);

        var results = new List<InviteCodeCheck>();
        for (var i = 0; i < 5; i++)
        {
            results.Add((await service.TryRedeemAsync(invite.Code, $"user{i}@example.com")).Result);
        }

        Assert.Equal(3, results.Count(r => r == InviteCodeCheck.Ok));
        Assert.Equal(2, results.Count(r => r == InviteCodeCheck.UsedUp));
    }

    [Fact]
    public async Task Redeem_EmailLockedCode_RejectsOtherEmailsAndIgnoresCase()
    {
        var invite = await service.CreateAsync(adminId, null, " Pat@Example.com ", maxUses: 1, expiresInDays: 14);

        var wrong = await service.TryRedeemAsync(invite.Code, "someone@example.com");
        var right = await service.TryRedeemAsync(invite.Code, "PAT@example.COM");

        Assert.Equal(InviteCodeCheck.WrongEmail, wrong.Result);
        Assert.Equal(InviteCodeCheck.Ok, right.Result);
    }

    [Fact]
    public async Task Redeem_ExpiredCode_IsRejected()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 1, expiresInDays: 1);
        time.Advance(TimeSpan.FromDays(1));

        var (result, _) = await service.TryRedeemAsync(invite.Code, "a@example.com");

        Assert.Equal(InviteCodeCheck.Expired, result);
    }

    [Fact]
    public async Task Redeem_RevokedCode_IsRejected()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 1, expiresInDays: 14);
        Assert.True(await service.RevokeAsync(invite.Id));

        var (result, _) = await service.TryRedeemAsync(invite.Code, "a@example.com");

        Assert.Equal(InviteCodeCheck.Revoked, result);
    }

    [Fact]
    public async Task Redeem_UnknownCode_IsNotFound()
    {
        var (result, _) = await service.TryRedeemAsync("ZZZZ-ZZZZ", "a@example.com");

        Assert.Equal(InviteCodeCheck.NotFound, result);
    }

    [Fact]
    public async Task Release_GivesBackARedemption()
    {
        var invite = await service.CreateAsync(adminId, null, null, maxUses: 1, expiresInDays: 14);
        var (_, id) = await service.TryRedeemAsync(invite.Code, "a@example.com");

        await service.ReleaseAsync(id!.Value);
        var (result, _) = await service.TryRedeemAsync(invite.Code, "a@example.com");

        Assert.Equal(InviteCodeCheck.Ok, result);
    }

    [Fact]
    public async Task Create_NormalizesNoteAndEmailAndSetsExpiry()
    {
        var invite = await service.CreateAsync(adminId, "  ", "Pat@Example.com", maxUses: 2, expiresInDays: 7);

        Assert.Null(invite.Note);
        Assert.Equal("pat@example.com", invite.LockedToEmail);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddDays(7), invite.ExpiresAtUtc);
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options =
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;

        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
