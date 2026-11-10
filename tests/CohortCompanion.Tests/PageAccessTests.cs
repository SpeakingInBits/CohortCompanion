using System.Reflection;
using System.Security.Claims;
using CohortCompanion.Web.Auth;
using CohortCompanion.Web.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CohortCompanion.Tests;

/// <summary>
/// Who can open each page. The rules are read off the pages themselves and run through the real authorization
/// service, so a page that loses its rule, or a new page added without one, fails here.
/// </summary>
public class PageAccessTests
{
    /// <summary>Every page the server routes to, by route. Student and projector pages live in the client project.</summary>
    private static readonly Dictionary<string, Type> Pages = typeof(QuizService).Assembly.GetTypes()
        .SelectMany(type => type.GetCustomAttributes<RouteAttribute>().Select(route => (route.Template, type)))
        .ToDictionary(page => page.Template, page => page.type);

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedInAs(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), authenticationType: "Test"));

    private static IEnumerable<string> RoutesUnder(string prefix) => Pages.Keys.Where(route => route.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task<bool> CanOpenAsync(string route, ClaimsPrincipal user)
    {
        var rules = Pages[route].GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        if (rules.Count == 0)
        {
            return true;
        }

        await using var services = new ServiceCollection().AddLogging().AddAuthorizationCore().BuildServiceProvider();
        var policy = await AuthorizationPolicy.CombineAsync(services.GetRequiredService<IAuthorizationPolicyProvider>(), rules);
        return (await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy!)).Succeeded;
    }

    [Theory]
    [InlineData("/admin/invites")]
    [InlineData("/admin/instructors")]
    public async Task InviteCodesAndTheInstructorList_AreForAdminsOnly(string route)
    {
        Assert.True(await CanOpenAsync(route, SignedInAs(Roles.Admin, Roles.Instructor)));

        Assert.False(await CanOpenAsync(route, SignedInAs(Roles.Instructor)));
        Assert.False(await CanOpenAsync(route, SignedInAs()));
        Assert.False(await CanOpenAsync(route, Anonymous));
    }

    [Fact]
    public async Task EveryAdminPage_TurnsInstructorsAway()
    {
        var adminRoutes = RoutesUnder("/admin/").ToList();

        Assert.NotEmpty(adminRoutes);
        foreach (var route in adminRoutes)
        {
            Assert.False(await CanOpenAsync(route, SignedInAs(Roles.Instructor)), route);
        }
    }

    [Theory]
    [InlineData("/quizzes")]
    [InlineData("/quizzes/{Id:int}/edit")]
    [InlineData("/presentations")]
    [InlineData("/presentations/{Id:int}/edit")]
    [InlineData("/sessions")]
    [InlineData("/sessions/{Id:guid}/host")]
    [InlineData("/sessions/{Id:guid}/summary")]
    public async Task TeachingPages_NeedAnInstructor(string route)
    {
        Assert.True(await CanOpenAsync(route, SignedInAs(Roles.Instructor)));
        Assert.True(await CanOpenAsync(route, SignedInAs(Roles.Admin, Roles.Instructor)));

        // Someone whose access was removed keeps their account, with no role.
        Assert.False(await CanOpenAsync(route, SignedInAs()));
        Assert.False(await CanOpenAsync(route, Anonymous));
    }

    [Fact]
    public async Task AccountSettings_NeedSigningIn()
    {
        var settingsRoutes = RoutesUnder("/Account/Manage").ToList();

        Assert.NotEmpty(settingsRoutes);
        foreach (var route in settingsRoutes)
        {
            Assert.False(await CanOpenAsync(route, Anonymous), route);
            Assert.True(await CanOpenAsync(route, SignedInAs()), route);
        }
    }

    /// <summary>A new page has to be given a rule, or be added here on purpose.</summary>
    [Fact]
    public async Task OnlyTheHomePrivacyAndSignInPages_AreOpenToEveryone()
    {
        var open = new List<string>();
        foreach (var route in Pages.Keys)
        {
            if (await CanOpenAsync(route, Anonymous))
            {
                open.Add(route);
            }
        }

        Assert.Equal(
            ["/", "/Error", "/not-found", "/privacy"],
            open.Where(route => !route.StartsWith("/Account/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(open, route => route.StartsWith("/Account/Manage", StringComparison.Ordinal));
    }
}
