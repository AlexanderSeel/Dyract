namespace Dyract.Storage;

public enum LocalMessageDirection
{
    Incoming = 0,
    Outgoing = 1
}

public enum LocalMessageType
{
    Text = 1
}

public enum LocalMessageState
{
    Queued = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}

public sealed record ContactDraft(
    string PeerId,
    byte[] IdentityPublicKey,
    string DisplayName,
    string? Capability = null);

public sealed record LocalContact(
    string PeerId,
    byte[] IdentityPublicKey,
    string DisplayName,
    string? Capability,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt)
{
    public string ShortPeerId => PeerId.Length <= 18 ? PeerId : $"{PeerId[..10]}…{PeerId[^6..]}";
    public string PairingStatus => Capability is null
        ? "Identity pinned — pairing response still needed"
        : "Pairing response stored";
}

public sealed record LocalConversation(
    string ConversationId,
    string PeerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public sealed record LocalMessage(
    string MessageId,
    string ConversationId,
    string SenderPeerId,
    string RecipientPeerId,
    LocalMessageDirection Direction,
    LocalMessageType Type,
    LocalMessageState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt,
    string Text);

public sealed record IncomingMessageStoreResult(
    LocalMessage Message,
    bool IsNew);

public sealed record PendingOutboxItem(
    string MessageId,
    string ConversationId,
    string RecipientPeerId,
    string Text,
    int Attempts,
    DateTimeOffset NextAttemptAt);

public sealed record DueOutboxMessage(
    string MessageId,
    string SenderPeerId,
    string RecipientPeerId,
    DateTimeOffset CreatedAt,
    string Text,
    int Attempts,
    DateTimeOffset NextAttemptAt);
