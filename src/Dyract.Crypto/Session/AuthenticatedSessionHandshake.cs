using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Signatures;

namespace Dyract.Crypto.Session;

public sealed record ResponderHandshakeResult(
    byte[] ResponsePacket,
    AuthenticatedSessionKeys Keys);

public sealed class AuthenticatedSessionInitiator : IDisposable
{
    private readonly ECDiffieHellman _ephemeralKey;
    private readonly PeerId _localPeerId;
    private readonly PeerId _remotePeerId;
    private readonly byte[] _remoteIdentityPublicKey;
    private readonly byte[] _initiatorNonce;
    private int _disposed;

    private AuthenticatedSessionInitiator(
        ECDiffieHellman ephemeralKey,
        PeerId localPeerId,
        PeerId remotePeerId,
        byte[] remoteIdentityPublicKey,
        byte[] initiatorNonce,
        string sessionId,
        byte[] helloPacket)
    {
        _ephemeralKey = ephemeralKey;
        _localPeerId = localPeerId;
        _remotePeerId = remotePeerId;
        _remoteIdentityPublicKey = remoteIdentityPublicKey;
        _initiatorNonce = initiatorNonce;
        SessionId = sessionId;
        HelloPacket = helloPacket;
    }

    public string SessionId { get; }
    public byte[] HelloPacket { get; }

