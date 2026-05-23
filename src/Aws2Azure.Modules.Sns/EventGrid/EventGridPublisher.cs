using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns.EventGrid;

internal interface IEventGridPublisher
{
    Task PublishAsync(EventGridPublishDestination destination, EventGridPublishMessage message, CancellationToken cancellationToken);

    Task<SnsBatchSendResult> PublishBatchAsync(
        EventGridPublishDestination destination,
        IReadOnlyList<EventGridPublishMessage> messages,
        CancellationToken cancellationToken);
}

internal sealed record EventGridPublishMessage(
    Guid MessageId,
    string TopicArn,
    string Message,
    string? Subject,
    string? MessageStructure,
    string? MessageGroupId,
    string? MessageDeduplicationId,
    IReadOnlyList<SnsMessageAttribute> MessageAttributes);

internal sealed class EventGridPublisherOptions
{
    public int MaxEventsPerRequest { get; set; } = 5000;
    public int MaxBytesPerEvent { get; set; } = 1024 * 1024;
    public int MaxBytesPerRequest { get; set; } = 1024 * 1024;
}

internal readonly record struct EventGridPublishFailure(int SnsStatusCode, string ErrorCode, string ErrorMessage, bool SenderFault);

internal sealed class EventGridPublishException(EventGridPublishFailure failure) : Exception(failure.ErrorMessage)
{
    public EventGridPublishFailure Failure { get; } = failure;
}

internal sealed class EventGridPublisher : IEventGridPublisher
{
    private readonly AzureHttpClient _http;
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly ILogger<EventGridPublisher> _logger;
    private readonly EventGridPublisherOptions _options;
    private readonly TimeProvider _clock;

    public EventGridPublisher(
        AzureHttpClient http,
        EntraIdTokenProvider tokenProvider,
        ILogger<EventGridPublisher> logger,
        EventGridPublisherOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _options = options ?? new EventGridPublisherOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task PublishAsync(EventGridPublishDestination destination, EventGridPublishMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(message);

        var payload = SerializeMessage(message);
        if (payload.Length > _options.MaxBytesPerEvent)
        {
            throw new EventGridPublishException(CreateOversizeFailure(_options.MaxBytesPerEvent));
        }

        var failure = await SendBatchAsync(destination, BuildBatchPayload([payload], [0], 2 + payload.Length), cancellationToken).ConfigureAwait(false);
        if (failure is { } publishFailure)
        {
            throw new EventGridPublishException(publishFailure);
        }
    }

    public async Task<SnsBatchSendResult> PublishBatchAsync(
        EventGridPublishDestination destination,
        IReadOnlyList<EventGridPublishMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return new SnsBatchSendResult([]);
        }

        var payloads = new byte[messages.Count][];
        var outcomes = new SnsBatchSendOutcome?[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            payloads[i] = SerializeMessage(messages[i]);
            if (payloads[i].Length > _options.MaxBytesPerEvent)
            {
                outcomes[i] = ToBatchOutcome(CreateOversizeFailure(_options.MaxBytesPerEvent));
            }
        }

        var pendingIndexes = new List<int>(_options.MaxEventsPerRequest);
        var batchBytes = 2;
        for (var i = 0; i < payloads.Length; i++)
        {
            if (outcomes[i] is not null)
            {
                continue;
            }

            var candidateBytes = batchBytes + payloads[i].Length + (pendingIndexes.Count > 0 ? 1 : 0);
            if (pendingIndexes.Count > 0
                && (pendingIndexes.Count == _options.MaxEventsPerRequest || candidateBytes > _options.MaxBytesPerRequest))
            {
                await FlushBatchAsync(destination, payloads, pendingIndexes, batchBytes, outcomes, cancellationToken).ConfigureAwait(false);
                pendingIndexes.Clear();
                batchBytes = 2;
                candidateBytes = 2 + payloads[i].Length;
            }

            pendingIndexes.Add(i);
            batchBytes = candidateBytes;
        }

        if (pendingIndexes.Count > 0)
        {
            await FlushBatchAsync(destination, payloads, pendingIndexes, batchBytes, outcomes, cancellationToken).ConfigureAwait(false);
        }

        return new SnsBatchSendResult(outcomes.Select(static outcome => outcome ?? new SnsBatchSendOutcome(true, null, null, false)).ToArray());
    }

