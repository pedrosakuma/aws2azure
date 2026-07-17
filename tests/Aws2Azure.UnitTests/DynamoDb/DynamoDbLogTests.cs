using System.Reflection;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.UnitTests.DynamoDb;

public sealed class DynamoDbLogTests
{
    [Fact]
    public void LoggerMessages_are_consolidated_with_explicit_unique_event_ids()
    {
        var loggerMethods = typeof(DynamoDbLog).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(entry => entry.Attribute is not null)
            .ToArray();

        Assert.NotEmpty(loggerMethods);
        Assert.Single(loggerMethods.Select(entry => entry.Method.DeclaringType).Distinct());
        Assert.All(loggerMethods, entry => Assert.Equal(typeof(DynamoDbLog), entry.Method.DeclaringType));
        Assert.All(loggerMethods, entry => Assert.NotEqual(-1, entry.Attribute!.EventId));
        Assert.Equal(
            loggerMethods.Length,
            loggerMethods.Select(entry => entry.Attribute!.EventId).Distinct().Count());

        var expectedEventIds = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(DynamoDbLog.BelowStrong)] = 63313435,
            [nameof(DynamoDbLog.CrossPartitionScan)] = 3001,
            [nameof(DynamoDbLog.DiscoveredRegions)] = 393774940,
            [nameof(DynamoDbLog.Failover)] = 563663975,
            [nameof(DynamoDbLog.Honored)] = 551435922,
            [nameof(DynamoDbLog.Indeterminate)] = 1492089352,
            [nameof(DynamoDbLog.LogSprocAlreadyExists)] = 789634110,
            [nameof(DynamoDbLog.LogSprocCreateFailed)] = 1729926049,
            [nameof(DynamoDbLog.LogSprocCreated)] = 1859797362,
            [nameof(DynamoDbLog.ProbeFailed)] = 353337968,
            [nameof(DynamoDbLog.SelectedEndpoint)] = 86962373,
        };

        Assert.Equal(
            expectedEventIds,
            loggerMethods.ToDictionary(entry => entry.Method.Name, entry => entry.Attribute!.EventId));
    }
}
