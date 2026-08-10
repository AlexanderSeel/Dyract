using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.OS;
#elif IOS
using Foundation;
#endif

namespace Dyract.App.Attachments;

/// <summary>
/// App-owned attachment destination for the shipping mobile app. Remote filenames remain display
/// metadata only: local paths are generated from the canonical attachment ID plus a conservative
/// cosmetic extension. Staging and final files stay inside the app sandbox.
/// </summary>
public sealed class AppOwnedAttachmentStorage :
    IAttachmentStorageCapacity,
    IAttachmentReceiveDestinationFactory
{
    public static string RootDirectoryPath =>
        Path.Combine(FileSystem.AppDataDirectory, "attachments");

    private readonly string _rootDirectory;
    private readonly string _stagingDirectory;
    private readonly string _receivedDirectory;

    public AppOwnedAttachmentStorage()
    {
        _rootDirectory = RootDirectoryPath;
        _stagingDirectory = Path.Combine(_rootDirectory, "staging");
        _receivedDirectory = Path.Combine(_rootDirectory, "received");
    }

    public ValueTask<long?> GetAvailableBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectories();

#if ANDROID
        using var statistics = new StatFs(_rootDirectory);
        return ValueTask.FromResult<long?>(statistics.AvailableBytes);
#elif IOS
        var attributes = NSFileManager.DefaultManager.GetFileSystemAttributes(_rootDirectory);
        if (attributes is null || attributes.FreeSize > long.MaxValue)
        {
            return ValueTask.FromResult<long?>(null);
        }

        return ValueTask.FromResult<long?>((long)attributes.FreeSize);
#else
        return ValueTask.FromResult<long?>(null);
#endif
    }

    public Task<IAttachmentReceiveDestination> CreateAsync(
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttachmentProtocol.ValidateManifest(manifest);
        EnsureDirectories();

        var extension = GetSafeExtension(manifest.FileName);
        var finalPath = Path.Combine(_receivedDirectory, manifest.AttachmentId + extension);
        var stagingPath = Path.Combine(
            _stagingDirectory,
            $"{manifest.AttachmentId}-{Guid.NewGuid():N}.part");
        var stream = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            AttachmentProtocol.ChunkSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        IAttachmentReceiveDestination destination = new AppOwnedAttachmentDestination(
            manifest,
            stagingPath,
            finalPath,
            stream);
        return Task.FromResult(destination);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_stagingDirectory);
        Directory.CreateDirectory(_receivedDirectory);

#if IOS
        var error = NSFileManager.SetSkipBackupAttribute(_rootDirectory, skipBackup: true);
        if (error is not null)
        {
            throw new IOException("Attachment storage could not be excluded from device cloud backup.");
        }
#endif
    }

    private static string GetSafeExtension(string displayFileName)
    {
        var extension = Path.GetExtension(displayFileName);
        if (extension.Length is < 2 or > 11)
        {
            return ".bin";
        }

        foreach (var character in extension.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return ".bin";
            }
        }

        return extension.ToLowerInvariant();
    }

    private sealed class AppOwnedAttachmentDestination(
        AttachmentManifest manifest,
        string stagingPath,
        string finalPath,
        FileStream stagingStream) : IAttachmentReceiveDestination
    {
        private FileStream? _stagingStream = stagingStream;

        public Stream StagingStream => _stagingStream
            ?? throw new InvalidOperationException("Attachment staging stream is no longer writable.");

        public async Task PromoteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stagingStream is null)
            {
                throw new InvalidOperationException("Attachment staging destination has already been closed.");
            }

            await _stagingStream.FlushAsync(cancellationToken);
            await _stagingStream.DisposeAsync();
            _stagingStream = null;

            if (File.Exists(finalPath))
            {
                await using var existing = new FileStream(
                    finalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    AttachmentProtocol.ChunkSizeBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!await AttachmentProtocol.VerifySha256Async(existing, manifest, cancellationToken))
                {
                    throw new InvalidDataException("Existing generated attachment destination does not match the verified manifest.");
                }

                TryDelete(stagingPath);
            }
            else
            {
                File.Move(stagingPath, finalPath);
            }

#if IOS
            var error = NSFileManager.SetSkipBackupAttribute(finalPath, skipBackup: true);
            if (error is not null)
            {
                throw new IOException("Received attachment could not be excluded from device cloud backup.");
            }
#endif
        }

        public async Task AbortAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stagingStream is not null)
            {
                await _stagingStream.DisposeAsync();
                _stagingStream = null;
            }

            TryDelete(stagingPath);
        }

        public async ValueTask DisposeAsync()
        {
            if (_stagingStream is not null)
            {
                await _stagingStream.DisposeAsync();
                _stagingStream = null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup is best effort; the coordinator preserves the primary failure.
            }
        }
    }
}
