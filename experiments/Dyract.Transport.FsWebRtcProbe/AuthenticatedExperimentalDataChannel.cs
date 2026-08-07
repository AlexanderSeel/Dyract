#if ANDROID
using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class AuthenticatedExperimentalDataChannel : IDisposable
{
    private readonly ExperimentalDataChannelAdapter _rawChannel;
    private readonly AuthenticatedSessionCipher _cipher;
    private int _disposed;

    private AuthenticatedExperimentalDataChannel(
        ExperimentalDataChannelAdapter rawChannel,
        AuthenticatedSessionCipher cipher)
    {
        _rawChannel = rawChannel;
        _cipher = cipher;
    }

    public static async Task<AuthenticatedExperimentalDataChannel> InitiateAsync(
        ExperimentalDataChannelAdapter rawChannel,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawChannel);
        ArgumentNullException.ThrowIfNull(localIdentity);
        await rawChannel.Opened.WaitAsync(cancellationToken);

        using var handshake = AuthenticatedSessionInitiator.Create(
            localIdentity,
            remotePeerId,
            remoteIdentityPublicKey.Span,
            sessionId);

        await rawChannel.SendAsync(handshake.HelloPacket, cancellationToken);
        var responsePacket = await ReceiveOneAsync(rawChannel, cancellationToken);
        try
        {
            using var keys = handshake.Complete(responsePacket);
            return new AuthenticatedExperimentalDataChannel(
                rawChannel,
                new AuthenticatedSessionCipher(keys));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responsePacket);
        }
    }

    public static async Task<AuthenticatedExperimentalDataChannel> RespondAsync(
        ExperimentalDataChannelAdapter rawChannel,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawChannel);
        ArgumentNullException.ThrowIfNull(localIdentity);
        await rawChannel.Opened.WaitAsync(cancellationToken);

        var helloPacket = await ReceiveOneAsync(rawChannel, cancellationToken);
        try
        {
            var response = AuthenticatedSessionResponder.Accept(
                localIdentity,
                remotePeerId,
                remoteIdentityPublicKey.Span,
                helloPacket,
                sessionId);
            using var keys = response.Keys;
            try
            {
                await rawChannel.SendAsync(response.ResponsePacket, cancellationToken);
                return new AuthenticatedExperimentalDataChannel(
                    rawChannel,
                    new AuthenticatedSessionCipher(keys));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response.ResponsePacket);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloPacket);
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var encrypted = _cipher.Encrypt(plaintext.Span);
        try
        {
            await _rawChannel.SendAsync(encrypted, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async IAsyncEnumerable<byte[]> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await foreach (var encrypted in _rawChannel.ReceiveAsync(cancellationToken))
        {
            ThrowIfDisposed();
            byte[] plaintext;
            try
            {
                plaintext = _cipher.Decrypt(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            yield return plaintext;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cipher.Dispose();
    }

    private static async Task<byte[]> ReceiveOneAsync(
        ExperimentalDataChannelAdapter channel,
        CancellationToken cancellationToken)
    {
        await using var enumerator = channel.ReceiveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync())
        {
            throw new EndOfStreamException("DataChannel closed during the authenticated session handshake.");
        }

        return enumerator.Current;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
