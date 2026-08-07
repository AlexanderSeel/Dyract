using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Microsoft.Maui.Storage;

namespace Dyract.Transport.AndroidHarness;

public sealed class HarnessIdentityVault
{
    private const string InitializationMarker = "dyract.transportharness.identity.initialized.v1";
    private const string PrivateKeyName = "dyract.transportharness.identity.pkcs8.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<PeerIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInstallBoundary();

            var encoded = await SecureStorage.Default.GetAsync(PrivateKeyName);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(encoded))
            {
                byte[] key;
                try
                {
                    key = Convert.FromBase64String(encoded);
                }
                catch (FormatException exception)
                {
                    throw new CryptographicException("Stored diagnostic identity has an invalid format.", exception);
                }

                try
                {
                    return PeerIdentity.ImportPkcs8PrivateKey(key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }

            var identity = PeerIdentity.Generate();
            var privateKey = identity.ExportPkcs8PrivateKey();
            try
            {
                await SecureStorage.Default.SetAsync(PrivateKeyName, Convert.ToBase64String(privateKey));
                cancellationToken.ThrowIfCancellationRequested();
                return identity;
            }
            catch
            {
                identity.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
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

        SecureStorage.Default.Remove(PrivateKeyName);
        Preferences.Default.Set(InitializationMarker, true);
    }
}
