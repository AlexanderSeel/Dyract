using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class OutboxDeliveryWorkerTests
{
    private const string MessageId = "019c1a2b3c4d7e8f9123456789abcdef";

    [Fact]
    public async Task SuccessfulSend_KeepsMessagePendingForAckAndSchedulesRetry()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_786_112_000_000);
        var queue = new StubOutboxQueue
        {
            Due = [CreateItem(local.PeerId.Value, remote.PeerId.Value, now, attempts: 0)]
        };
        var sender = new CapturingSender();
        var worker = new OutboxDeliveryWorker(queue, sender, new FixedTimeProvider(now));

        var result = await worker.ProcessDueAsync(local.PeerId);

        Assert.Equal(new OutboxDeliveryCycleResult(1, 1, 0, 0), result);
        Assert.Equal(remote.PeerId, sender.LastRecipient);
        Assert.NotNull(sender.LastFrame);
        Assert.True(PeerMessagingProtocol.TryDecode(sender.LastFrame, out var decoded, out var error), error);
        var text = Assert.IsType<PeerTextMessageFrame>(decoded);
        Assert.Equal(MessageId, text.MessageId);
        Assert.Equal(now.AddMinutes(-1), text.CreatedAt);
        Assert.Equal("retry-safe payload", text.Text);
        Assert.Equal(now.AddSeconds(10), queue.LastSentNextAttemptAt);
        Assert.Equal(1, queue.RecordSentCalls);
        Assert.Equal(0, queue.RecordFailureCalls);
    }

    [Fact]
    public async Task FailedSend_StoresOnlyExceptionTypeCodeAndBacksOff()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_786_112_100_000);
        var queue = new StubOutboxQueue
        {
            Due = [CreateItem(local.PeerId.Value, remote.PeerId.Value, now, attempts: 3)]
        };
        var sender = new CapturingSender
        {
            Failure = new InvalidOperationException("candidate 203.0.113.7:5000 failed")
        };
        var worker = new OutboxDeliveryWorker(queue, sender, new FixedTimeProvider(now));

        var result = await worker.ProcessDueAsync(local.PeerId);

        Assert.Equal(new OutboxDeliveryCycleResult(1, 0, 1, 0), result);
        Assert.Equal("send:InvalidOperationException", queue.LastFailureCode);
        Assert.DoesNotContain("203.0.113.7", queue.LastFailureCode, StringComparison.Ordinal);
        Assert.Equal(now.AddSeconds(16), queue.LastFailureNextAttemptAt);
        Assert.Equal(0, queue.RecordSentCalls);
        Assert.Equal(1, queue.RecordFailureCalls);
    }

    [Fact]
    public async Task AckWaitBackoff_IsBounded()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_786_112_200_000);
        var queue = new StubOutboxQueue
        {
            Due = [CreateItem(local.PeerId.Value, remote.PeerId.Value, now, attempts: 10)]
        };
        var worker = new OutboxDeliveryWorker(queue, new CapturingSender(), new FixedTimeProvider(now));

        await worker.ProcessDueAsync(local.PeerId);

        Assert.Equal(now.AddMinutes(2), queue.LastSentNextAttemptAt);
    }

    [Fact]
    public async Task WrongLocalSender_IsRejectedBeforeNetworkSend()
    {
        using var local = PeerIdentity.Generate();
        using var anotherLocal = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var queue = new StubOutboxQueue
        {
            Due = [CreateItem(anotherLocal.PeerId.Value, remote.PeerId.Value, now, attempts: 0)]
        };
        var sender = new CapturingSender();
        var worker = new OutboxDeliveryWorker(queue, sender, new FixedTimeProvider(now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessDueAsync(local.PeerId));
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(0, queue.RecordSentCalls);
        Assert.Equal(0, queue.RecordFailureCalls);
    }

    [Fact]
    public async Task ConcurrentAckDuringSend_IsReportedWithoutResurrectingOutbox()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var queue = new StubOutboxQueue
        {
            Due = [CreateItem(local.PeerId.Value, remote.PeerId.Value, now, attempts: 0)],
            RecordSentReturnValue = false
        };
        var worker = new OutboxDeliveryWorker(queue, new CapturingSender(), new FixedTimeProvider(now));

        var result = await worker.ProcessDueAsync(local.PeerId);

        Assert.Equal(new OutboxDeliveryCycleResult(1, 0, 0, 1), result);
    }

    private static DueOutboxMessage CreateItem(
        string senderPeerId,
        string recipientPeerId,
        DateTimeOffset now,
        int attempts)
        => new(
            MessageId,
            senderPeerId,
            recipientPeerId,
            now.AddMinutes(-1),
            "retry-safe payload",
            attempts,
            now);

    private sealed class StubOutboxQueue : IOutboxDeliveryQueue
    {
        public IReadOnlyList<DueOutboxMessage> Due { get; init; } = [];
        public bool RecordSentReturnValue { get; init; } = true;
        public bool RecordFailureReturnValue { get; init; } = true;
        public int RecordSentCalls { get; private set; }
        public int RecordFailureCalls { get; private set; }
        public DateTimeOffset? LastSentNextAttemptAt { get; private set; }
        public DateTimeOffset? LastFailureNextAttemptAt { get; private set; }
        public string? LastFailureCode { get; private set; }

        public Task<IReadOnlyList<DueOutboxMessage>> GetDueOutboxAsync(
            DateTimeOffset dueAtOrBefore,
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Due);

        public Task<bool> RecordOutboundSentAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            RecordSentCalls++;
            LastSentNextAttemptAt = nextAttemptAt;
            return Task.FromResult(RecordSentReturnValue);
        }

        public Task<bool> RecordOutboundFailureAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            string failureCode,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            RecordFailureCalls++;
            LastFailureCode = failureCode;
            LastFailureNextAttemptAt = nextAttemptAt;
            return Task.FromResult(RecordFailureReturnValue);
        }
    }

    private sealed class CapturingSender : IPeerApplicationFrameSender
    {
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }
        public Dyract.Core.Identity.PeerId LastRecipient { get; private set; }
        public byte[]? LastFrame { get; private set; }

        public Task SendAsync(
            Dyract.Core.Identity.PeerId recipientPeerId,
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            LastRecipient = recipientPeerId;
            LastFrame = frame.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
