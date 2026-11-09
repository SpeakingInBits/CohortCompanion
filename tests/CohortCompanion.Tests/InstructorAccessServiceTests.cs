using CohortCompanion.Web.Auth;
using CohortCompanion.Web.Components.Account.Shared;
using CohortCompanion.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CohortCompanion.Tests;

public sealed class InstructorAccessServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private ServiceProvider services = default!;
    private AsyncServiceScope scope;
    private UserManager<ApplicationUser> users = default!;
    private InstructorAccessService access = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<AuditLog>()
            .AddScoped<InstructorAccessService>()
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .Services
            .BuildServiceProvider();

        scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            await roles.CreateAsync(new IdentityRole(role));
        }

        users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        access = scope.ServiceProvider.GetRequiredService<InstructorAccessService>();
    }

    public async Task DisposeAsync()
    {
        await scope.DisposeAsync();
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private async Task<string> UserAsync(string name, params string[] roles)
    {
        var user = new ApplicationUser { UserName = $"{name}@example.com", Email = $"{name}@example.com", DisplayName = name };
        await users.CreateAsync(user);
        await users.AddToRolesAsync(user, roles);
        return user.Id;
    }

    private async Task<IList<string>> RolesOfAsync(string id) => await users.GetRolesAsync((await users.FindByIdAsync(id))!);

    [Fact]
    public async Task MakeAdmin_AlsoMakesThemAnInstructor()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);
        var other = await UserAsync("other");

        Assert.Equal(AccessChange.Done, await access.MakeAdminAsync(admin, other));

        Assert.Equal([Roles.Admin, Roles.Instructor], (await RolesOfAsync(other)).Order());
    }

    [Fact]
    public async Task RemoveAdmin_KeepsThemTeaching()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);
        var other = await UserAsync("other", Roles.Admin, Roles.Instructor);

        Assert.Equal(AccessChange.Done, await access.RemoveAdminAsync(admin, other));

        Assert.Equal([Roles.Instructor], await RolesOfAsync(other));
    }

    [Fact]
    public async Task RemoveAdmin_NeverRemovesTheLastOne()
    {
        // Someone who isn't an admin can't reach the page, but the rule holds anyway.
        var outsider = await UserAsync("outsider", Roles.Instructor);
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);

        Assert.Equal(AccessChange.LastAdmin, await access.RemoveAdminAsync(outsider, admin));
        Assert.Contains(Roles.Admin, await RolesOfAsync(admin));
    }

    [Fact]
    public async Task Admins_CannotChangeTheirOwnAccess()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);
        await UserAsync("second", Roles.Admin, Roles.Instructor);

        Assert.Equal(AccessChange.Self, await access.RemoveAdminAsync(admin, admin));
        Assert.Equal(AccessChange.Self, await access.SetInstructorAsync(admin, admin, false));
        Assert.Equal([Roles.Admin, Roles.Instructor], (await RolesOfAsync(admin)).Order());
    }

    [Fact]
    public async Task RemovingInstructorAccess_FromAnAdmin_IsRefused()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);
        var other = await UserAsync("other", Roles.Admin, Roles.Instructor);

        Assert.Equal(AccessChange.StillAdmin, await access.SetInstructorAsync(admin, other, false));
        Assert.Contains(Roles.Instructor, await RolesOfAsync(other));
    }

    [Fact]
    public async Task InstructorAccess_CanBeRemovedAndRestored()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);
        var teacher = await UserAsync("teacher", Roles.Instructor);
        var stampBefore = (await users.FindByIdAsync(teacher))!.SecurityStamp;

        Assert.Equal(AccessChange.Done, await access.SetInstructorAsync(admin, teacher, false));
        Assert.Empty(await RolesOfAsync(teacher));
        Assert.NotEqual(stampBefore, (await users.FindByIdAsync(teacher))!.SecurityStamp);

        Assert.Equal(AccessChange.Done, await access.SetInstructorAsync(admin, teacher, true));
        Assert.Equal([Roles.Instructor], await RolesOfAsync(teacher));
    }

    [Fact]
    public async Task UnknownUser_IsNotFound()
    {
        var admin = await UserAsync("admin", Roles.Admin, Roles.Instructor);

        Assert.Equal(AccessChange.NotFound, await access.MakeAdminAsync(admin, "missing"));
    }

    [Fact]
    public async Task EnsureAdminsTeach_RepairsAdminsWithoutTheInstructorRole()
    {
        var admin = await UserAsync("admin", Roles.Admin);

        await access.EnsureAdminsTeachAsync();

        Assert.Equal([Roles.Admin, Roles.Instructor], (await RolesOfAsync(admin)).Order());
    }

    [Fact]
    public void PasswordRules_DescribeIdentitysDefaults() =>
        Assert.Equal(
            "At least 6 characters, with an uppercase letter, a lowercase letter, a number and a symbol such as ! or #.",
            PasswordRules.Describe(new PasswordOptions()));

    [Fact]
    public void PasswordRules_DescribeLooserSettings() =>
        Assert.Equal(
            "At least 12 characters.",
            PasswordRules.Describe(new PasswordOptions
            {
                RequiredLength = 12,
                RequireDigit = false,
                RequireLowercase = false,
                RequireUppercase = false,
                RequireNonAlphanumeric = false,
            }));
}
