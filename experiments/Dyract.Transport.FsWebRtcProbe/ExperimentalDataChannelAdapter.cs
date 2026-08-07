#if ANDROID
using System.Threading.Channels;
using Java.Nio;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class ExperimentalDataChannelAdapter : Java.Lang.Object, DataChannel.IObserver, IAsyncDisposable
{
    private const int MaximumExperimentalFrameBytes = 256 * 1024;

    private readonly DataChannel _dataChannel;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private int _disposed;

    public ExperimentalDataChannelAdapter(DataChannel dataChannel)
    {
        _dataChannel = dataChannel ?? throw new ArgumentNullException(nameof(dataChannel));
        _dataChannel.RegisterObserver(this);
    }

    public event Action? StateChanged;
    public event Action<Exception>? ProtocolError;

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (payload.IsEmpty || payload.Length > MaximumExperimentalFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Experimental DataChannel frame must contain 1-{MaximumExperimentalFrameBytes} bytes.");
        }

        var bytes = payload.ToArray();
        using var byteBuffer = ByteBuffer.Wrap(bytes);
        using var buffer = new DataChannel.Buffer(byteBuffer, true);

        if (!_dataChannel.Send(buffer))
        {
            throw new InvalidOperationException("Native WebRTC DataChannel rejected the frame.");
        }

        await ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
        => _frames.Reader.ReadAllAsync(cancellationToken);

    public void OnBufferedAmountChange(long previousAmount) { }

    public void OnStateChange()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            StateChanged?.Invoke();
        }
    }

    public void OnMessage(DataChannel.Buffer? buffer)
    {
        if (Volatile.Read(ref _disposed) != 0 || buffer is null)
        {
            return;
        }

        try
        {
            if (!buffer.Binary)
            {
                ProtocolError?.Invoke(new InvalidOperationException("Dyract experimental transport accepts binary DataChannel frames only."));
                return;
            }

            var data = buffer.Data;
            var remaining = data.Remaining;
            if (remaining is <= 0 or > MaximumExperimentalFrameBytes)
            {
                ProtocolError?.Invoke(new InvalidOperationException("Received DataChannel frame has an invalid size."));
                return;
            }

            var bytes = new byte[remaining];
            data.Get(bytes);

            if (!_frames.Writer.TryWrite(bytes))
            {
                ProtocolError?.Invoke(new InvalidOperationException("Received DataChannel frame queue is full."));
            }
        }
        catch (Exception exception)
        {
            ProtocolError?.Invoke(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _frames.Writer.TryComplete();
        _dataChannel.UnregisterObserver();
        _dataChannel.Close();
        _dataChannel.Dispose();
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