    public static AuthenticatedSessionInitiator Create(
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlySpan<byte> remoteIdentityPublicKey,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        SessionHandshakeWire.ValidateSessionId(sessionId);
        ValidatePinnedIdentity(remotePeerId, remoteIdentityPublicKey);

        var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(SessionHandshakeWire.NonceLength);
            var publicKey = ephemeral.ExportSubjectPublicKeyInfo();
            var unsigned = SessionHandshakeWire.EncodeUnsigned(
                SessionHandshakeWire.HelloType,
                sessionId,
                localIdentity.PeerId,
                remotePeerId,
                nonce,
                publicKey,
                helloHash: null);
            var signature = localIdentity.Sign(unsigned);
            var packet = SessionHandshakeWire.AppendSignature(unsigned, signature);

            return new AuthenticatedSessionInitiator(
                ephemeral,
                localIdentity.PeerId,
                remotePeerId,
                remoteIdentityPublicKey.ToArray(),
                nonce,
                sessionId.ToLowerInvariant(),
                packet);
        }
        catch
        {
            ephemeral.Dispose();
            throw;
        }
    }

    public AuthenticatedSessionKeys Complete(ReadOnlySpan<byte> responsePacket)
    {
        ThrowIfDisposed();

        try
        {
            var response = SessionHandshakeWire.Decode(responsePacket, SessionHandshakeWire.ResponseType);
            EnsureExpected(response);

            var expectedHelloHash = SHA256.HashData(HelloPacket);
            if (response.HelloHash is null ||
                !CryptographicOperations.FixedTimeEquals(response.HelloHash, expectedHelloHash))
            {
                throw new CryptographicException("Session response is not bound to the initiator hello transcript.");
            }

            if (!SignatureVerifier.Verify(
                    _remoteIdentityPublicKey,
                    response.UnsignedPacket,
                    response.Signature))
            {
                throw new CryptographicException("Session response identity signature is invalid.");
            }

            return SessionHandshakeKeyDerivation.Derive(
                _ephemeralKey,
                response.EphemeralPublicKey,
                _initiatorNonce,
                response.Nonce,
                HelloPacket,
                responsePacket,
                initiator: true);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ephemeralKey.Dispose();
        CryptographicOperations.ZeroMemory(_remoteIdentityPublicKey);
        CryptographicOperations.ZeroMemory(_initiatorNonce);
    }

    private void EnsureExpected(SessionHandshakeWire.DecodedPacket response)
    {
        if (!string.Equals(response.SessionId, SessionId, StringComparison.Ordinal) ||
            response.SenderPeerId != _remotePeerId ||
            response.ReceiverPeerId != _localPeerId)
        {
            throw new CryptographicException("Session response identity or session scope does not match the initiator state.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal static void ValidatePinnedIdentity(PeerId expectedPeerId, ReadOnlySpan<byte> publicKey)
    {
        if (!SignatureVerifier.IsValidIdentityPublicKey(publicKey))
        {
            throw new CryptographicException("Pinned peer identity key is invalid or not P-256.");
        }

        var actualPeerId = PeerId.FromPublicKey(publicKey);
        if (actualPeerId != expectedPeerId)
        {
            throw new CryptographicException("Pinned peer identity key does not match the expected Peer ID.");
        }
    }
}

public static class AuthenticatedSessionResponder
{
    public static ResponderHandshakeResult Accept(
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlySpan<byte> remoteIdentityPublicKey,
        ReadOnlySpan<byte> helloPacket,
        string expectedSessionId)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        SessionHandshakeWire.ValidateSessionId(expectedSessionId);
        AuthenticatedSessionInitiator.ValidatePinnedIdentity(remotePeerId, remoteIdentityPublicKey);

        var hello = SessionHandshakeWire.Decode(helloPacket, SessionHandshakeWire.HelloType);
        if (!string.Equals(hello.SessionId, expectedSessionId, StringComparison.OrdinalIgnoreCase) ||
            hello.SenderPeerId != remotePeerId ||
            hello.ReceiverPeerId != localIdentity.PeerId)
        {
            throw new CryptographicException("Session hello identity or session scope does not match the responder state.");
        }

        if (!SignatureVerifier.Verify(
                remoteIdentityPublicKey,
                hello.UnsignedPacket,
                hello.Signature))
        {
            throw new CryptographicException("Session hello identity signature is invalid.");
        }

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderNonce = RandomNumberGenerator.GetBytes(SessionHandshakeWire.NonceLength);
        try
        {
            var responderPublicKey = ephemeral.ExportSubjectPublicKeyInfo();
            var helloHash = SHA256.HashData(helloPacket);
            var unsignedResponse = SessionHandshakeWire.EncodeUnsigned(
                SessionHandshakeWire.ResponseType,
                hello.SessionId,
                localIdentity.PeerId,
                remotePeerId,
                responderNonce,
                responderPublicKey,
                helloHash);
            var signature = localIdentity.Sign(unsignedResponse);
            var responsePacket = SessionHandshakeWire.AppendSignature(unsignedResponse, signature);

            var keys = SessionHandshakeKeyDerivation.Derive(
                ephemeral,
                hello.EphemeralPublicKey,
                hello.Nonce,
                responderNonce,
                helloPacket,
                responsePacket,
                initiator: false);

            return new ResponderHandshakeResult(responsePacket, keys);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responderNonce);
        }
    }
}

internal static class SessionHandshakeKeyDerivation
{
    private static readonly byte[] InfoLabel = Encoding.ASCII.GetBytes("Dyract/session-keys/v1");

    public static AuthenticatedSessionKeys Derive(
        ECDiffieHellman localEphemeral,
        ReadOnlySpan<byte> remoteEphemeralPublicKey,
        ReadOnlySpan<byte> initiatorNonce,
        ReadOnlySpan<byte> responderNonce,
        ReadOnlySpan<byte> helloPacket,
        ReadOnlySpan<byte> responsePacket,
        bool initiator)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(remoteEphemeralPublicKey, out var bytesRead);
        if (bytesRead != remoteEphemeralPublicKey.Length)
        {
            throw new CryptographicException("Remote ephemeral key contains trailing data.");
        }

        EnsureP256(remote);

        var sharedSecret = localEphemeral.DeriveRawSecretAgreement(remote.PublicKey);
        try
        {
            Span<byte> nonceInput = stackalloc byte[SessionHandshakeWire.NonceLength * 2];
            initiatorNonce.CopyTo(nonceInput);
            responderNonce.CopyTo(nonceInput[SessionHandshakeWire.NonceLength..]);
            Span<byte> salt = stackalloc byte[32];
            SHA256.HashData(nonceInput, salt);

            var transcriptInput = new byte[helloPacket.Length + responsePacket.Length];
            helloPacket.CopyTo(transcriptInput);
            responsePacket.CopyTo(transcriptInput.AsSpan(helloPacket.Length));
            Span<byte> transcriptHash = stackalloc byte[32];
            SHA256.HashData(transcriptInput, transcriptHash);
            CryptographicOperations.ZeroMemory(transcriptInput);

            Span<byte> info = stackalloc byte[InfoLabel.Length + transcriptHash.Length];
            InfoLabel.CopyTo(info);
            transcriptHash.CopyTo(info[InfoLabel.Length..]);

            Span<byte> keyMaterial = stackalloc byte[64];
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                keyMaterial,
                salt,
                info);

            var result = initiator
                ? new AuthenticatedSessionKeys(keyMaterial[..32], keyMaterial[32..], transcriptHash)
                : new AuthenticatedSessionKeys(keyMaterial[32..], keyMaterial[..32], transcriptHash);

            CryptographicOperations.ZeroMemory(keyMaterial);
            CryptographicOperations.ZeroMemory(nonceInput);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(info);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static void EnsureP256(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        if (!string.Equals(
                parameters.Curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal))
        {
            throw new CryptographicException("Dyract ephemeral session keys must use NIST P-256.");
        }
    }
}

internal static class SessionHandshakeWire
{
    private static readonly byte[] Magic = "DYSH"u8.ToArray();
    private const byte Version = 1;
    public const byte HelloType = 1;
    public const byte ResponseType = 2;
    public const int NonceLength = 32;
    private const int SessionIdBytes = 16;
    private const int PeerIdBytes = 56;
    private const int HelloHashLength = 32;
    private const int SignatureLength = 64;
    private const int MaxEphemeralPublicKeyLength = 256;
    private const int MaximumPacketLength = 1024;

    internal sealed record DecodedPacket(
        byte Type,
        string SessionId,
        PeerId SenderPeerId,
        PeerId ReceiverPeerId,
        byte[] Nonce,
        byte[] EphemeralPublicKey,
        byte[]? HelloHash,
        byte[] UnsignedPacket,
        byte[] Signature);

    public static byte[] EncodeUnsigned(
        byte type,
        string sessionId,
        PeerId senderPeerId,
        PeerId receiverPeerId,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte>? helloHash)
    {
        ValidateSessionId(sessionId);
        if (type is not (HelloType or ResponseType))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"Session nonce must contain {NonceLength} bytes.", nameof(nonce));
        }

        if (ephemeralPublicKey.IsEmpty || ephemeralPublicKey.Length > MaxEphemeralPublicKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(ephemeralPublicKey));
        }

        if (type == ResponseType && (!helloHash.HasValue || helloHash.Value.Length != HelloHashLength))
        {
            throw new ArgumentException("Session response must contain a 32-byte hello hash.", nameof(helloHash));
        }

        if (type == HelloType && helloHash.HasValue)
        {
            throw new ArgumentException("Session hello must not contain a prior transcript hash.", nameof(helloHash));
        }

        var length = Magic.Length + 1 + 1 + SessionIdBytes + (PeerIdBytes * 2) + NonceLength + 2 +
                     ephemeralPublicKey.Length + (type == ResponseType ? HelloHashLength : 0);
        var output = new byte[length];
        var offset = 0;

        Write(Magic, output, ref offset);
        output[offset++] = Version;
        output[offset++] = type;
        Write(Convert.FromHexString(sessionId), output, ref offset);
        WritePeerId(senderPeerId, output, ref offset);
        WritePeerId(receiverPeerId, output, ref offset);
        Write(nonce, output, ref offset);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset, 2), checked((ushort)ephemeralPublicKey.Length));
        offset += 2;
        Write(ephemeralPublicKey, output, ref offset);

        if (helloHash.HasValue)
        {
            Write(helloHash.Value, output, ref offset);
        }

        return output;
    }

    public static byte[] AppendSignature(ReadOnlySpan<byte> unsignedPacket, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
        {
            throw new CryptographicException("Dyract P-256 identity signatures must contain 64 bytes in IEEE P1363 format.");
        }

        if (unsignedPacket.IsEmpty || unsignedPacket.Length + SignatureLength > MaximumPacketLength)
        {
            throw new ArgumentOutOfRangeException(nameof(unsignedPacket));
        }

        var packet = new byte[unsignedPacket.Length + SignatureLength];
        unsignedPacket.CopyTo(packet);
        signature.CopyTo(packet.AsSpan(unsignedPacket.Length));
        return packet;
    }

    public static DecodedPacket Decode(ReadOnlySpan<byte> packet, byte expectedType)
    {
        if (packet.Length < 300 || packet.Length > MaximumPacketLength)
        {
            throw new CryptographicException("Session handshake packet length is invalid.");
        }

        var unsignedLength = packet.Length - SignatureLength;
        var unsigned = packet[..unsignedLength];
        var signature = packet[unsignedLength..].ToArray();
        var offset = 0;

        RequireBytes(unsigned, ref offset, Magic);
        var version = ReadByte(unsigned, ref offset);
        if (version != Version)
        {
            throw new CryptographicException("Session handshake protocol version is unsupported.");
        }

        var type = ReadByte(unsigned, ref offset);
        if (type != expectedType)
        {
            throw new CryptographicException("Unexpected session handshake packet type.");
        }

        var sessionBytes = Read(unsigned, ref offset, SessionIdBytes);
        var sessionId = Convert.ToHexString(sessionBytes).ToLowerInvariant();
        var sender = ReadPeerId(unsigned, ref offset);
        var receiver = ReadPeerId(unsigned, ref offset);
        var nonce = Read(unsigned, ref offset, NonceLength).ToArray();

        var keyLength = ReadUInt16(unsigned, ref offset);
        if (keyLength is 0 or > MaxEphemeralPublicKeyLength)
        {
            throw new CryptographicException("Session ephemeral public-key length is invalid.");
        }

        var ephemeral = Read(unsigned, ref offset, keyLength).ToArray();
        byte[]? helloHash = null;
        if (type == ResponseType)
        {
            helloHash = Read(unsigned, ref offset, HelloHashLength).ToArray();
        }

        if (offset != unsigned.Length)
        {
            throw new CryptographicException("Session handshake packet contains unexpected trailing fields.");
        }

        return new DecodedPacket(
            type,
            sessionId,
            sender,
            receiver,
            nonce,
            ephemeral,
            helloHash,
            unsigned.ToArray(),
            signature);
    }

    public static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length != SessionIdBytes * 2 || !sessionId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Session ID must be a 128-bit hexadecimal identifier.", nameof(sessionId));
        }
    }

    private static void WritePeerId(PeerId peerId, Span<byte> destination, ref int offset)
    {
        var value = peerId.Value;
        if (value is null || Encoding.ASCII.GetByteCount(value) != PeerIdBytes)
        {
            throw new CryptographicException("Peer ID has an invalid encoded length.");
        }

        var written = Encoding.ASCII.GetBytes(value, destination[offset..]);
        if (written != PeerIdBytes)
        {
            throw new CryptographicException("Peer ID could not be encoded canonically.");
        }

        offset += written;
    }

    private static PeerId ReadPeerId(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = Encoding.ASCII.GetString(Read(source, ref offset, PeerIdBytes));
        if (!PeerId.TryParse(value, out var peerId))
        {
            throw new CryptographicException("Session handshake contains an invalid Peer ID.");
        }

        return peerId;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
    {
        var bytes = Read(source, ref offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int offset)
    {
        if ((uint)offset >= (uint)source.Length)
        {
            throw new CryptographicException("Session handshake packet is truncated.");
        }

        return source[offset++];
    }

    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        if (length < 0 || offset < 0 || offset > source.Length - length)
        {
            throw new CryptographicException("Session handshake packet is truncated.");
        }

        var result = source.Slice(offset, length);
        offset += length;
        return result;
    }

    private static void RequireBytes(ReadOnlySpan<byte> source, ref int offset, ReadOnlySpan<byte> expected)
    {
        var actual = Read(source, ref offset, expected.Length);
        if (!actual.SequenceEqual(expected))
        {
            throw new CryptographicException("Session handshake magic is invalid.");
        }
    }

    private static void Write(ReadOnlySpan<byte> source, Span<byte> destination, ref int offset)
    {
        source.CopyTo(destination[offset..]);
        offset += source.Length;
    }
}
