# Deploying to Azure

This sets up the demo environment for testers. [infra/main.bicep](../infra/main.bicep) creates everything; two
steps have to be done by hand, because they need your own Azure sign-in.

> The demo is for testers, not real graded classes. See "Who operates the app" in [PLAN.md](PLAN.md).

## What gets created

| Resource | Notes |
|---|---|
| App Service plan and Linux web app | B1 by default, with WebSockets, Always On, and session affinity on |
| Azure SQL server and database | Microsoft Entra sign-in only, so there's no SQL password. Basic by default |
| Storage account | Private `images` container. Images are served through the app, from our own domain |
| Key Vault | Empty for now. Microsoft sign-in's client secret goes here in milestone 8 |
| Application Insights | Default IP masking stays on |

**Cost:** check current pricing before you deploy. B1 and the database are the main costs, and both bill
for as long as they exist, even when nobody is using the app.

## 1. Create the resources

Requirements: the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), signed in with `az login`.

You need a Microsoft Entra **group** to administer the SQL server, and you should be a member of it. Note its
name and object id.

```bash
az group create --name cohortcompanion-demo --location centralus
```

```bash
az deployment group create --resource-group cohortcompanion-demo --template-file infra/main.bicep --parameters sqlAdminLogin="<group name>" sqlAdminObjectId="<group object id>" bootstrapAdminEmail="<your email>"
```

The output includes `webAppName`, `webAppUrl`, `sqlServerName`, and `databaseName`.

## 2. Let the app use the database

The app signs in to SQL as its managed identity, and that identity needs a database user. Bicep can't create
one, so run this once against the database, signed in as a member of the admin group. The Query editor in the
Azure portal is the easiest way.

```sql
CREATE USER [<webAppName>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [<webAppName>];
```

If your network blocks port 1433, as a campus firewall may, use Azure Cloud Shell. It runs inside Azure, so the server
already allows it and you don't need a firewall rule. Cloud Shell may not have `sqlcmd`, but its PowerShell can do this
with nothing installed. Run `pwsh`, then paste this, filling in `<sqlServerName>` and `<webAppName>`. It prints `-1` when
it works.

```powershell
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$conn = [System.Data.SqlClient.SqlConnection]::new("Server=tcp:<sqlServerName>.database.windows.net,1433;Database=CohortCompanion;Encrypt=True;")
$conn.AccessToken = $token
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "CREATE USER [<webAppName>] FROM EXTERNAL PROVIDER; ALTER ROLE db_owner ADD MEMBER [<webAppName>];"
$cmd.ExecuteNonQuery()
$conn.Close()
```

The app can't start until this is done, so if it was already deployed, restart it afterward.

`db_owner` is needed because the app applies migrations at startup (`Database:MigrateOnStartup`). The Deploy workflow
applies them first, so this finds nothing to do after a workflow deploy, but deploying from your machine still relies
on it. For the school's production setup, consider turning that setting off and granting the app only `db_datareader`
and `db_datawriter`.

## 3. Deploy the app

**From your machine:**

```bash
dotnet publish src/CohortCompanion.Web --configuration Release --output publish
```

Zip the contents of `publish`, then:

```bash
az webapp deploy --resource-group cohortcompanion-demo --name <webAppName> --src-path publish.zip --type zip
```

**Or from GitHub Actions:** run the **Deploy** workflow from the Actions tab, on `main`. It runs the tests, applies any
new database migrations, deploys the app, and waits for the site to answer. If a migration fails, the app isn't deployed.

Once the site answers, the workflow creates a GitHub release with a `vX.Y.Z` tag and a list of the changes since the last
release. Choose what's in the release when you start the run, which picks the version number:

| Choice | Example |
|---|---|
| Bug fixes | `v1.2.3` to `v1.2.4` |
| New features | `v1.2.3` to `v1.3.0` |
| Breaking changes | `v1.2.3` to `v2.0.0` |

