namespace Dyract.Storage;

/// <summary>
/// Initializes the established v1 store and then records/applies the formal migration ledger.
/// Existing v1 databases are adopted without rewriting user data.
/// </summary>
public sealed class MigratingLocalStore : ILocalStore
{
    private readonly SqliteLocalStore _inner;
    private readonly SqliteSchemaMigrationRunner _migrations;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public MigratingLocalStore(string databasePath, ILocalEncryptionKeyProvider keyProvider)
    {
        _inner = new SqliteLocalStore(databasePath, keyProvider);
        _migrations = new SqliteSchemaMigrationRunner(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            // SqliteLocalStore owns the historical v1 bootstrap. Once it exists, the
            // migration runner adopts that schema into an append-only migration ledger.
            await _inner.InitializeAsync(cancellationToken);
            await _migrations.ApplyAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task UpsertContactAsync(ContactDraft contact, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _inner.UpsertContactAsync(contact, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalContact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.GetContactsAsync(cancellationToken);
    }

    public async Task<LocalContact?> GetContactAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.GetContactAsync(peerId, cancellationToken);
    }

    public async Task<LocalConversation> GetOrCreateConversationAsync(
        string peerId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.GetOrCreateConversationAsync(peerId, cancellationToken);
    }

    public async Task<LocalMessage> QueueOutgoingTextAsync(
        string conversationId,
        string senderPeerId,
        string recipientPeerId,
        string text,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.QueueOutgoingTextAsync(
            conversationId,
            senderPeerId,
            recipientPeerId,
            text,
            createdAt,
            cancellationToken);
    }

    public async Task<IReadOnlyList<LocalMessage>> GetMessagesAsync(
        string conversationId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.GetMessagesAsync(conversationId, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<PendingOutboxItem>> GetPendingOutboxAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _inner.GetPendingOutboxAsync(limit, cancellationToken);
    }

    public async Task RecordOutboxFailureAsync(
        string messageId,
        string? error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _inner.RecordOutboxFailureAsync(messageId, error, nextAttemptAt, cancellationToken);
    }

    public async Task MarkDeliveredAsync(
        string messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _inner.MarkDeliveredAsync(messageId, deliveredAt, cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }
}
