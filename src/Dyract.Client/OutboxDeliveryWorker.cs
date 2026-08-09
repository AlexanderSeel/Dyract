using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Dyract.Transport;

namespace Dyract.Client;

public sealed record OutboxDeliveryCycleResult(
    int Examined,
    int Sent,
    int Failed,
    int ChangedConcurrently);

public sealed record OutboxBacklogDrainResult(
    int Cycles,
    int Examined,
    int Sent,
    int Failed,
    int ChangedConcurrently,
    bool BudgetExhausted);

public sealed class OutboxDeliveryWorker
{
    private static readonly TimeSpan InitialFailureDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialAckRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(2);
    private const int MaximumBatchSize = 500;
    private const int MaximumBacklogBudget = 500;

    private readonly IOutboxDeliveryQueue _outbox;
    private readonly IPeerApplicationFrameSender _sender;
    private readonly TimeProvider _timeProvider;

    public OutboxDeliveryWorker(
        IOutboxDeliveryQueue outbox,
        IPeerApplicationFrameSender sender,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OutboxDeliveryCycleResult> ProcessDueAsync(
        PeerId localPeerId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPeerId.Value))
        {
            throw new ArgumentException("Local PeerId must be initialized.", nameof(localPeerId));
        }

        if (limit is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Outbox delivery limit must be between 1 and {MaximumBatchSize}.");
        }

        var due = await _outbox.GetDueOutboxAsync(
            _timeProvider.GetUtcNow(),
            limit,
            cancellationToken);

        var sent = 0;
        var failed = 0;
        var changedConcurrently = 0;

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(item.SenderPeerId, localPeerId.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Outbox item sender does not match the active local identity.");
            }

            if (!PeerId.TryParse(item.RecipientPeerId, out var recipientPeerId) ||
                recipientPeerId == localPeerId)
            {
                throw new InvalidOperationException("Outbox item recipient PeerId is invalid.");
            }

            var frame = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
                item.MessageId,
                localPeerId,
                recipientPeerId,
                item.CreatedAt,
                item.Text));

            try
            {
                await _sender.SendAsync(recipientPeerId, frame, cancellationToken);
                var nextAttemptAt = _timeProvider.GetUtcNow().Add(
                    ComputeDelay(InitialAckRetryDelay, item.Attempts));
                if (await _outbox.RecordOutboundSentAsync(
                        item.MessageId,
                        localPeerId.Value,
                        recipientPeerId.Value,
                        nextAttemptAt,
                        cancellationToken))
                {
                    sent++;
                }
                else
                {
                    changedConcurrently++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var nextAttemptAt = _timeProvider.GetUtcNow().Add(
                    ComputeDelay(InitialFailureDelay, item.Attempts));
                var failureCode = $"send:{exception.GetType().Name}";
                if (await _outbox.RecordOutboundFailureAsync(
                        item.MessageId,
                        localPeerId.Value,
                        recipientPeerId.Value,
                        failureCode,
                        nextAttemptAt,
                        cancellationToken))
                {
                    failed++;
                }
                else
                {
                    changedConcurrently++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }

        return new OutboxDeliveryCycleResult(
            due.Count,
            sent,
            failed,
            changedConcurrently);
    }

    public async Task<OutboxBacklogDrainResult> ProcessDueBacklogAsync(
        PeerId localPeerId,
        int batchSize = 50,
        int maximumMessages = MaximumBacklogBudget,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"Backlog batch size must be between 1 and {MaximumBatchSize}.");
        }

        if (maximumMessages is < 1 or > MaximumBacklogBudget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMessages),
                $"Backlog message budget must be between 1 and {MaximumBacklogBudget}.");
        }

        var cycles = 0;
        var examined = 0;
        var sent = 0;
        var failed = 0;
        var changedConcurrently = 0;

        while (examined < maximumMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBudget = maximumMessages - examined;
            var currentBatchSize = Math.Min(batchSize, remainingBudget);
            var cycle = await ProcessDueAsync(
                localPeerId,
                currentBatchSize,
                cancellationToken);

            cycles++;
            examined += cycle.Examined;
            sent += cycle.Sent;
            failed += cycle.Failed;
            changedConcurrently += cycle.ChangedConcurrently;

            if (cycle.Examined < currentBatchSize)
            {
                break;
            }
        }

        return new OutboxBacklogDrainResult(
            cycles,
            examined,
            sent,
            failed,
            changedConcurrently,
            BudgetExhausted: examined >= maximumMessages);
    }

    internal static TimeSpan ComputeFailureDelay(int attempts)
        => ComputeDelay(InitialFailureDelay, attempts);

    internal static TimeSpan ComputeAckRetryDelay(int attempts)
        => ComputeDelay(InitialAckRetryDelay, attempts);

    private static TimeSpan ComputeDelay(TimeSpan initialDelay, int attempts)
    {
        if (attempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts));
        }

        var exponent = Math.Min(attempts, 10);
        var multiplier = 1L << exponent;
        var ticks = initialDelay.Ticks > MaximumRetryDelay.Ticks / multiplier
            ? MaximumRetryDelay.Ticks
            : initialDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, MaximumRetryDelay.Ticks));
    }
}
