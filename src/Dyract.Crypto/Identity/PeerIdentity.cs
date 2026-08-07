using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Crypto.Identity;

public sealed class PeerIdentity : IDisposable
{
    private readonly ECDsa _key;
    private readonly byte[] _publicKey;
    private bool _disposed;

    private PeerIdentity(ECDsa key)
    {
        _key = key;
        _publicKey = key.ExportSubjectPublicKeyInfo();
        PeerId = PeerId.FromPublicKey(_publicKey);
    }

    public PeerId PeerId { get; }

    public static PeerIdentity Generate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PeerIdentity(key);
    }

    public static PeerIdentity ImportPkcs8PrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.IsEmpty)
        {
            throw new ArgumentException("Private key must not be empty.", nameof(privateKey));
        }

        var key = ECDsa.Create();

        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length)
            {
                throw new CryptographicException("Unexpected trailing data in private key.");
            }

            EnsureP256(key);
            return new PeerIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportPublicKey()
    {
        ThrowIfDisposed();
        return _publicKey.ToArray();
    }

    // Export exists for bootstrap/testing. Production mobile code must protect this
    // material with platform secure storage and must not persist it as plain data.
    public byte[] ExportPkcs8PrivateKey()
    {
        ThrowIfDisposed();
        return _key.ExportPkcs8PrivateKey();
    }

    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();

        if (payload.IsEmpty)
        {
            throw new ArgumentException("Payload must not be empty.", nameof(payload));
        }

        return _key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        ThrowIfDisposed();
        return _key.VerifyData(
            payload,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _key.Dispose();
        CryptographicOperations.ZeroMemory(_publicKey);
        _disposed = true;
    }

    private static void EnsureP256(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var actual = parameters.Curve.Oid.Value;
        var expected = ECCurve.NamedCurves.nistP256.Oid.Value;

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new CryptographicException("Dyract identity keys must use NIST P-256.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
