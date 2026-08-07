namespace Dyract.Storage;

public interface ILocalEncryptionKeyProvider
{
    ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default);
}

public interface ILocalStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertContactAsync(ContactDraft contact, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalContact>> GetContactsAsync(CancellationToken cancellationToken = default);
    Task<LocalContact?> GetContactAsync(string peerId, CancellationToken cancellationToken = default);

    Task<LocalConversation> GetOrCreateConversationAsync(
        string peerId,
        CancellationToken cancellationToken = default);

    Task<LocalMessage> QueueOutgoingTextAsync(
        string conversationId,
        string senderPeerId,
        string recipientPeerId,
        string text,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalMessage>> GetMessagesAsync(
        string conversationId,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingOutboxItem>> GetPendingOutboxAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task RecordOutboxFailureAsync(
        string messageId,
        string? error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(
        string messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}

public interface IIncomingMessageStore
{
    Task<IncomingMessageStoreResult> StoreIncomingTextAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        string text,
        DateTimeOffset createdAt,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);
}

public interface IOutgoingDeliveryStore
{
    Task<bool> MarkOutgoingDeliveredAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}
