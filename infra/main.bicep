// CohortCompanion on Azure: one Linux App Service, Azure SQL, Blob Storage for images, and monitoring.
// The app reaches SQL and Storage with its managed identity, so no connection string holds a password.
// See docs/DEPLOYMENT.md.

targetScope = 'resourceGroup'

@description('Short name used to build resource names. Lowercase letters and digits only.')
@minLength(3)
@maxLength(12)
param appName string = 'cohortcomp'

@description('Azure region. Keep this in the US: see the FERPA section of docs/PLAN.md.')
@allowed([
  'centralus'
  'eastus'
  'eastus2'
  'northcentralus'
  'southcentralus'
  'westus'
  'westus2'
  'westus3'
])
param location string = 'centralus'

@description('App Service plan size. B1 suits the demo; see docs/DEPLOYMENT.md for moving up.')
@allowed([
  'B1'
  'B2'
  'B3'
  'S1'
  'P0v3'
  'P1v3'
])
param planSku string = 'B1'

@description('Azure SQL database size. Basic is the cheapest; see docs/DEPLOYMENT.md for moving up.')
@allowed([
  'Basic'
  'S0'
  'S1'
])
param databaseSku string = 'Basic'

@description('Display name of the Microsoft Entra user or group that administers the SQL server.')
param sqlAdminLogin string

@description('Object id of that Microsoft Entra user or group.')
param sqlAdminObjectId string

@description('Email that may register as the first admin without an invite code.')
param bootstrapAdminEmail string

var suffix = uniqueString(resourceGroup().id)
var webAppName = '${appName}-${suffix}'
var sqlServerName = '${appName}-sql-${suffix}'
var databaseName = 'CohortCompanion'
var storageName = take('${appName}${suffix}', 24)
var keyVaultName = take('${appName}-kv-${suffix}', 24)
var imageContainerName = 'images'

// Built-in role definition ids.
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    // Keeps the default IP masking: see the FERPA section of docs/PLAN.md.
    DisableIpMasking: false
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource imageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      principalType: 'Group'
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: databaseSku
    tier: databaseSku == 'Basic' ? 'Basic' : 'Standard'
  }
}

// Lets the App Service reach the server. Access still requires Microsoft Entra sign-in.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan-${suffix}'
  location: location
  kind: 'linux'
  sku: { name: planSku }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // Live sessions are held in memory, so a client must stay on the instance it connected to.
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      webSocketsEnabled: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/privacy'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        // The app sits behind App Service's proxy. This restores the real scheme and client address,
        // which rate limiting and QR codes depend on.
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION', value: '~3' }
        { name: 'Auth__BootstrapAdminEmail', value: bootstrapAdminEmail }
        { name: 'Database__MigrateOnStartup', value: 'true' }
        { name: 'DataProtection__KeysPath', value: '/home/data-protection-keys' }
        { name: 'Images__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Images__BlobContainer', value: imageContainerName }
      ]
      connectionStrings: [
        {
          name: 'DefaultConnection'
          type: 'SQLAzure'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
        }
      ]
    }
  }
}

resource imagesRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, webApp.id, storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource secretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output sqlServerName string = sqlServer.name
output databaseName string = databaseName
