using System.Security.Cryptography;
using Dyract.Storage;
using Microsoft.Maui.Storage;

namespace Dyract.App.Security;

public sealed class SecureLocalEncryptionKeyProvider : ILocalEncryptionKeyProvider
{
    private const string InitializationMarker = "dyract.localdata.initialized.v1";
    private const string EncryptionKeyName = "dyract.localdata.aes256.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInstallBoundary();

            string? encoded;
            try
            {
                encoded = await SecureStorage.Default.GetAsync(EncryptionKeyName);
            }
            catch (Exception exception)
            {
                throw new CryptographicException("Dyract could not read the local data encryption key.", exception);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(encoded))
            {
                byte[] existing;
                try
                {
                    existing = Convert.FromBase64String(encoded);
                }
                catch (FormatException exception)
                {
                    throw new CryptographicException("The local data encryption key has an invalid format.", exception);
                }

                if (existing.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(existing);
                    throw new CryptographicException("The local data encryption key has an invalid length.");
                }

                return existing;
            }

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                await SecureStorage.Default.SetAsync(EncryptionKeyName, Convert.ToBase64String(key));
                cancellationToken.ThrowIfCancellationRequested();
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void EnsureInstallBoundary()
    {
        if (Preferences.Default.Get(InitializationMarker, false))
        {
            return;
        }

        // Keychain entries can survive an iOS uninstall while the app database does not.
        // Treat a reinstall as a fresh local-data boundary until explicit backup/recovery exists.
        SecureStorage.Default.Remove(EncryptionKeyName);
        Preferences.Default.Set(InitializationMarker, true);
    }
}
