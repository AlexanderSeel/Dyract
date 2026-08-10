using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Dyract.Transport;

namespace Dyract.Client;

public sealed record AttachmentOutboxCycleResult(
    int TransfersExamined,
    int FramesSent,
    int TransfersFailed,
    int ChangedConcurrently);

public sealed class AttachmentOutboxWorker
{
    private readonly IAttachmentSendStore _outbox;
    private readonly IPeerApplicationFrameSender _sender;
    private readonly TimeProvider _timeProvider;

    public AttachmentOutboxWorker(
        IAttachmentSendStore outbox,
        IPeerApplicationFrameSender sender,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AttachmentOutboxCycleResult> ProcessDueAsync(
        PeerId localPeerId,
        int transferLimit = 4,
        int chunksPerTransfer = 16,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalPeer(localPeerId);
        var due = await _outbox.GetDueAsync(
            _timeProvider.GetUtcNow(),
            transferLimit,
            chunksPerTransfer,
            cancellationToken);

        var framesSent = 0;
        var failed = 0;
        var changedConcurrently = 0;

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(item.SenderPeerId, localPeerId.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Attachment outbox item does not belong to the active local identity.");
            }

            if (!PeerId.TryParse(item.RecipientPeerId, out var recipientPeerId) || recipientPeerId == localPeerId)
            {
                throw new InvalidOperationException("Attachment outbox recipient PeerId is invalid.");
            }

            try
            {
                if (item.SendManifest)
                {
                    var manifestFrame = AttachmentApplicationFrameProtocol.Encode(
                        new AttachmentManifestApplicationFrame(item.Manifest));
                    try
                    {
                        await _sender.SendAsync(recipientPeerId, manifestFrame, cancellationToken);
                        framesSent++;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(manifestFrame);
                    }
                }

                foreach (var chunk in item.Chunks)
                {
                    var chunkFrame = AttachmentApplicationFrameProtocol.Encode(
                        new AttachmentChunkApplicationFrame(chunk));
                    try
                    {
                        await _sender.SendAsync(recipientPeerId, chunkFrame, cancellationToken);
                        framesSent++;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunkFrame);
                    }
                }

                var nextAttempt = _timeProvider.GetUtcNow().Add(
                    OutboxDeliveryWorker.ComputeAckRetryDelay(item.Attempts));
                if (!await _outbox.RecordAttemptSentAsync(
                        localPeerId.Value,
                        recipientPeerId.Value,
                        item.Manifest.AttachmentId,
                        nextAttempt,
                        cancellationToken))
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
                var nextAttempt = _timeProvider.GetUtcNow().Add(
                    OutboxDeliveryWorker.ComputeFailureDelay(item.Attempts));
                var failureCode = $"send:{exception.GetType().Name}";
                if (await _outbox.RecordAttemptFailureAsync(
                        localPeerId.Value,
                        recipientPeerId.Value,
                        item.Manifest.AttachmentId,
                        failureCode,
                        nextAttempt,
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
                foreach (var chunk in item.Chunks)
                {
                    CryptographicOperations.ZeroMemory(chunk.Data);
                }
            }
        }

        return new AttachmentOutboxCycleResult(
            due.Count,
            framesSent,
            failed,
            changedConcurrently);
    }

    public Task<bool> ApplyResumeAsync(
        PeerId localPeerId,
        PeerId remotePeerId,
        AttachmentResumeApplicationFrame resume,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(localPeerId, remotePeerId);
        return _outbox.ApplyResumeAsync(
            localPeerId.Value,
            remotePeerId.Value,
            resume,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task<bool> ApplyCompletionAsync(
        PeerId localPeerId,
        PeerId remotePeerId,
        AttachmentCompletionAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(localPeerId, remotePeerId);
        return _outbox.MarkCompletedAsync(
            localPeerId.Value,
            remotePeerId.Value,
            acknowledgement,
            cancellationToken);
    }

    private static void ValidateLocalPeer(PeerId localPeerId)
    {
        if (string.IsNullOrWhiteSpace(localPeerId.Value))
        {
            throw new ArgumentException("Local PeerId must be initialized.", nameof(localPeerId));
        }
    }

    private static void ValidatePeerPair(PeerId localPeerId, PeerId remotePeerId)
    {
        ValidateLocalPeer(localPeerId);
        if (string.IsNullOrWhiteSpace(remotePeerId.Value) || remotePeerId == localPeerId)
        {
            throw new ArgumentException("Attachment acknowledgement peer scope is invalid.", nameof(remotePeerId));
        }
    }
}
