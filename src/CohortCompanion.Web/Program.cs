using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Radzen;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Auth;
using CohortCompanion.Web.Components;
using CohortCompanion.Web.Components.Account;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Images;
using CohortCompanion.Web.Live;
using CohortCompanion.Web.Presentations;
using CohortCompanion.Web.Quizzes;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// Registers both IDbContextFactory (for interactive components) and a scoped ApplicationDbContext (for Identity).
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // No email service yet, so accounts are not confirmed. Invite codes gate registration instead.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InviteCodeService>();
builder.Services.AddScoped<InstructorAccessService>();

builder.Services.AddRadzenComponents();
builder.Services.AddSingleton<ContentSanitizer>();
builder.Services.AddScoped<QuizService>();
builder.Services.AddScoped<PresentationService>();

var imageOptions = builder.Configuration.GetSection(ImageOptions.SectionName).Get<ImageOptions>() ?? new ImageOptions();
builder.Services.AddSingleton<IImageStore>(string.IsNullOrWhiteSpace(imageOptions.BlobServiceUri)
    ? new LocalImageStore(Path.Combine(builder.Environment.ContentRootPath, imageOptions.LocalPath))
    : new BlobImageStore(imageOptions));
builder.Services.AddSingleton<ImageService>();

builder.Services.AddSingleton<LiveRateLimiter>();
builder.Services.AddSingleton<HubRateLimitFilter>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.AddFilter<HubRateLimitFilter>();
});
builder.Services.AddScoped<SessionHistoryService>();
builder.Services.AddSingleton<AuditLog>();

// App Service restarts would otherwise invalidate sign-in cookies, so keys live on persistent storage there.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("CohortCompanion");
}
builder.Services.AddSingleton<ISessionBroadcaster, HubSessionBroadcaster>();
builder.Services.AddSingleton<LiveSessionManager>();
builder.Services.AddHostedService<LiveSessionTicker>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    // Join codes appear in URLs, so they aren't passed on to any site a slide links to.
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(CohortCompanion.Web.Client._Imports).Assembly);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapHub<SessionHub>(HubEvents.HubPath);
app.MapJoinQrCode();
app.MapImages();

await app.InitializeDatabaseAsync();

app.Run();
