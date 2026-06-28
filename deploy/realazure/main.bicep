// Ephemeral real-Azure backends for the nightly `integration-real-azure` CI
// job (issues #153, #257). Deployed at resource-group scope into a per-run,
// uniquely-named resource group that the workflow deletes in an `if: always()`
// teardown. Provisions only what the RealAzure smoke matrix exercises:
//
//   S3       -> Blob Storage   (Storage account; module creates containers)
//   DynamoDB -> Cosmos DB      (serverless SQL account + a pre-created database)
//   SQS      -> Service Bus    (Standard namespace; module creates queues)
//   Kinesis  -> Event Hubs     (Standard namespace + a pre-created hub;
//                               CreateStream is unimplemented so the hub must
//                               already exist)
//
// Secrets (account keys / connection strings) are intentionally NOT emitted as
// deployment outputs — the workflow fetches them with `az ... keys list` and
// masks them. Outputs carry only the non-sensitive resource identifiers needed
// to locate each resource for the key lookups.

@description('Short prefix combined with a deterministic uniqueString for globally-unique resource names.')
@minLength(2)
@maxLength(5)
param baseName string = 'a2a'

@description('Location for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Cosmos SQL database the DynamoDB module targets. The module creates containers (tables) but NOT the database, so it must pre-exist.')
param cosmosDatabaseName string = 'dynamodb'

@description('Event Hub entity the Kinesis module targets. CreateStream is unimplemented, so the hub must pre-exist.')
param eventHubName string = 'kinesis-smoke'

@description('Object (principal) id of the service principal that the proxy authenticates as when a backend block uses AAD/Workload-Identity auth (the OIDC SP the workflow logs in with). When empty, no data-plane role assignments are created and the WorkloadIdentity E2E scenario is skipped — the shared-key/SAS smoke matrix is unaffected.')
param principalId string = ''

var suffix = uniqueString(resourceGroup().id)
var storageAccountName = toLower('${baseName}st${suffix}')
var serviceBusNamespaceName = '${baseName}-sb-${suffix}'
var cosmosAccountName = toLower('${baseName}-cosmos-${suffix}')
var eventHubsNamespaceName = '${baseName}-eh-${suffix}'
var keyVaultName = '${baseName}-kv-${suffix}'

// Built-in role definition ids for the AAD data-plane roles the proxy needs when a
// backend block authenticates with Workload Identity (issue #307). Event Hubs /
// Service Bus use Azure RBAC; Cosmos uses its own SQL RBAC plane (see below).
var eventHubsDataOwnerRoleId = 'f526a384-b230-433a-b45c-95f59c4a2dec' // Azure Event Hubs Data Owner
var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419' // Azure Service Bus Data Owner
// Cosmos DB Built-in Data Contributor — a SQL-plane role assigned via
// sqlRoleAssignments, NOT an Azure RBAC roleAssignment.
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'
// Key Vault Secrets Officer — built-in Azure RBAC role granting get/set/list/delete
// on secrets (data plane). Assigned to the proxy SP so the SecretsManager smoke can
// drive the full secret lifecycle against the ephemeral vault via Workload Identity.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var assignDataPlaneRoles = !empty(principalId)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      // Strong (not the Session default) so the DynamoDB module can faithfully
      // serve strongly-consistent reads (ConsistentRead=true -> Cosmos Strong);
      // a Session account rejects a Strong read with HTTP 400. Permitted on a
      // single-region account.
      defaultConsistencyLevel: 'Strong'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    disableLocalAuth: false
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource eventHubs 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: eventHubsNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1
  }
}

resource eventHub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: eventHubs
  name: eventHubName
  properties: {
    partitionCount: 1
    messageRetentionInDays: 1
  }
}

// Key Vault backing the Secrets Manager smoke. Ephemeral like every other
// backend here: a uniquely-named, RBAC-authorized vault provisioned per run and
// purged on teardown. Soft-delete is mandatory and cannot be disabled, so the
// retention is pinned to the 7-day minimum and purge protection is left OFF so
// the workflow can `az keyvault purge` the name immediately after the run
// instead of leaking a reserved name for 90 days. The proxy authenticates with
// the same Workload-Identity SP (principalId) the other AAD scenarios use.
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

// --- Data-plane RBAC for the Workload-Identity E2E scenario (issue #307) ---
// Each is created only when principalId is supplied, so the default shared-key
// provisioning path (and any deploy without the OIDC SP) is unchanged.

resource eventHubsDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignDataPlaneRoles) {
  name: guid(eventHubs.id, principalId, eventHubsDataOwnerRoleId)
  scope: eventHubs
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', eventHubsDataOwnerRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignDataPlaneRoles) {
  name: guid(serviceBus.id, principalId, serviceBusDataOwnerRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (assignDataPlaneRoles) {
  parent: cosmos
  name: guid(cosmos.id, principalId, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: principalId
    scope: cosmos.id
  }
}

resource keyVaultSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignDataPlaneRoles) {
  name: guid(keyVault.id, principalId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountName string = storage.name
output serviceBusNamespaceName string = serviceBus.name
output cosmosAccountName string = cosmos.name
output cosmosDatabaseName string = cosmosDatabaseName
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output eventHubsNamespaceName string = eventHubs.name
output eventHubName string = eventHubName
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
