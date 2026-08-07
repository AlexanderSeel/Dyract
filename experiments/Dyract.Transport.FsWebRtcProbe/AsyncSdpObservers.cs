#if ANDROID
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class CreateSdpObserver : Java.Lang.Object, ISdpObserver
{
    private readonly TaskCompletionSource<SessionDescription> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SessionDescription> Task => _completion.Task;

    public void OnCreateSuccess(SessionDescription? description)
    {
        if (description is null)
        {
            _completion.TrySetException(new InvalidOperationException("WebRTC returned an empty session description."));
            return;
        }

        _completion.TrySetResult(description);
    }

    public void OnCreateFailure(string? error)
        => _completion.TrySetException(new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "WebRTC failed to create SDP." : error));

    public void OnSetSuccess()
        => _completion.TrySetException(new InvalidOperationException("Unexpected SDP set callback on create observer."));

    public void OnSetFailure(string? error)
        => _completion.TrySetException(new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "Unexpected SDP set failure callback." : error));
}

public sealed class SetSdpObserver : Java.Lang.Object, ISdpObserver
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Task => _completion.Task;

    public void OnSetSuccess() => _completion.TrySetResult();

    public void OnSetFailure(string? error)
        => _completion.TrySetException(new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "WebRTC failed to apply SDP." : error));

    public void OnCreateSuccess(SessionDescription? description)
        => _completion.TrySetException(new InvalidOperationException("Unexpected SDP create callback on set observer."));

    public void OnCreateFailure(string? error)
        => _completion.TrySetException(new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "Unexpected SDP create failure callback." : error));
}
#endif
