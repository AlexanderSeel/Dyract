using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dyract.Crypto.Session;

public sealed class AuthenticatedSessionCipher : IDisposable
{
    private static readonly byte[] Magic = "DYSE"u8.ToArray();
    private const byte Version = 1;
    private const int HeaderLength = 4 + 1 + 8 + 4;
    private const int TagLength = 16;
    private const int NonceLength = 12;
    public const int MaximumPlaintextBytes = (256 * 1024) - HeaderLength - TagLength;

    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private readonly byte[] _transcriptHash;
    private readonly object _sendGate = new();
    private readonly object _receiveGate = new();
    private ulong _sendSequence;
    private ulong _receiveSequence;
    private int _disposed;

    public AuthenticatedSessionCipher(AuthenticatedSessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _sendCipher = new AesGcm(keys.SendKey, TagLength);
        _receiveCipher = new AesGcm(keys.ReceiveKey, TagLength);
        _transcriptHash = keys.ExportTranscriptHash();
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        if (plaintext.IsEmpty || plaintext.Length > MaximumPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                $"Authenticated session plaintext must contain 1-{MaximumPlaintextBytes} bytes.");
        }

        lock (_sendGate)
        {
            ThrowIfDisposed();
            if (_sendSequence == ulong.MaxValue)
            {
                throw new CryptographicException("Authenticated session send sequence is exhausted.");
            }

            var sequence = _sendSequence;
            var frame = new byte[HeaderLength + plaintext.Length + TagLength];
            WriteHeader(frame, sequence, plaintext.Length);

            Span<byte> nonce = stackalloc byte[NonceLength];
            BuildNonce(sequence, nonce);
            Span<byte> associatedData = stackalloc byte[HeaderLength + 32];
            frame.AsSpan(0, HeaderLength).CopyTo(associatedData);
            _transcriptHash.CopyTo(associatedData[HeaderLength..]);

            try
            {
                var ciphertext = frame.AsSpan(HeaderLength, plaintext.Length);
                var tag = frame.AsSpan(HeaderLength + plaintext.Length, TagLength);
                _sendCipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                _sendSequence++;
                return frame;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> frame)
    {
        ThrowIfDisposed();
        if (frame.Length < HeaderLength + 1 + TagLength ||
            frame.Length > HeaderLength + MaximumPlaintextBytes + TagLength)
        {
            throw new CryptographicException("Authenticated session frame length is invalid.");
        }

        lock (_receiveGate)
        {
            ThrowIfDisposed();
            ValidateHeader(frame, out var sequence, out var ciphertextLength);
            if (sequence != _receiveSequence)
            {
                throw new CryptographicException(
                    $"Authenticated session frame sequence mismatch. Expected {_receiveSequence}, received {sequence}.");
            }

            var plaintext = new byte[ciphertextLength];
            Span<byte> nonce = stackalloc byte[NonceLength];
            BuildNonce(sequence, nonce);
            Span<byte> associatedData = stackalloc byte[HeaderLength + 32];
            frame[..HeaderLength].CopyTo(associatedData);
            _transcriptHash.CopyTo(associatedData[HeaderLength..]);

            try
            {
                var ciphertext = frame.Slice(HeaderLength, ciphertextLength);
                var tag = frame.Slice(HeaderLength + ciphertextLength, TagLength);
                _receiveCipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(associatedData);
            }

            if (_receiveSequence == ulong.MaxValue)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new CryptographicException("Authenticated session receive sequence is exhausted.");
            }

            _receiveSequence++;
            return plaintext;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sendGate)
        {
            lock (_receiveGate)
            {
                _sendCipher.Dispose();
                _receiveCipher.Dispose();
                CryptographicOperations.ZeroMemory(_transcriptHash);
            }
        }
    }

    private static void WriteHeader(Span<byte> destination, ulong sequence, int plaintextLength)
    {
        Magic.CopyTo(destination);
        destination[4] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(5, 8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(13, 4), checked((uint)plaintextLength));
    }

    private static void ValidateHeader(
        ReadOnlySpan<byte> frame,
        out ulong sequence,
        out int ciphertextLength)
    {
        if (!frame[..Magic.Length].SequenceEqual(Magic) || frame[4] != Version)
        {
            throw new CryptographicException("Authenticated session frame header is invalid.");
        }

        sequence = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(5, 8));
        var encodedLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(13, 4));
        var actualCiphertextLength = frame.Length - HeaderLength - TagLength;
        if (encodedLength is 0 or > MaximumPlaintextBytes ||
            encodedLength != checked((uint)actualCiphertextLength))
        {
            throw new CryptographicException("Authenticated session ciphertext length is invalid.");
        }

        ciphertextLength = checked((int)encodedLength);
    }

    private void BuildNonce(ulong sequence, Span<byte> nonce)
    {
        _transcriptHash.AsSpan(0, 4).CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