To choose the number yourself, enter it under **Version number**. It has to be newer than the latest release. If a deploy
fails, no release is created, and the next run uses the same number. If only the release fails, re-run that failed job.

Migrations run before the new code goes live, so the old code briefly runs against the new schema. A column the app
has stopped using is dropped in the same release, rather than kept for one more. Until the deploy finishes, pages of the
old code that still read that column can fail, so deploy when no class is running.

It needs a one-time setup:

1. Create a Microsoft Entra app registration with a federated credential for this repository's `demo` environment.
   Give it the Website Contributor role on the web app, and SQL Security Manager on the SQL server. The second role
   lets the workflow open the database firewall to its runner, and close it again.

   This repository uses GitHub's immutable subject format, so the credential's subject has to include the numeric ids:
   `repo:SpeakingInBits@7307281/CohortCompanion@1443163509:environment:demo`. If sign-in fails with `AADSTS700213`,
   compare this with the subject shown in the workflow log.
2. Give the app registration a database user, the same way as in step 2, using its display name. It needs `db_owner`
   to apply migrations.
3. In the repository, create an environment named `demo`, and limit its deployment branches to `main`.
4. In that environment, add the secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`, and the
   variables `AZURE_WEBAPP_NAME`, `AZURE_RESOURCE_GROUP`, and `AZURE_SQL_SERVER`.

## 4. First sign-in

Open `<webAppUrl>/Account/Register` and register with the bootstrap email. You'll be the admin, and can create
invite codes for your testers.

## Running it

- **Deploy outside class hours.** A deployment restarts the app, and B1 has no deployment slots. Live sessions recover
  and students reconnect by themselves, but they'll see a pause. On B1, the site returned 500 errors for about two
  minutes during a deployment, until the new version finished starting.
- **Scaling up** is a settings change: redeploy the Bicep with a different `planSku`. It can restart the app. Check the
  connection limit for the size you choose: S1 and P1v3 have the same limit as B1.
- **Moving the database up** is also a settings change: redeploy the Bicep with a different `databaseSku`. Basic has
  only 5 DTUs and 2 GB, so move to S0 or higher before a large class.
- **Scaling out is not supported yet.** Live sessions are held in the memory of one instance. Running more than one
  needs Azure SignalR Service and shared session state.
- **Before relying on it with a large class**, try it on the real plan. See "Load testing" below for what has and
  hasn't been measured.

## Load testing

`tests/CohortCompanion.LoadTest` runs quiz sessions with simulated students, then reports latency, CPU, memory, and
failures. It starts a copy of the real app in a child process, pinned to one CPU core with a 1.25 GB heap limit, so
the runtime sees something close to a B1. The simulated students run on the other cores.

```bash
dotnet run --configuration Release --project tests/CohortCompanion.LoadTest -- --students 500
```

To simulate many classes at once, which is the normal pattern at a school:

```bash
dotnet run --configuration Release --project tests/CohortCompanion.LoadTest -- --students 50 --classes 20 --stagger 30 --db-delay 10
```

Options:

| Option | Meaning |
|---|---|
| `--students <n>` | Students in each class |
| `--classes <n>` | How many classes run at the same time, each its own session. The app allows 3000 joins a minute per address, and every simulated student shares one |
| `--stagger <seconds>` | Starts each class at a random moment within this window. Without it, they all start together |
| `--db-delay <ms>` | Adds a delay to every database command, because LocalDB answers far faster than Azure SQL |
| `--out <file>` | Also appends the results to a file |

It needs LocalDB, and it deletes and recreates its own `CohortCompanionLoadTest` database on every run. Instructor
commands go through control endpoints that exist only in this test host, not in the app itself.

Each session has four questions: students answer three of them spread over 5 seconds, and one all at the same instant.
The last question closes on its timer, which shows how late the background tick runs. Everyone in a class joins at
once, and everyone drops and reconnects at once near the end. That's harsher than a real class.

### Results

Measured on 2026-11-05, on one thread of a 2.7 GHz Intel Core i7-6820HQ laptop with a scored quiz. Times are the 95th
percentile. There were no failures in any run: every join, answer, reconnect, and event got through.

**Many classes at once**, with 50 students in each and about 15 ms added to each database command. This is the
pattern to expect. Each row is one representative run, and there were 12 runs in all.

| Classes | Students | Classes start | Slowest class joined in | Question reaches students | Answer, everyone at once | Results reach students | Timer closes late by | Average CPU | Peak memory |
|---|---|---|---|---|---|---|---|---|---|
| 6 | 300 | Within 30 s | 1.1 s | 34 ms | 25 ms | 34 ms | 250 ms | 30% | 194 MB |
| 20 | 1000 | Within 5 s | 2.0 s | 54 ms | 16 ms | 67 ms | 252 ms | 43% | 224 MB |
| 20 | 1000 | Same moment | 4.7 s | 48 ms | 25 ms | 50 ms | 297 ms | 54% | 231 MB |
| 30 | 1500 | Within 30 s | 1.3 s | 43 ms | 48 ms | 49 ms | 251 ms | 42% | 239 MB |

The tick runs every 250 ms, so a timer closing up to about that late is expected.

**One large session**, with no added database delay. These figures come from three-question runs, before the timed
question was added:

| Students | Everyone joined in | Question reaches students | Answer, everyone at once | Results reach students | Reconnect | Average CPU | Peak memory |
|---|---|---|---|---|---|---|---|
| 100 | 0.6 s | 40 ms | 16 ms | 31 ms | 178 ms | 15% | 171 MB |
| 300 | 1.8 s | 44 ms | 44 ms | 50 ms | 310 ms | 29% | 189 MB |
| 500 | 3.0 s | 52 ms | 86 ms | 66 ms | 335 ms | 45% | 206 MB |
| 1000 | 4.9 s | 94 ms | 71 ms | 106 ms | 527 ms | 59% | 252 MB |

With 10 ms added to each database command, which works out to about 15 ms on Windows:

| Students | Everyone joined in | Answer, everyone at once | Results reach students | Slowest instructor command |
|---|---|---|---|---|
| 300 | 5.4 s | 31 ms | 61 ms | 172 ms |
| 500 | 8.4 s | 71 ms | 80 ms | 433 ms |
| 1000 | 17.1 s | 91 ms | 116 ms | 579 ms |

What this shows:

- **Hub traffic isn't the limit on one core, up to 1500 students.** CPU reached 100% only in short bursts, and memory
  stayed far below B1's 1.75 GB.
- **Classes don't slow each other down.** Each session ticks separately, so a class waiting on the database can't delay
  another class's timers. Before this was fixed, one class's deadlock made timers everywhere up to 14 seconds late.
- **Many small classes are easier than one big one.** Joining happens one at a time within a session, so 1000 students
  across 20 classes get in far faster than 1000 in one session.
- **Database latency slows joining.** Each join writes to the database while holding the session's lock, so joins
  happen one at a time. Answers are unaffected, because they're saved in batches. Real classes join over half a
  minute or so, which should hide this.
- **Saving a batch of answers is the slowest instructor command**, because closing a question waits for it.

### What this doesn't tell you

These are local numbers, and they don't represent a real B1, whose core is shared and may well be slower. Not measured:

- **Downloading the student client.** About 3 MB compressed per student, which is likely the first thing to hurt on
  B1 or on classroom Wi-Fi. Everyone's first visit happens at the same moment.
- **Real networks.** Everything here ran over loopback, with no Wi-Fi, TLS, or App Service proxy.
- **Azure SQL itself.** The delay above simulates latency, not the database tier's throughput limits.
- **Real browsers, the host page, or the projector** during the test.

More than 3000 students behind one campus address can't all join within a minute, because of the rate limit.

## Incident response

The FERPA section of the plan calls for a written contact and process in case of a breach. That has to be
decided by people, not code, so **name a contact here before real classes use the app.**
