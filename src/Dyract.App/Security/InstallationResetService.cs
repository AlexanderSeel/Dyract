using Dyract.App.Attachments;
using Dyract.App.Directory;
using Dyract.Storage;
using Microsoft.Maui.Storage;

namespace Dyract.App.Security;

public interface IInstallationResetService
{
    Task CompletePendingResetAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class InstallationResetService : IInstallationResetService
{
    internal const string PendingResetMarker = "dyract.installation-reset.pending.v1";
    internal const string LocalDatabaseFileName = "dyract-local-v1.db3";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IDirectorySettingsStore _directorySettings;

    public InstallationResetService(IDirectorySettingsStore directorySettings)
    {
        _directorySettings = directorySettings ?? throw new ArgumentNullException(nameof(directorySettings));
    }

    public static string LocalDatabasePath =>
        Path.Combine(FileSystem.AppDataDirectory, LocalDatabaseFileName);

    public async Task CompletePendingResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Preferences.Default.Get(PendingResetMarker, false))
        {
            return;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Preferences.Default.Get(PendingResetMarker, false))
            {
                // Once an explicit reset has started, finish it even if the caller is later
                // cancelled. The persisted marker makes process interruption resumable.
                await CompleteResetCoreAsync(CancellationToken.None);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Preferences.Default.Set(PendingResetMarker, true);

            // Do not leave an intentionally destructive reset half-finished because the UI
            // navigation token was cancelled. Process termination is handled by the marker.
            await CompleteResetCoreAsync(CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task CompleteResetCoreAsync(CancellationToken cancellationToken)
    {
        var databasePath = LocalDatabasePath;
        if (File.Exists(databasePath))
        {
            await SqliteLocalResetter.ResetUserDataAsync(databasePath, cancellationToken);
        }

        var attachmentDirectory = AppOwnedAttachmentStorage.RootDirectoryPath;
        if (Directory.Exists(attachmentDirectory))
        {
            Directory.Delete(attachmentDirectory, recursive: true);
        }

        _directorySettings.Clear();

        SecureStorage.Default.Remove(SecureIdentityVault.PrivateKeyName);
        SecureStorage.Default.Remove(SecureLocalEncryptionKeyProvider.EncryptionKeyName);
        Preferences.Default.Remove(SecureIdentityVault.InitializationMarker);
        Preferences.Default.Remove(SecureLocalEncryptionKeyProvider.InitializationMarker);

        Preferences.Default.Remove(PendingResetMarker);
    }
}
