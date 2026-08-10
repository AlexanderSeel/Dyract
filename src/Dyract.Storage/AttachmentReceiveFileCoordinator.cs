using Dyract.Protocol;

namespace Dyract.Storage;

public interface IAttachmentStorageCapacity
{
    ValueTask<long?> GetAvailableBytesAsync(CancellationToken cancellationToken = default);
}

public interface IAttachmentReceiveDestinationFactory
{
    Task<IAttachmentReceiveDestination> CreateAsync(
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default);
}

public interface IAttachmentReceiveDestination : IAsyncDisposable
{
    Stream StagingStream { get; }
    Task PromoteAsync(CancellationToken cancellationToken = default);
    Task AbortAsync(CancellationToken cancellationToken = default);
}

public sealed record AttachmentReceiveFileCompletion(
    AttachmentCompletionAcknowledgement Acknowledgement,
    bool AlreadyCompleted);

/// <summary>
/// Bridges encrypted durable receive state to a caller/platform-owned file destination without
/// changing the transport boundary. Promotion happens only after exact reconstruction verification,
/// and the durable DYAC completion receipt is committed only after promotion succeeds.
/// </summary>
public sealed class AttachmentReceiveFileCoordinator
{
    private readonly SqliteAttachmentReceiveStore _receiveStore;
    private readonly IAttachmentReceiveDestinationFactory _destinationFactory;
    private readonly IAttachmentStorageCapacity _storageCapacity;

    public AttachmentReceiveFileCoordinator(
        SqliteAttachmentReceiveStore receiveStore,
        IAttachmentReceiveDestinationFactory destinationFactory,
        IAttachmentStorageCapacity storageCapacity)
    {
        _receiveStore = receiveStore ?? throw new ArgumentNullException(nameof(receiveStore));
        _destinationFactory = destinationFactory ?? throw new ArgumentNullException(nameof(destinationFactory));
        _storageCapacity = storageCapacity ?? throw new ArgumentNullException(nameof(storageCapacity));
    }

    public async Task<AttachmentReceiveFileCompletion> CompleteAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var completed = await _receiveStore.GetCompletionReceiptAsync(
            senderPeerId,
            attachmentId,
            cancellationToken);
        if (completed is not null)
        {
            return new AttachmentReceiveFileCompletion(completed.Acknowledgement, AlreadyCompleted: true);
        }

        var manifest = await _receiveStore.GetManifestAsync(senderPeerId, attachmentId, cancellationToken)
            ?? throw new InvalidDataException("Attachment receive state does not exist.");

        var availableBytes = await _storageCapacity.GetAvailableBytesAsync(cancellationToken);
        if (availableBytes is >= 0 && availableBytes.Value < manifest.SizeBytes)
        {
            throw new IOException(
                $"Insufficient local storage for verified attachment staging. Required {manifest.SizeBytes} bytes; available {availableBytes.Value} bytes.");
        }

        await using var destination = await _destinationFactory.CreateAsync(manifest, cancellationToken);
        try
        {
            var verified = await _receiveStore.WriteVerifiedStagingAsync(
                senderPeerId,
                attachmentId,
                destination.StagingStream,
                cancellationToken);

            await destination.PromoteAsync(cancellationToken);
            var acknowledgement = await _receiveStore.MarkCompletedAsync(verified, cancellationToken);
            return new AttachmentReceiveFileCompletion(acknowledgement, AlreadyCompleted: false);
        }
        catch
        {
            await BestEffortAbortAsync(destination);
            throw;
        }
    }

    private static async Task BestEffortAbortAsync(IAttachmentReceiveDestination destination)
    {
        try
        {
            await destination.AbortAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original verification/promotion/completion failure.
        }
    }
}
