using Dyract.Protocol;

namespace Dyract.Storage;

public sealed record DueAttachmentSend(
    string SenderPeerId,
    string RecipientPeerId,
    AttachmentManifest Manifest,
    IReadOnlyList<AttachmentChunk> Chunks,
    bool SendManifest,
    bool CompletionProbe,
    int Attempts);

public interface IAttachmentSendStore
{
    Task QueueAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentManifest manifest,
        IAsyncEnumerable<AttachmentChunk> chunks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DueAttachmentSend>> GetDueAsync(
        DateTimeOffset dueAtOrBefore,
        int transferLimit = 4,
        int chunksPerTransfer = 16,
        CancellationToken cancellationToken = default);

    Task<bool> RecordAttemptSentAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task<bool> RecordAttemptFailureAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        string failureCode,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task<bool> ApplyResumeAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentResumeApplicationFrame resume,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task<bool> MarkCompletedAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentCompletionAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}
