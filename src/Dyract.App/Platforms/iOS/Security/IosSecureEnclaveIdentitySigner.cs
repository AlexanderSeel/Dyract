using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Signatures;
using Foundation;
using Security;

namespace Dyract.App.Security;

/// <summary>
/// iOS-only P-256 identity signer backed by a persistent Secure Enclave SecKey.
/// The private key remains referenced by Keychain/Secure Enclave and is never exported.
///
/// This implementation is not yet selected by SecureIdentityVault. The iOS simulator can
/// compile it, but actual Secure Enclave behavior requires a physical iPhone and an explicit
/// identity-migration/recovery decision before shipping activation.
/// </summary>
public sealed class IosSecureEnclaveIdentitySigner : IPeerIdentitySigner, IDisposable
{
    private const int KeySizeBits = 256;
    private static readonly SecKeyAlgorithm SigningAlgorithm = SecKeyAlgorithm.EcdsaSignatureMessageX962Sha256;

    private readonly string _applicationTag;
    private readonly SecKey _privateKey;
    private readonly byte[] _publicKey;
    private bool _disposed;

    private IosSecureEnclaveIdentitySigner(
        string applicationTag,
        SecKey privateKey,
        byte[] publicKey)
    {
        _applicationTag = applicationTag;
        _privateKey = privateKey;
        _publicKey = publicKey;
        PeerId = PeerId.FromPublicKey(_publicKey);
    }

    public PeerId PeerId { get; }

    public string ApplicationTag => _applicationTag;

    public static IosSecureEnclaveIdentitySigner CreateOrLoad(string applicationTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationTag);
        using var tagData = NSData.FromArray(Encoding.UTF8.GetBytes(applicationTag));

        var privateKey = TryLoad(tagData) ?? Generate(tagData);
        try
        {
            if (!privateKey.IsAlgorithmSupported(SecKeyOperationType.Sign, SigningAlgorithm))
            {
                throw new CryptographicException("Secure Enclave identity key does not support Dyract SHA-256 ECDSA signing.");
            }

            using var publicKey = privateKey.GetPublicKey()
                ?? throw new CryptographicException("Secure Enclave identity public key is unavailable.");
            using var external = publicKey.GetExternalRepresentation(out var publicError)
                ?? throw CreateCryptographicException("Secure Enclave public key export failed.", publicError);

            var rawPoint = external.ToArray();
            var spki = P256PublicKeyEncoding.UncompressedPointToSubjectPublicKeyInfo(rawPoint);
            CryptographicOperations.ZeroMemory(rawPoint);

            return new IosSecureEnclaveIdentitySigner(applicationTag, privateKey, spki);
        }
        catch
        {
            privateKey.Dispose();
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

        using var data = NSData.FromArray(payload.ToArray());
        using var signature = _privateKey.CreateSignature(SigningAlgorithm, data, out var error)
            ?? throw CreateCryptographicException("Secure Enclave identity signing failed.", error);
        var derSignature = signature.ToArray();
        try
        {
            return EcdsaSignatureEncoding.DerToP256P1363(derSignature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derSignature);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_publicKey);
        _privateKey.Dispose();
        _disposed = true;
    }

    private static SecKey? TryLoad(NSData applicationTag)
    {
        using var query = new SecRecord(SecKind.Key)
        {
            ApplicationTag = applicationTag,
            KeyType = SecKeyType.ECSecPrimeRandom,
            KeyClass = SecKeyClass.Private,
            TokenID = SecTokenID.SecureEnclave
        };

        var value = SecKeyChain.QueryAsConcreteType(query, out var status);
        if (status == SecStatusCode.ItemNotFound)
        {
            return null;
        }

        if (status != SecStatusCode.Success)
        {
            throw new CryptographicException($"Secure Enclave identity lookup failed with Keychain status {status}.");
        }

        return value as SecKey
            ?? throw new CryptographicException("Secure Enclave identity query returned an unexpected Keychain object type.");
    }

    private static SecKey Generate(NSData applicationTag)
    {
        var parameters = new SecKeyGenerationParameters
        {
            KeyType = SecKeyType.ECSecPrimeRandom,
            KeySizeInBits = KeySizeBits,
            TokenID = SecTokenID.SecureEnclave,
            IsPermanent = true,
            ApplicationTag = applicationTag,
            CanSign = true,
            CanDecrypt = false,
            CanDerive = false,
            CanEncrypt = false
        };

        return SecKey.CreateRandomKey(parameters, out var error)
            ?? throw CreateCryptographicException("Secure Enclave identity key generation failed.", error);
    }

    private static CryptographicException CreateCryptographicException(string message, NSError? error)
        => error is null
            ? new CryptographicException(message)
            : new CryptographicException($"{message} ({error.Domain}/{error.Code})");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
