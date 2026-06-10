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

var suffix = uniqueString(resourceGroup().id)
var storageAccountName = toLower('${baseName}st${suffix}')
var serviceBusNamespaceName = '${baseName}-sb-${suffix}'
var cosmosAccountName = toLower('${baseName}-cosmos-${suffix}')
var eventHubsNamespaceName = '${baseName}-eh-${suffix}'

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

output storageAccountName string = storage.name
output serviceBusNamespaceName string = serviceBus.name
output cosmosAccountName string = cosmos.name
output cosmosDatabaseName string = cosmosDatabaseName
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output eventHubsNamespaceName string = eventHubs.name
output eventHubName string = eventHubName
