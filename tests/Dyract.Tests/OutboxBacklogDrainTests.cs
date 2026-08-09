using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Storage;
using Dyract.Transport;
using Xunit;

namespace Dyract.Tests;

public sealed class OutboxBacklogDrainTests
{
    [Fact]
    public async Task ProcessDueBacklog_DrainsMultipleBatches()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_100_000_000);
        var queue = new DrainingOutboxQueue(CreateItems(local.PeerId.Value, remote.PeerId.Value, now, 5));
        var sender = new CountingSender();
        var worker = new OutboxDeliveryWorker(queue, sender, new FixedTimeProvider(now));

        var result = await worker.ProcessDueBacklogAsync(
            local.PeerId,
            batchSize: 2,
            maximumMessages: 10);

        Assert.Equal(new OutboxBacklogDrainResult(3, 5, 5, 0, 0, false), result);
        Assert.Equal(5, sender.CallCount);
        Assert.Empty(queue.Pending);
        Assert.Equal(new[] { 2, 2, 2 }, queue.RequestedLimits);
    }

    [Fact]
    public async Task ProcessDueBacklog_StopsAtExplicitMessageBudget()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_100_100_000);
        var queue = new DrainingOutboxQueue(CreateItems(local.PeerId.Value, remote.PeerId.Value, now, 5));
        var sender = new CountingSender();
        var worker = new OutboxDeliveryWorker(queue, sender, new FixedTimeProvider(now));

        var result = await worker.ProcessDueBacklogAsync(
            local.PeerId,
            batchSize: 2,
            maximumMessages: 3);

        Assert.Equal(new OutboxBacklogDrainResult(2, 3, 3, 0, 0, true), result);
        Assert.Equal(3, sender.CallCount);
        Assert.Equal(2, queue.Pending.Count);
        Assert.Equal(new[] { 2, 1 }, queue.RequestedLimits);
    }

    private static IReadOnlyList<DueOutboxMessage> CreateItems(
        string senderPeerId,
        string recipientPeerId,
        DateTimeOffset now,
        int count)
        => Enumerable.Range(1, count)
            .Select(index => new DueOutboxMessage(
                index.ToString("x32"),
                senderPeerId,
                recipientPeerId,
                now.AddMinutes(-count + index),
                $"offline message {index}",
                Attempts: 0,
                NextAttemptAt: now))
            .ToArray();

    private sealed class DrainingOutboxQueue(IEnumerable<DueOutboxMessage> due) : IOutboxDeliveryQueue
    {
        private readonly List<DueOutboxMessage> _pending = [.. due];
        private readonly List<int> _requestedLimits = [];

        public IReadOnlyList<DueOutboxMessage> Pending => _pending;
        public IReadOnlyList<int> RequestedLimits => _requestedLimits;

        public Task<IReadOnlyList<DueOutboxMessage>> GetDueOutboxAsync(
            DateTimeOffset dueAtOrBefore,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestedLimits.Add(limit);
            return Task.FromResult<IReadOnlyList<DueOutboxMessage>>(
                _pending.Where(item => item.NextAttemptAt <= dueAtOrBefore).Take(limit).ToArray());
        }

        public Task<bool> RecordOutboundSentAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
            => RemoveExactAsync(messageId, senderPeerId, recipientPeerId, cancellationToken);

        public Task<bool> RecordOutboundFailureAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            string failureCode,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
            => RemoveExactAsync(messageId, senderPeerId, recipientPeerId, cancellationToken);

        private Task<bool> RemoveExactAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = _pending.FindIndex(item =>
                string.Equals(item.MessageId, messageId, StringComparison.Ordinal) &&
                string.Equals(item.SenderPeerId, senderPeerId, StringComparison.Ordinal) &&
                string.Equals(item.RecipientPeerId, recipientPeerId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _pending.RemoveAt(index);
            return Task.FromResult(true);
        }
    }

    private sealed class CountingSender : IPeerApplicationFrameSender
    {
        public int CallCount { get; private set; }

        public Task SendAsync(
            Dyract.Core.Identity.PeerId recipientPeerId,
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