    private async Task<EventGridPublishFailure?> SendBatchAsync(
        EventGridPublishDestination destination,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildPublishUri(destination.Endpoint))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await AuthenticateAsync(request, destination, cancellationToken).ConfigureAwait(false);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return CreateFailure(response.StatusCode, body);
        }
        catch (HttpRequestException exception)
        {
            return new EventGridPublishFailure(
                StatusCodes.Status500InternalServerError,
                "InternalFailure",
                "Azure Event Grid publish failed. " + exception.Message,
                SenderFault: false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new EventGridPublishFailure(
                StatusCodes.Status500InternalServerError,
                "InternalFailure",
                "Azure Event Grid publish timed out. " + exception.Message,
                SenderFault: false);
        }
    }

    private async ValueTask AuthenticateAsync(HttpRequestMessage request, EventGridPublishDestination destination, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(destination.AccessKey))
        {
            request.Headers.TryAddWithoutValidation("aeg-sas-key", destination.AccessKey);
            return;
        }

        var token = await _tokenProvider.GetTokenAsync(
            destination.TenantId!,
            destination.ClientId!,
            destination.ClientSecret!,
            "https://eventgrid.azure.net/.default",
            cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private byte[] SerializeMessage(EventGridPublishMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageGroupId)
            || !string.IsNullOrWhiteSpace(message.MessageDeduplicationId))
        {
            EventGridPublisherLog.IgnoringFifoFields(_logger, message.TopicArn, message.MessageId);
        }

        var attributes = new Dictionary<string, SnsEventGridMessageAttribute>(message.MessageAttributes.Count, StringComparer.Ordinal);
        for (var i = 0; i < message.MessageAttributes.Count; i++)
        {
            var attribute = message.MessageAttributes[i];
            attributes[attribute.Name] = new SnsEventGridMessageAttribute
            {
                Type = attribute.DataType,
                Value = attribute.StringValue ?? attribute.BinaryValue ?? string.Empty,
            };
        }

        var envelope = new SnsEventGridEnvelope
        {
            Id = message.MessageId.ToString(),
            EventType = SnsEventGridConstants.EventType,
            Subject = message.TopicArn,
            EventTime = _clock.GetUtcNow(),
            DataVersion = SnsEventGridConstants.DataVersion,
            Data = new SnsEventGridData
            {
                Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject,
                Message = message.Message,
                MessageAttributes = attributes,
                MessageStructure = string.IsNullOrWhiteSpace(message.MessageStructure) ? null : message.MessageStructure,
                TopicArn = message.TopicArn,
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, SnsEventGridJsonContext.Default.SnsEventGridEnvelope);
    }

    private async Task FlushBatchAsync(
        EventGridPublishDestination destination,
        byte[][] payloads,
        IReadOnlyList<int> indexes,
        int totalBytes,
        SnsBatchSendOutcome?[] outcomes,
        CancellationToken cancellationToken)
    {
        var payload = BuildBatchPayload(payloads, indexes, totalBytes);
        var failure = await SendBatchAsync(destination, payload, cancellationToken).ConfigureAwait(false);
        var outcome = failure is null
            ? new SnsBatchSendOutcome(true, null, null, false)
            : ToBatchOutcome(failure.Value);
        for (var i = 0; i < indexes.Count; i++)
        {
            outcomes[indexes[i]] = outcome;
        }
    }

    private static byte[] BuildBatchPayload(byte[][] payloads, IReadOnlyList<int> indexes, int totalBytes)
    {
        var combined = new byte[totalBytes];
        var offset = 0;
        combined[offset++] = (byte)'[';
        for (var i = 0; i < indexes.Count; i++)
        {
            if (i > 0)
            {
                combined[offset++] = (byte)',';
            }

            var payload = payloads[indexes[i]];
            Buffer.BlockCopy(payload, 0, combined, offset, payload.Length);
            offset += payload.Length;
        }

        combined[offset] = (byte)']';
        return combined;
    }

    private static Uri BuildPublishUri(string endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var query = builder.Query.TrimStart('?');
        if (query.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            return builder.Uri;
        }

        builder.Query = string.IsNullOrEmpty(query)
            ? "api-version=" + SnsEventGridConstants.ApiVersion
            : query + "&api-version=" + SnsEventGridConstants.ApiVersion;
        return builder.Uri;
    }

    private static EventGridPublishFailure CreateOversizeFailure(int maxBytesPerEvent)
        => new(
            StatusCodes.Status400BadRequest,
            "InvalidParameter",
            $"Azure Event Grid classic schema limits each event to {maxBytesPerEvent} bytes.",
            SenderFault: true);

    private static EventGridPublishFailure CreateFailure(HttpStatusCode statusCode, string body)
    {
        var trimmedBody = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new EventGridPublishFailure(
                StatusCodes.Status403Forbidden,
                "AuthorizationError",
                "Access denied when sending to Azure Event Grid.",
                SenderFault: false),
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => new EventGridPublishFailure(
                StatusCodes.Status400BadRequest,
                "InvalidParameter",
                BuildFailureMessage("Azure Event Grid rejected the publish request.", trimmedBody),
                SenderFault: true),
            _ => new EventGridPublishFailure(
                StatusCodes.Status500InternalServerError,
                "InternalFailure",
                BuildFailureMessage($"Azure Event Grid publish failed with HTTP {(int)statusCode}.", trimmedBody),
                SenderFault: false),
        };
    }

    private static SnsBatchSendOutcome ToBatchOutcome(EventGridPublishFailure failure)
        => new(false, failure.ErrorCode, failure.ErrorMessage, failure.SenderFault);

    private static string BuildFailureMessage(string prefix, string? body)
        => string.IsNullOrWhiteSpace(body) ? prefix : prefix + " " + body;
}

internal static partial class EventGridPublisherLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "SNS Event Grid backend ignored FIFO fields for topic '{TopicArn}' and generated message id '{MessageId}'.")]
    public static partial void IgnoringFifoFields(ILogger logger, string topicArn, Guid messageId);
}
