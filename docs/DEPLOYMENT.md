# Deploying to Azure

This sets up the demo environment for testers. [infra/main.bicep](../infra/main.bicep) creates everything; two
steps have to be done by hand, because they need your own Azure sign-in.

> The demo is for testers, not real graded classes. See "Who operates the app" in [PLAN.md](PLAN.md).

## What gets created

| Resource | Notes |
|---|---|
| App Service plan and Linux web app | B1 by default, with WebSockets, Always On, and session affinity on |
| Azure SQL server and database | Microsoft Entra sign-in only, so there's no SQL password. S0 by default |
| Storage account | Private `images` container. Images are served through the app, from our own domain |
| Key Vault | Empty for now. Microsoft sign-in's client secret goes here in milestone 8 |
| Application Insights | Default IP masking stays on |

**Cost:** check current pricing before you deploy. B1 and the S0 database are the main costs, and both bill
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

`db_owner` is needed because the app applies migrations at startup (`Database:MigrateOnStartup`). For the school's
production setup, consider running migrations from the deployment pipeline instead and granting the app only
`db_datareader` and `db_datawriter`.

## 3. Deploy the app

**From your machine:**

```bash
dotnet publish src/CohortCompanion.Web --configuration Release --output publish
```

Zip the contents of `publish`, then:

```bash
az webapp deploy --resource-group cohortcompanion-demo --name <webAppName> --src-path publish.zip --type zip
```

**Or from GitHub Actions:** the **Deploy** workflow runs the tests, publishes, and deploys. It needs a one-time
setup:

1. Create a Microsoft Entra app registration with a federated credential for this repository's `demo` environment.
   Give it the Website Contributor role on the web app.
2. In the repository, create an environment named `demo`.
3. Add the secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`, and the variable `AZURE_WEBAPP_NAME`.

## 4. First sign-in

Open `<webAppUrl>/Account/Register` and register with the bootstrap email. You'll be the admin, and can create
invite codes for your testers.

## Running it

- **Deploy outside class hours.** A deployment restarts the app, and B1 has no deployment slots. Live sessions recover
  and students reconnect by themselves, but they'll see a pause.
- **Scaling up** is a settings change: redeploy the Bicep with a different `planSku`. It can restart the app. Check the
  connection limit for the size you choose: S1 and P1v3 have the same limit as B1.
- **Scaling out is not supported yet.** Live sessions are held in the memory of one instance. Running more than one
  needs Azure SignalR Service and shared session state.
- **Before relying on it with a large class**, try it on the real plan. See "Load testing" below for what has and
  hasn't been measured.

## Load testing

`tests/CohortCompanion.LoadTest` runs one quiz session with simulated students, then reports latency, CPU, memory, and
failures. It starts a copy of the real app in a child process, pinned to one CPU core with a 1.25 GB heap limit, so
the runtime sees something close to a B1. The simulated students run on the other cores.

```bash
dotnet run --configuration Release --project tests/CohortCompanion.LoadTest -- --students 500
```

Options:

| Option | Meaning |
|---|---|
| `--students <n>` | Class size, up to 1000. The app allows 1000 joins a minute per address, and every simulated student shares one |
| `--db-delay <ms>` | Adds a delay to every database command, because LocalDB answers far faster than Azure SQL |
| `--out <file>` | Also appends the results to a file |

It needs LocalDB, and it deletes and recreates its own `CohortCompanionLoadTest` database on every run. Instructor
commands go through control endpoints that exist only in this test host, not in the app itself.

Each run has three questions: students answer two of them spread over 5 seconds, and one all at the same instant. All
of them join at once, and all of them drop and reconnect at once near the end. That's harsher than a real class.

### Results

Measured on 2026-11-05, on one thread of a 2.7 GHz Intel Core i7-6820HQ laptop with a scored quiz. Times are the 95th
percentile. There were no failures in any run: every join, answer, reconnect, and event got through.

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

- **Hub traffic isn't the limit on one core, up to 1000 students.** CPU reached 100% only in short bursts, and memory
  stayed far below B1's 1.75 GB.
- **Database latency slows joining.** Each join writes to the database while holding the session's lock, so joins
  happen one at a time. Answers are unaffected, because they're saved in batches. Real classes join over half a
  minute or so, which should hide this.
- **Saving a batch of answers is the slowest instructor command**, because closing a question waits for it.

### What this doesn't tell you

These are local numbers, and they don't represent a real B1, whose core is shared and may well be slower. Not measured:

- **Downloading the student client.** About 3 MB compressed per student, which is likely the first thing to hurt on
  B1 or on classroom Wi-Fi. Everyone's first visit happens at the same moment.
- **Real networks.** Everything here ran over loopback, with no Wi-Fi, TLS, or App Service proxy.
- **Azure SQL itself.** The delay above simulates latency, not the S0 tier's throughput limits.
- **Several sessions at once**, and real browsers, the host page, or the projector during the test.

A lecture of more than 1000 behind one campus address also can't all join within a minute, because of the rate limit.

## Incident response

The FERPA section of the plan calls for a written contact and process in case of a breach. That has to be
decided by people, not code, so **name a contact here before real classes use the app.**
