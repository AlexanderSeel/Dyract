#if ANDROID
using System.Threading.Channels;
using Java.Nio;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class ExperimentalDataChannelAdapter : Java.Lang.Object, DataChannel.IObserver, IAsyncDisposable
{
    private const int MaximumExperimentalFrameBytes = 256 * 1024;

    private readonly DataChannel _dataChannel;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        ObserveNativeState();
    }

    public event Action? StateChanged;
    public event Action<Exception>? ProtocolError;

    public Task Opened => _opened.Task;

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (payload.IsEmpty || payload.Length > MaximumExperimentalFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Experimental DataChannel frame must contain 1-{MaximumExperimentalFrameBytes} bytes.");
        }

        await _opened.Task.WaitAsync(cancellationToken);
        ThrowIfDisposed();

        if (_dataChannel.InvokeState() != DataChannel.State.Open)
        {
            throw new InvalidOperationException("Native WebRTC DataChannel is not open.");
        }

        var bytes = payload.ToArray();
        using var byteBuffer = ByteBuffer.Wrap(bytes);
        using var buffer = new DataChannel.Buffer(byteBuffer, true);

        if (!_dataChannel.Send(buffer))
        {
            throw new InvalidOperationException("Native WebRTC DataChannel rejected the frame.");
        }
    }

    public IAsyncEnumerable<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
        => _frames.Reader.ReadAllAsync(cancellationToken);

    public void OnBufferedAmountChange(long previousAmount) { }

    public void OnStateChange()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ObserveNativeState();
        StateChanged?.Invoke();
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

            var data = buffer.Data ?? throw new InvalidOperationException("Received DataChannel frame did not contain a native ByteBuffer.");
            var remaining = data.Remaining();
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

        _opened.TrySetException(new ObjectDisposedException(nameof(ExperimentalDataChannelAdapter)));
        _frames.Writer.TryComplete();
        _dataChannel.UnregisterObserver();
        _dataChannel.Close();
        _dataChannel.Dispose();
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ObserveNativeState()
    {
        try
        {
            var state = _dataChannel.InvokeState();
            if (state == DataChannel.State.Open)
            {
                _opened.TrySetResult();
                return;
            }

            if (state == DataChannel.State.Closing || state == DataChannel.State.Closed)
            {
                _opened.TrySetException(
                    new InvalidOperationException("Native WebRTC DataChannel closed before reaching OPEN."));
            }
        }
        catch (Exception exception)
        {
            _opened.TrySetException(exception);
            ProtocolError?.Invoke(exception);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
