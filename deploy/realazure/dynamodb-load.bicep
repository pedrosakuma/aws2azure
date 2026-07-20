param location string = resourceGroup().location

@description('Cosmos SQL database the DynamoDB module targets. The module creates containers (tables) but NOT the database, so it must pre-exist.')
param cosmosDatabaseName string = 'dynamodb'

var cosmosAccountName = toLower('a2aload${uniqueString(resourceGroup().id)}')

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
      // single-region account. Mirrors main.bicep's nightly-smoke topology so
      // load evidence exercises the same consistency contract.
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

output cosmosAccountName string = cosmos.name
output cosmosDatabaseName string = cosmosDatabaseName
output cosmosEndpoint string = cosmos.properties.documentEndpoint
