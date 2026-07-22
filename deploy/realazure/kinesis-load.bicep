param location string = resourceGroup().location

@minValue(1)
@maxValue(32)
param partitionCount int = 2

param eventHubName string = 'kinesis-load'

var eventHubsNamespaceName = toLower('a2aload${uniqueString(resourceGroup().id)}')

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
    partitionCount: partitionCount
    messageRetentionInDays: 1
  }
}

output eventHubsNamespaceName string = eventHubs.name
output eventHubName string = eventHub.name
