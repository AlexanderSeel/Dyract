using System.Security.Cryptography;
using Android.Security.Keystore;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Signatures;
using Java.Security;
using Java.Security.Spec;
using KeyStore = Java.Security.KeyStore;
using Signature = Java.Security.Signature;

namespace Dyract.App.Security;

/// <summary>
/// Android-only P-256 identity signer backed by an AndroidKeyStore private key.
/// The private key is referenced by alias and is never exported as PKCS#8 bytes.
///
/// This implementation is not yet selected by SecureIdentityVault. Existing installations
/// continue using the current SecureStorage-backed PeerIdentity until migration and recovery
/// semantics have been reviewed and physical-device behavior has been validated.
/// </summary>
public sealed class AndroidKeystoreIdentitySigner : IPeerIdentitySigner, IDisposable
{
    private const string ProviderName = "AndroidKeyStore";
    private const string SignatureAlgorithm = "SHA256withECDSA";
    private const string CurveName = "secp256r1";

    private readonly string _alias;
    private readonly KeyStore _keyStore;
    private readonly byte[] _publicKey;
    private bool _disposed;

    private AndroidKeystoreIdentitySigner(
        string alias,
        KeyStore keyStore,
        byte[] publicKey)
    {
        _alias = alias;
        _keyStore = keyStore;
        _publicKey = publicKey;
        PeerId = PeerId.FromPublicKey(_publicKey);
    }

    public PeerId PeerId { get; }

    public string Alias => _alias;

    public static AndroidKeystoreIdentitySigner CreateOrLoad(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var keyStore = KeyStore.GetInstance(ProviderName)
            ?? throw new CryptographicException("AndroidKeyStore provider is unavailable.");

        try
        {
            keyStore.Load(null);

            if (!keyStore.ContainsAlias(alias))
            {
                Generate(alias);
            }

            var certificate = keyStore.GetCertificate(alias)
                ?? throw new CryptographicException("AndroidKeyStore identity certificate is missing.");
            var publicKey = certificate.PublicKey?.GetEncoded()
                ?? throw new CryptographicException("AndroidKeyStore identity public key is unavailable.");

            if (!SignatureVerifier.IsValidIdentityPublicKey(publicKey))
            {
                throw new CryptographicException("AndroidKeyStore identity is not a valid Dyract P-256 public key.");
            }

            return new AndroidKeystoreIdentitySigner(alias, keyStore, publicKey);
        }
        catch
        {
            keyStore.Dispose();
            throw;
        }
    }

    public byte[] ExportPublicKey()
    {
        ThrowIfDisposed();
        return _publicKey.ToArray();
    }

    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        if (payload.IsEmpty)
        {
            throw new ArgumentException("Payload must not be empty.", nameof(payload));
        }

        var entry = _keyStore.GetEntry(_alias, null) as KeyStore.PrivateKeyEntry
            ?? throw new CryptographicException("AndroidKeyStore identity private-key entry is unavailable.");
        using (entry)
        {
            var signer = Signature.GetInstance(SignatureAlgorithm)
                ?? throw new CryptographicException("Android ECDSA signing provider is unavailable.");
            using (signer)
            {
                signer.InitSign(entry.PrivateKey);
                signer.Update(payload.ToArray());
                var derSignature = signer.Sign()
                    ?? throw new CryptographicException("AndroidKeyStore returned an empty identity signature.");
                try
                {
                    return EcdsaSignatureEncoding.DerToP256P1363(derSignature);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(derSignature);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_publicKey);
        _keyStore.Dispose();
        _disposed = true;
    }

    private static void Generate(string alias)
    {
        var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, ProviderName)
            ?? throw new CryptographicException("AndroidKeyStore EC key generator is unavailable.");

        using (generator)
        using (var curve = new ECGenParameterSpec(CurveName))
        using (var builder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Sign)
                   .SetAlgorithmParameterSpec(curve)
                   .SetDigests(KeyProperties.DigestSha256))
        using (var parameters = builder.Build())
        {
            generator.Initialize(parameters);
            using var keyPair = generator.GenerateKeyPair();
            var generatedPublicKey = keyPair?.Public;
            var generatedPrivateKey = keyPair?.Private;
            if (generatedPublicKey is null || generatedPrivateKey is null)
            {
                throw new CryptographicException("AndroidKeyStore failed to generate the Dyract identity key pair.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
