using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentReceiveFileCoordinatorTests
{
    [Fact]
    public async Task InsufficientCapacityRejectsBeforeDestinationOrCompletion()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeDestinationFactory();
            var coordinator = new AttachmentReceiveFileCoordinator(
                fixture.Store,
                factory,
                new FixedCapacity(fixture.Manifest.SizeBytes - 1));

            await Assert.ThrowsAsync<IOException>(() => coordinator.CompleteAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));

            Assert.Equal(0, factory.CreateCount);
            Assert.NotNull(await fixture.Store.GetManifestAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));
            Assert.Null(await fixture.Store.GetCompletionReceiptAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task VerifiedStagingPromotesBeforeDurableCompletionAndReplaySkipsDestination()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeDestinationFactory();
            var coordinator = new AttachmentReceiveFileCoordinator(
                fixture.Store,
                factory,
                new FixedCapacity(fixture.Manifest.SizeBytes));

            var completed = await coordinator.CompleteAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId);

            Assert.False(completed.AlreadyCompleted);
            Assert.Equal(fixture.Manifest.AttachmentId, completed.Acknowledgement.AttachmentId);
            Assert.Equal(fixture.Content, factory.LastPromotedBytes);
            Assert.Equal(1, factory.CreateCount);
            Assert.Null(await fixture.Store.GetManifestAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));
            Assert.NotNull(await fixture.Store.GetCompletionReceiptAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));

            var replay = await coordinator.CompleteAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId);
            Assert.True(replay.AlreadyCompleted);
            Assert.Equal(completed.Acknowledgement, replay.Acknowledgement);
            Assert.Equal(1, factory.CreateCount);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task PromotionFailureDoesNotCommitCompletionReceipt()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeDestinationFactory(failPromotion: true);
            var coordinator = new AttachmentReceiveFileCoordinator(
                fixture.Store,
                factory,
                new FixedCapacity(fixture.Manifest.SizeBytes + 1024));

            await Assert.ThrowsAsync<IOException>(() => coordinator.CompleteAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));

            Assert.True(factory.Aborted);
            Assert.NotNull(await fixture.Store.GetManifestAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));
            Assert.Null(await fixture.Store.GetCompletionReceiptAsync(
                fixture.SenderPeerId,
                fixture.Manifest.AttachmentId));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "dyract-attachment-file-coordinator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local.db3");
        var keyProvider = new FixedKeyProvider(0x49);
        var localStore = new MigratingLocalStore(databasePath, keyProvider);
        var store = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);
        using var sender = PeerIdentity.Generate();
        var senderPeerId = sender.PeerId.Value;
        var content = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 31);
        var manifest = AttachmentProtocol.CreateManifest(
            "received.bin",
            "application/octet-stream",
            content.Length,
            SHA256.HashData(content),
            "66666666666666666666666666666666");

        Assert.Equal(AttachmentManifestStoreResult.Created, await store.StoreManifestAsync(senderPeerId, manifest));
        for (var index = 0; index < manifest.ChunkCount; index++)
        {
            var offset = index * manifest.ChunkSize;
            var length = Math.Min(manifest.ChunkSize, content.Length - offset);
            await store.StoreChunkAsync(
                senderPeerId,
                AttachmentProtocol.CreateChunk(manifest, index, content.AsSpan(offset, length)));
        }

        return new Fixture(directory, store, senderPeerId, manifest, content);
    }

    private sealed record Fixture(
        string DirectoryPath,
        SqliteAttachmentReceiveStore Store,
        string SenderPeerId,
        AttachmentManifest Manifest,
        byte[] Content) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class FixedCapacity(long? availableBytes) : IAttachmentStorageCapacity
    {
        public ValueTask<long?> GetAvailableBytesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(availableBytes);
    }

    private sealed class FakeDestinationFactory(bool failPromotion = false) : IAttachmentReceiveDestinationFactory
    {
        public int CreateCount { get; private set; }
        public byte[]? LastPromotedBytes { get; private set; }
        public bool Aborted { get; private set; }

        public Task<IAttachmentReceiveDestination> CreateAsync(
            AttachmentManifest manifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return Task.FromResult<IAttachmentReceiveDestination>(new Destination(this, failPromotion));
        }

        private sealed class Destination(
            FakeDestinationFactory owner,
            bool failPromotion) : IAttachmentReceiveDestination
        {
            private readonly MemoryStream _staging = new();

            public Stream StagingStream => _staging;

            public Task PromoteAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failPromotion)
                {
                    throw new IOException("simulated-promotion-failure");
                }

                owner.LastPromotedBytes = _staging.ToArray();
                return Task.CompletedTask;
            }

            public Task AbortAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.Aborted = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _staging.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FixedKeyProvider(byte fill) : ILocalEncryptionKeyProvider
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Enumerable.Repeat(fill, 32).Select(value => (byte)value).ToArray());
    }
}
