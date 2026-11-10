# CohortCompanion

An ASP.NET Core Blazor app for collaborative classroom activities. Instructors run slideshow
presentations that students follow on their own devices, and live quizzes (multiple choice, word cloud,
fill in the blank) that students join anonymously with a QR code or a short join link.

See [docs/PLAN.md](docs/PLAN.md) for the full plan and milestones.

## Getting started

Requirements: the .NET 10 SDK and SQL Server LocalDB (installed with Visual Studio's ASP.NET workload).

1. Choose the email for the first admin account. It can register without an invite code, but only
   while no admin exists yet:

   ```bash
   dotnet user-secrets set "Auth:BootstrapAdminEmail" "you@example.com" --project src/CohortCompanion.Web
   ```

2. Run the app. In Development, the database is created and migrated automatically on startup:

   ```bash
   dotnet run --project src/CohortCompanion.Web --launch-profile https
   ```

3. Open `/Account/Register` and register with the bootstrap email. You'll be the admin and can create
   invite codes for everyone else under **Invite codes**.

## Projects

| Project | Purpose |
|---|---|
| `src/CohortCompanion.Web` | Server: Identity, EF Core, instructor and admin UI, the live session engine and its SignalR hub |
| `src/CohortCompanion.Web.Client` | WebAssembly client for student devices and the projector, plus components the server pages share |
| `src/CohortCompanion.Shared` | Types shared by server and client |
| `tests/CohortCompanion.Tests` | xUnit tests |
| `infra` | Azure infrastructure in Bicep |

## Trying it out

The first time you sign in, the home page offers a short tour that walks you through the steps below. You can
take it again later with **Take the tour** on the home page.

1. Under **Quizzes**, create a quiz, add a question, and save it. Then choose **Run live**.
2. Open the join link in another browser or a private window, and enter a nickname.
3. On the host page, choose **Open projector view** for what the class sees, then **Start**. **Copy link**
   gives a projector link that works on another computer without signing in.

**Presentations** work the same way, with **Present**.

## Deploying

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Common tasks

```bash
dotnet test
```

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/CohortCompanion.Web --output-dir Data/Migrations
```
