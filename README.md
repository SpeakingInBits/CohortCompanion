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
| `src/CohortCompanion.Web` | Server: Identity, EF Core, admin and instructor UI |
| `src/CohortCompanion.Web.Client` | WebAssembly client for the student-facing pages |
| `src/CohortCompanion.Shared` | Types shared by server and client |
| `tests/CohortCompanion.Tests` | xUnit tests |

## Common tasks

```bash
dotnet test
```

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/CohortCompanion.Web --output-dir Data/Migrations
```
