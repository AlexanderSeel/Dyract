using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Microsoft.Maui.Storage;

namespace Dyract.App.Security;

public interface IIdentityVault
{
    Task<PeerIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default);
}

public sealed class SecureIdentityVault : IIdentityVault
{
    private const string InitializationMarker = "dyract.identity.initialized.v1";
    private const string PrivateKeyName = "dyract.identity.pkcs8.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<PeerIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInstallBoundary();

            string? encodedPrivateKey;

            try
            {
                encodedPrivateKey = await SecureStorage.Default.GetAsync(PrivateKeyName);
            }
            catch (Exception exception)
            {
                throw new IdentityVaultException(
                    "The secure identity could not be read. Dyract will not silently replace it because that would change the Peer ID.",
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(encodedPrivateKey))
            {
                return ImportExistingIdentity(encodedPrivateKey);
            }

            return await CreateIdentityAsync(cancellationToken);
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

        // iOS Keychain entries can survive an app uninstall. Dyract currently defines
        // a reinstall without an explicit recovery flow as a new installation/identity.
        SecureStorage.Default.Remove(PrivateKeyName);
        Preferences.Default.Set(InitializationMarker, true);
    }

    private static PeerIdentity ImportExistingIdentity(string encodedPrivateKey)
    {
        byte[] privateKey;

        try
        {
            privateKey = Convert.FromBase64String(encodedPrivateKey);
        }
        catch (FormatException exception)
        {
            throw new IdentityVaultException("The stored identity has an invalid format.", exception);
        }

        try
        {
            return PeerIdentity.ImportPkcs8PrivateKey(privateKey);
        }
        catch (CryptographicException exception)
        {
            throw new IdentityVaultException("The stored identity cannot be imported.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static async Task<PeerIdentity> CreateIdentityAsync(CancellationToken cancellationToken)
    {
        var identity = PeerIdentity.Generate();
        var privateKey = identity.ExportPkcs8PrivateKey();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = Convert.ToBase64String(privateKey);
            await SecureStorage.Default.SetAsync(PrivateKeyName, encoded);
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
}

public sealed class IdentityVaultException : Exception
{
    public IdentityVaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
