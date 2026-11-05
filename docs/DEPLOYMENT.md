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
- **Before relying on it with a large class**, load test it: a few hundred simulated clients answering at the same
  moment. The capacity figures so far are estimates.

## Incident response

The FERPA section of the plan calls for a written contact and process in case of a breach. That has to be
decided by people, not code, so **name a contact here before real classes use the app.**
