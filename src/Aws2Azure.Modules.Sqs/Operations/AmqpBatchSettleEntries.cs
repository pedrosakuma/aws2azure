using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class AmqpBatchSettleEntries
{
    internal static async Task DeleteBatchEntryAsync(
        (string Id, string ReceiptHandle) entry,
        string queueName,
        IAmqpReceiverProvider receivers,
        List<BatchEntryOk> ok,
        List<BatchEntryError> failed,
        HashSet<string> receiversToInvalidate,
        CancellationToken ct)
    {
        if (!TryDecodeBatchReceiptHandle(entry.Id, entry.ReceiptHandle, queueName, failed, out var decoded))
        {
            return;
        }

        AmqpReceiverLease? settleLease;
        try
        {
            settleLease = await AmqpSettleDispatcher.TryAcquireSettleReceiverAsync(
                receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddFailure(failed, entry.Id, AmqpErrorMapper.MapAmqpException(ex, "DeleteMessageBatch"));
            return;
        }
        using var settleLeaseScope = settleLease;
        var receiver = settleLease?.Receiver;
        if (receiver is null)
        {
            AddFailure(failed, entry.Id, SqsErrorMapping.ReceiptHandleInvalid());
            return;
        }

        bool settled;
        try
        {
            settled = await receiver.CompleteAsync(decoded.LockToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Defer invalidation until after sibling entries finish: disposing
            // the shared session receiver mid-batch can hang siblings.
            AddReceiverInvalidation(receiversToInvalidate, decoded.SessionId);
            AddFailure(failed, entry.Id, AmqpErrorMapper.MapSettleException(ex, "DeleteMessageBatch"));
            return;
        }
        if (!settled)
        {
            AddFailure(failed, entry.Id, SqsErrorMapping.ReceiptHandleInvalid());
            return;
        }
        if (!string.IsNullOrEmpty(decoded.SessionId) && receiver.InFlightCount == 0)
            AddReceiverInvalidation(receiversToInvalidate, decoded.SessionId);
        AddSuccess(ok, entry.Id);
    }

    internal static async Task ChangeVisibilityBatchEntryAsync(
        BatchAdminHandlers.ChangeVisEntry entry,
        string queueName,
        IAmqpReceiverProvider receivers,
        List<BatchEntryOk> ok,
        List<BatchEntryError> failed,
        HashSet<string> receiversToInvalidate,
        Action markManagementFailed,
        CancellationToken ct)
    {
        if (entry.VisibilityTimeout < 0 || entry.VisibilityTimeout > AmqpReceiveMessageHandlers.MaxVisibilityTimeoutSeconds)
        {
            AddFailure(failed, entry.Id, SqsErrorMapping.VisibilityTimeoutInvalid());
            return;
        }
        if (!TryDecodeBatchReceiptHandle(entry.Id, entry.ReceiptHandle, queueName, failed, out var decoded))
        {
            return;
        }

        if (entry.VisibilityTimeout == 0)
        {
            await AbandonVisibilityBatchEntryAsync(entry.Id, queueName, decoded, receivers, ok, failed, receiversToInvalidate, ct)
                .ConfigureAwait(false);
            return;
        }

        await RenewVisibilityBatchEntryAsync(entry.Id, queueName, decoded, receivers, ok, failed, markManagementFailed, ct)
            .ConfigureAwait(false);
    }

    private static async Task AbandonVisibilityBatchEntryAsync(
        string entryId,
        string queueName,
        AmqpReceiptHandle.Decoded decoded,
        IAmqpReceiverProvider receivers,
        List<BatchEntryOk> ok,
        List<BatchEntryError> failed,
        HashSet<string> receiversToInvalidate,
        CancellationToken ct)
    {
        AmqpReceiverLease? settleLease;
        try
        {
            settleLease = await AmqpSettleDispatcher.TryAcquireSettleReceiverAsync(
                receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddFailure(failed, entryId, AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibilityBatch"));
            return;
        }
        using var settleLeaseScope = settleLease;
        var receiver = settleLease?.Receiver;
        if (receiver is null)
        {
            AddFailure(failed, entryId, SqsErrorMapping.ReceiptHandleInvalid());
            return;
        }

        bool abandoned;
        try
        {
            abandoned = await receiver.AbandonAsync(decoded.LockToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AddReceiverInvalidation(receiversToInvalidate, decoded.SessionId);
            AddFailure(failed, entryId, AmqpErrorMapper.MapSettleException(ex, "ChangeMessageVisibilityBatch"));
            return;
        }
        if (!abandoned)
        {
            AddFailure(failed, entryId, SqsErrorMapping.MessageNotInflight());
            return;
        }
        if (!string.IsNullOrEmpty(decoded.SessionId) && receiver.InFlightCount == 0)
            AddReceiverInvalidation(receiversToInvalidate, decoded.SessionId);
        AddSuccess(ok, entryId);
    }

    private static async Task RenewVisibilityBatchEntryAsync(
        string entryId,
        string queueName,
        AmqpReceiptHandle.Decoded decoded,
        IAmqpReceiverProvider receivers,
        List<BatchEntryOk> ok,
        List<BatchEntryError> failed,
        Action markManagementFailed,
        CancellationToken ct)
    {
        using var sessionLease = string.IsNullOrEmpty(decoded.SessionId)
            ? null
            : receivers.TryAcquireExistingSessionReceiver(queueName, decoded.SessionId);
        ServiceBusReceiver? trackedReceiver = sessionLease?.Receiver
            ?? (string.IsNullOrEmpty(decoded.SessionId)
                ? receivers.TryGetExistingReceiver(queueName)
                : null);
        if (trackedReceiver is null)
        {
            AddFailure(failed, entryId, SqsErrorMapping.MessageNotInflight());
            return;
        }
        using var renewal = trackedReceiver.TryBeginLockRenewal(decoded.LockToken);
        if (renewal is null)
        {
            AddFailure(failed, entryId, SqsErrorMapping.MessageNotInflight());
            return;
        }

        ServiceBusManagementClient mgmt;
        try
        {
            mgmt = await receivers.GetManagementClientAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddFailure(failed, entryId, AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibilityBatch"));
            return;
        }

        DateTimeOffset lockedUntil;
        try
        {
            lockedUntil = string.IsNullOrEmpty(decoded.SessionId)
                ? await mgmt.RenewLockAsync(decoded.LockToken, ct).ConfigureAwait(false)
                : await mgmt.RenewSessionLockAsync(
                    decoded.SessionId, trackedReceiver.LinkName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceBusManagementException ex)
        {
            AddFailure(failed, entryId, AmqpErrorMapper.MapManagementException(ex, "ChangeMessageVisibilityBatch"));
            return;
        }
        catch (Exception ex)
        {
            markManagementFailed();
            AddFailure(failed, entryId, AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibilityBatch"));
            return;
        }
        if (string.IsNullOrEmpty(decoded.SessionId))
        {
            if (!renewal.Complete(lockedUntil, updateSession: false))
            {
                AddFailure(failed, entryId, SqsErrorMapping.MessageNotInflight());
                return;
            }
        }
        else
        {
            var currentReceiver = receivers.TryGetExistingSessionReceiver(queueName, decoded.SessionId);
            if (!ReferenceEquals(currentReceiver, trackedReceiver)
                || !renewal.Complete(lockedUntil, updateSession: true))
            {
                AddFailure(failed, entryId, SqsErrorMapping.MessageNotInflight());
                return;
            }
        }
        AddSuccess(ok, entryId);
    }

    private static bool TryDecodeBatchReceiptHandle(
        string entryId,
        string receiptHandle,
        string queueName,
        List<BatchEntryError> failed,
        out AmqpReceiptHandle.Decoded decoded)
    {
        if (AmqpReceiptHandle.TryDecode(receiptHandle, out decoded)
            && string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        AddFailure(failed, entryId, SqsErrorMapping.ReceiptHandleInvalid());
        return false;
    }

    private static void AddSuccess(List<BatchEntryOk> ok, string entryId)
    {
        lock (ok) ok.Add(new BatchEntryOk(entryId));
    }

    private static void AddFailure(
        List<BatchEntryError> failed,
        string entryId,
        SqsErrorMapping.Mapping mapping)
    {
        lock (failed)
        {
            failed.Add(new BatchEntryError(
                entryId,
                mapping.Code,
                mapping.Message,
                SenderFault: mapping.FaultType == SqsErrorResponse.FaultType.Sender));
        }
    }

    private static void AddReceiverInvalidation(HashSet<string> receiversToInvalidate, string? sessionId)
    {
        lock (receiversToInvalidate) receiversToInvalidate.Add(sessionId ?? string.Empty);
    }

}
