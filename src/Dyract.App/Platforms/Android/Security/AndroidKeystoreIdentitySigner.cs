using System.Formats.Asn1;
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
    private const int CoordinateLength = 32;

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
                    return ConvertDerEcdsaToP1363(derSignature);
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

    internal static byte[] ConvertDerEcdsaToP1363(ReadOnlySpan<byte> derSignature)
    {
        if (derSignature.IsEmpty)
        {
            throw new CryptographicException("ECDSA signature must not be empty.");
        }

        try
        {
            var reader = new AsnReader(derSignature.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadInteger();
            var s = sequence.ReadInteger();
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();

            if (r.Sign <= 0 || s.Sign <= 0)
            {
                throw new CryptographicException("ECDSA signature integers must be positive.");
            }

            var output = new byte[CoordinateLength * 2];
            WriteCoordinate(r.ToByteArray(isUnsigned: true, isBigEndian: true), output.AsSpan(0, CoordinateLength));
            WriteCoordinate(s.ToByteArray(isUnsigned: true, isBigEndian: true), output.AsSpan(CoordinateLength, CoordinateLength));
            return output;
        }
        catch (AsnContentException exception)
        {
            throw new CryptographicException("Android ECDSA signature is not canonical DER.", exception);
        }
    }

    private static void WriteCoordinate(ReadOnlySpan<byte> integer, Span<byte> destination)
    {
        if (integer.IsEmpty || integer.Length > destination.Length)
        {
            throw new CryptographicException("ECDSA signature coordinate is outside the P-256 range.");
        }

        destination.Clear();
        integer.CopyTo(destination[^integer.Length..]);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
