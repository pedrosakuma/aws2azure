using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Azure.Messaging.ServiceBus.Administration;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SqsRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task ListQueues_paginates_against_real_service_bus()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var prefix = "aws2azure-list-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var queueUrls = new List<string>();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                var created = await client.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = $"{prefix}-{i}",
                }, timeout.Token).ConfigureAwait(false);
                queueUrls.Add(created.QueueUrl);
            }

            var first = await client.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = prefix,
                MaxResults = 2,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(2, first.QueueUrls.Count);
            Assert.False(string.IsNullOrWhiteSpace(first.NextToken));

            var second = await client.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = prefix,
                MaxResults = 2,
                NextToken = first.NextToken,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Single(second.QueueUrls);
            Assert.True(string.IsNullOrWhiteSpace(second.NextToken));
            Assert.Equal(
                queueUrls.Order(StringComparer.Ordinal).ToArray(),
                first.QueueUrls.Concat(second.QueueUrls).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            foreach (var queueUrl in queueUrls)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task Message_batches_report_real_service_bus_results()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = "aws2azure-batch-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string? queueUrl = null;

        try
        {
            queueUrl = (await client.CreateQueueAsync(queueName, timeout.Token).ConfigureAwait(false)).QueueUrl;
            var bodies = Enumerable.Range(0, 3)
                .Select(i => $"batch-{Guid.NewGuid():N}-{i}")
                .ToArray();
            var sent = await client.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = bodies.Select((body, i) => new SendMessageBatchRequestEntry
                {
                    Id = $"send-{i}",
                    MessageBody = body,
                }).ToList(),
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, sent.Successful.Count);
            Assert.Empty(sent.Failed);

            var received = await ReceiveBodiesAsync(client, queueUrl, bodies, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, received.Count);

            var deleteEntries = received.Select((item, i) => new DeleteMessageBatchRequestEntry
            {
                Id = $"delete-{i}",
                ReceiptHandle = item.Value,
            }).ToList();
            deleteEntries.Add(new DeleteMessageBatchRequestEntry
            {
                Id = "invalid",
                ReceiptHandle = "not-a-real-service-bus-lock-token",
            });

            var deleted = await client.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = deleteEntries,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, deleted.Successful.Count);
            var failure = Assert.Single(deleted.Failed);
            Assert.Equal("invalid", failure.Id);
            Assert.True(failure.SenderFault);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task Queue_metadata_and_tags_round_trip_against_real_service_bus()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = "aws2azure-meta-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        var admin = new ServiceBusAdministrationClient(fixture.CreateServiceBusConnectionString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string? queueUrl = null;

        try
        {
            var created = await client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["DelaySeconds"] = "3",
                    ["ReceiveMessageWaitTimeSeconds"] = "2",
                },
                Tags = new Dictionary<string, string>
                {
                    ["env"] = "prod",
                    ["owner"] = "platform",
                },
            }, timeout.Token).ConfigureAwait(false);
            queueUrl = created.QueueUrl;

            var initialAttributes = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string>
                {
                    "DelaySeconds",
                    "ReceiveMessageWaitTimeSeconds",
                    "QueueArn",
                    "CreatedTimestamp",
                    "LastModifiedTimestamp",
                },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal("3", initialAttributes.Attributes["DelaySeconds"]);
            Assert.Equal("2", initialAttributes.Attributes["ReceiveMessageWaitTimeSeconds"]);
            Assert.Equal($"arn:aws:sqs:us-east-1:000000000000:{queueName}", initialAttributes.Attributes["QueueArn"]);
            Assert.True(long.Parse(initialAttributes.Attributes["CreatedTimestamp"]) > 0);
            Assert.True(long.Parse(initialAttributes.Attributes["LastModifiedTimestamp"]) > 0);

            var initialTags = await client.ListQueueTagsAsync(new ListQueueTagsRequest
            {
                QueueUrl = queueUrl,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal("prod", initialTags.Tags["env"]);
            Assert.Equal("platform", initialTags.Tags["owner"]);

            await client.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["ReceiveMessageWaitTimeSeconds"] = "5",
                },
            }, timeout.Token).ConfigureAwait(false);

            var updatedAttributes = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string>
                {
                    "DelaySeconds",
                    "ReceiveMessageWaitTimeSeconds",
                },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal("3", updatedAttributes.Attributes["DelaySeconds"]);
            Assert.Equal("5", updatedAttributes.Attributes["ReceiveMessageWaitTimeSeconds"]);

            await client.TagQueueAsync(new TagQueueRequest
            {
                QueueUrl = queueUrl,
                Tags = new Dictionary<string, string>
                {
                    ["team"] = "core",
                },
            }, timeout.Token).ConfigureAwait(false);

            await client.UntagQueueAsync(new UntagQueueRequest
            {
                QueueUrl = queueUrl,
                TagKeys = new List<string> { "env" },
            }, timeout.Token).ConfigureAwait(false);

            var finalTags = await client.ListQueueTagsAsync(new ListQueueTagsRequest
            {
                QueueUrl = queueUrl,
            }, timeout.Token).ConfigureAwait(false);
            Assert.False(finalTags.Tags.ContainsKey("env"));
            Assert.Equal("platform", finalTags.Tags["owner"]);
            Assert.Equal("core", finalTags.Tags["team"]);

            var azureQueue = await admin.GetQueueAsync(queueName, timeout.Token).ConfigureAwait(false);
            Assert.False(string.IsNullOrWhiteSpace(azureQueue.Value.UserMetadata));
        }
        finally
        {
            if (queueUrl is not null)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task SendMessage_to_deleted_queue_returns_native_nonexistent_queue_error()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = "aws2azure-gone-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var queueUrl = (await client.CreateQueueAsync(queueName, timeout.Token).ConfigureAwait(false)).QueueUrl;
        await client.DeleteQueueAsync(queueUrl, timeout.Token).ConfigureAwait(false);

        var exception = await Assert.ThrowsAsync<QueueDoesNotExistException>(() =>
            client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "should-not-be-delivered",
            }, timeout.Token));
        // Once the SDK recognizes the shape correctly (see the AwsJson
        // protocol-code fix in SqsErrorResponse.cs), its own ErrorCode
        // property reflects the wire-level Smithy shape name it actually
        // received, not the legacy Query-protocol code documented by AWS.
        Assert.Equal("QueueDoesNotExist", exception.ErrorCode);
        // AWSSDK.SQS only derives ErrorType from the legacy x-amzn-query-error
        // response header (a Code;Sender|Receiver pair used for backward
        // compatibility with pre-JSON-protocol SQS clients); the proxy doesn't
        // emit that header, so JsonErrorResponseUnmarshaller's hardcoded
        // ErrorType.Unknown default applies here, same as every other
        // JSON-protocol error the proxy renders (see the analogous Kinesis
        // real-Azure assertion).
        Assert.Equal(ErrorType.Unknown, exception.ErrorType);
    }

    [SkippableFact]
    public async Task PurgeQueue_empties_a_real_service_bus_queue()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = "aws2azure-purge-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string? queueUrl = null;

        try
        {
            queueUrl = (await client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["VisibilityTimeout"] = "5",
                },
            }, timeout.Token).ConfigureAwait(false)).QueueUrl;
            var bodies = Enumerable.Range(0, 4)
                .Select(i => $"purge-{Guid.NewGuid():N}-{i}")
                .ToArray();
            foreach (var body in bodies)
            {
                await client.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = body,
                }, timeout.Token).ConfigureAwait(false);
            }

            await client.PurgeQueueAsync(new PurgeQueueRequest
            {
                QueueUrl = queueUrl,
            }, timeout.Token).ConfigureAwait(false);

            await AssertQueueEmptyAsync(client, queueUrl, TimeSpan.FromSeconds(2), timeout.Token)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token).ConfigureAwait(false);
            await AssertQueueEmptyAsync(client, queueUrl, TimeSpan.FromSeconds(2), timeout.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task ChangeMessageVisibilityBatch_mixes_zero_timeout_lock_renewal_and_invalid_handles_against_real_service_bus()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = RealAzureProxyFixture.SqsRestLaneQueueName;
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string? queueUrl = null;

        try
        {
            queueUrl = await EnsureQueueUrlAsync(client, queueName, timeout.Token).ConfigureAwait(false);
            await client.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["VisibilityTimeout"] = "10",
                },
            }, timeout.Token).ConfigureAwait(false);
            await ResetSharedRestLaneQueueAsync(client, queueUrl).ConfigureAwait(false);

            var zeroBody = "cmvb-zero-" + Guid.NewGuid().ToString("N");
            var renewedBody = "cmvb-renewed-" + Guid.NewGuid().ToString("N");
            await client.SendMessageAsync(queueUrl, zeroBody, timeout.Token).ConfigureAwait(false);
            await client.SendMessageAsync(queueUrl, renewedBody, timeout.Token).ConfigureAwait(false);

            var received = await ReceiveBodiesAsync(client, queueUrl, [zeroBody, renewedBody], timeout.Token)
                .ConfigureAwait(false);
            Assert.Equal(2, received.Count);

            await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token).ConfigureAwait(false);
            var changed = await client.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
            {
                QueueUrl = queueUrl,
                Entries =
                [
                    new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = "zero",
                        ReceiptHandle = received[zeroBody],
                        VisibilityTimeout = 0,
                    },
                    new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = "renewed",
                        ReceiptHandle = received[renewedBody],
                        VisibilityTimeout = 30,
                    },
                    new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = "invalid",
                        ReceiptHandle = "not-a-real-service-bus-lock-token",
                        VisibilityTimeout = 0,
                    },
                ],
            }, timeout.Token).ConfigureAwait(false);

            var successfulIds = changed.Successful.Select(entry => entry.Id).OrderBy(id => id).ToArray();
            Assert.Equal(new[] { "renewed", "zero" }, successfulIds);
            var failure = Assert.Single(changed.Failed);
            Assert.Equal("invalid", failure.Id);
            Assert.True(failure.SenderFault);

            var zeroRedelivered = await ReceiveExpectedBodyAsync(
                client,
                queueUrl,
                zeroBody,
                TimeSpan.FromSeconds(3),
                timeout.Token).ConfigureAwait(false);
            Assert.Equal("2", zeroRedelivered.Attributes["ApproximateReceiveCount"]);
            await client.DeleteMessageAsync(queueUrl, zeroRedelivered.ReceiptHandle, timeout.Token)
                .ConfigureAwait(false);

            await AssertNoBodyAsync(
                client,
                queueUrl,
                renewedBody,
                TimeSpan.FromSeconds(4),
                timeout.Token).ConfigureAwait(false);

            var renewedRedelivered = await ReceiveExpectedBodyAsync(
                client,
                queueUrl,
                renewedBody,
                TimeSpan.FromSeconds(20),
                timeout.Token).ConfigureAwait(false);
            await client.DeleteMessageAsync(queueUrl, renewedRedelivered.ReceiptHandle, timeout.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try
                {
                    await client.SetQueueAttributesAsync(new SetQueueAttributesRequest
                    {
                        QueueUrl = queueUrl,
                        Attributes = new Dictionary<string, string>
                        {
                            ["VisibilityTimeout"] = "30",
                        },
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                try { await ResetSharedRestLaneQueueAsync(client, queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task<Dictionary<string, string>> ReceiveBodiesAsync(
        IAmazonSQS client,
        string queueUrl,
        IReadOnlyCollection<string> expectedBodies,
        CancellationToken cancellationToken)
    {
        var expected = expectedBodies.ToHashSet(StringComparer.Ordinal);
        var received = new Dictionary<string, string>(StringComparer.Ordinal);
        while (received.Count < expected.Count && !cancellationToken.IsCancellationRequested)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                if (expected.Contains(message.Body))
                {
                    received[message.Body] = message.ReceiptHandle;
                }
            }
        }

        return received;
    }

    private static async Task<Message> ReceiveExpectedBodyAsync(
        IAmazonSQS client,
        string queueUrl,
        string expectedBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
                MessageSystemAttributeNames = ["ApproximateReceiveCount"],
            }, cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 })
            {
                continue;
            }

            var message = Assert.Single(response.Messages);
            Assert.Equal(expectedBody, message.Body);
            return message;
        }

        throw new TimeoutException($"Timed out waiting to receive '{expectedBody}'.");
    }

    private static async Task AssertNoBodyAsync(
        IAmazonSQS client,
        string queueUrl,
        string unexpectedBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
            }, cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 })
            {
                continue;
            }

            var message = Assert.Single(response.Messages);
            Assert.NotEqual(unexpectedBody, message.Body);
        }
    }

    private static async Task AssertQueueEmptyAsync(
        IAmazonSQS client,
        string queueUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
            }, cancellationToken).ConfigureAwait(false);
            Assert.Empty(response.Messages);
        }
    }

    private static async Task<string> EnsureQueueUrlAsync(
        IAmazonSQS client,
        string queueName,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
            }, cancellationToken).ConfigureAwait(false);
            return created.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            var existing = await client.GetQueueUrlAsync(new GetQueueUrlRequest
            {
                QueueName = queueName,
            }, cancellationToken).ConfigureAwait(false);
            return existing.QueueUrl;
        }
    }

    private static async Task DrainQueueAsync(
        IAmazonSQS client,
        string queueUrl,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
            }, cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 } messages)
            {
                return;
            }

            foreach (var message in messages)
            {
                await client.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ResetSharedRestLaneQueueAsync(
        IAmazonSQS client,
        string queueUrl)
    {
        await DrainQueueAsync(client, queueUrl, CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(31)).ConfigureAwait(false);
        await DrainQueueAsync(client, queueUrl, CancellationToken.None).ConfigureAwait(false);
    }
}
