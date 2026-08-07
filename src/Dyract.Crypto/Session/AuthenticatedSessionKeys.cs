using System.Security.Cryptography;

namespace Dyract.Crypto.Session;

public sealed class AuthenticatedSessionKeys : IDisposable
{
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly byte[] _transcriptHash;
    private int _disposed;

    internal AuthenticatedSessionKeys(
        ReadOnlySpan<byte> sendKey,
        ReadOnlySpan<byte> receiveKey,
        ReadOnlySpan<byte> transcriptHash)
    {
        if (sendKey.Length != 32 || receiveKey.Length != 32 || transcriptHash.Length != 32)
        {
            throw new ArgumentException("Dyract session keys and transcript hash must be 32 bytes.");
        }

        _sendKey = sendKey.ToArray();
        _receiveKey = receiveKey.ToArray();
        _transcriptHash = transcriptHash.ToArray();
    }

    public byte[] ExportSendKey()
    {
        ThrowIfDisposed();
        return _sendKey.ToArray();
    }

    public byte[] ExportReceiveKey()
    {
        ThrowIfDisposed();
        return _receiveKey.ToArray();
    }

    public byte[] ExportTranscriptHash()
    {
        ThrowIfDisposed();
        return _transcriptHash.ToArray();
    }

    internal ReadOnlySpan<byte> SendKey
    {
        get
        {
            ThrowIfDisposed();
            return _sendKey;
        }
    }

    internal ReadOnlySpan<byte> ReceiveKey
    {
        get
        {
            ThrowIfDisposed();
            return _receiveKey;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
        CryptographicOperations.ZeroMemory(_transcriptHash);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
