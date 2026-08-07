using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.Client;

public interface IPeerApplicationFrameSender
{
    Task SendAsync(
        PeerId recipientPeerId,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default);
}

public sealed record OutboxDeliveryCycleResult(
    int Examined,
    int Sent,
    int Failed,
    int ChangedConcurrently);

public sealed class OutboxDeliveryWorker
{
    private static readonly TimeSpan InitialFailureDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialAckRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(2);

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
        }

        return new OutboxDeliveryCycleResult(
            due.Count,
            sent,
            failed,
            changedConcurrently);
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
