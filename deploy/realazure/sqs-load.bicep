param location string = resourceGroup().location

var serviceBusNamespaceName = toLower('a2aload${uniqueString(resourceGroup().id)}')

// Standard tier: the sqs-standard-messaging profile qualifies both the
// AMQP-default and REST transports the SqsServiceModule already supports —
// no Premium-only feature (partitioning, dedicated capacity) is exercised.
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

output serviceBusNamespaceName string = serviceBus.name
