using Dyract.Client;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Transport.FsWebRtcProbe;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Dyract.Transport.AndroidHarness;

public sealed class MainPage : ContentPage
{
    private const string DirectoryPreference = "dyract.transportharness.directory.v1";
    private const string StunPreference = "dyract.transportharness.stun.v1";
    private const string RemoteInvitationPreference = "dyract.transportharness.remote-invitation.v1";
    private const string RemotePairingSecureKey = "dyract.transportharness.remote-pairing.v1";

    private readonly HarnessIdentityVault _identityVault;
    private readonly Entry _directoryEntry = new() { Placeholder = "https://directory.example.com" };
    private readonly Entry _stunEntry = new() { Placeholder = "stun:host:3478 (optional; comma separated)" };
    private readonly Label _peerIdLabel = new() { LineBreakMode = LineBreakMode.CharacterWrap };
    private readonly Label _remotePeerLabel = new() { Text = "Remote peer: not loaded", LineBreakMode = LineBreakMode.CharacterWrap };
    private readonly Label _statusLabel = new() { Text = "Not initialized", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _logLabel = new() { LineBreakMode = LineBreakMode.WordWrap };
    private readonly Editor _remoteInvitationEditor = new()
    {
        Placeholder = "Paste remote dyract://contact/v1/... invitation",
        AutoSize = EditorAutoSizeOption.TextChanges,
        HeightRequest = 100
    };
    private readonly Editor _remotePairingEditor = new()
    {
        Placeholder = "Paste remote dyract://pair/v1/... response",
        AutoSize = EditorAutoSizeOption.TextChanges,
        HeightRequest = 100
    };
    private readonly Button _pingButton = new() { Text = "Ping", IsEnabled = false };

    private ContactInvitation? _remoteInvitation;
    private ContactCapability? _remoteCapability;
    private HarnessPeerSignalingGateway? _gateway;
    private FsWebRtcDiagnosticConnection? _connection;
    private CancellationTokenSource? _echoCancellation;
    private Task? _echoTask;
    private bool _initialized;

    public MainPage(HarnessIdentityVault identityVault)
    {
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        Title = "Transport Harness";

        _directoryEntry.Text = Preferences.Default.Get(DirectoryPreference, string.Empty);
        _stunEntry.Text = Preferences.Default.Get(StunPreference, string.Empty);
        _remoteInvitationEditor.Text = Preferences.Default.Get(RemoteInvitationPreference, string.Empty);

        var registerButton = new Button { Text = "Configure + Register" };
        registerButton.Clicked += async (_, _) => await ExecuteAsync(RegisterAsync);

        var copyInviteButton = new Button { Text = "Copy my contact invitation" };
        copyInviteButton.Clicked += async (_, _) => await ExecuteAsync(CopyLocalInvitationAsync);

        var loadRemoteButton = new Button { Text = "Load remote invitation" };
        loadRemoteButton.Clicked += async (_, _) => await ExecuteAsync(LoadRemoteInvitationAsync);

        var copyPairingButton = new Button { Text = "Copy pairing response for remote" };
        copyPairingButton.Clicked += async (_, _) => await ExecuteAsync(CopyPairingResponseAsync);

        var validatePairingButton = new Button { Text = "Validate remote pairing response" };
        validatePairingButton.Clicked += async (_, _) => await ExecuteAsync(ValidateRemotePairingAsync);

        var initiatorButton = new Button { Text = "Start initiator" };
        initiatorButton.Clicked += async (_, _) => await ExecuteAsync(StartInitiatorAsync);

        var responderButton = new Button { Text = "Wait as responder" };
        responderButton.Clicked += async (_, _) => await ExecuteAsync(StartResponderAsync);

        _pingButton.Clicked += async (_, _) => await ExecuteAsync(PingAsync);

        var closeButton = new Button { Text = "Close connection" };
        closeButton.Clicked += async (_, _) => await ExecuteAsync(CloseConnectionAsync);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 18,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Dyract WebRTC physical-device harness", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    new Label
                    {
                        Text = "Experiment only. No chat messages are sent. Signaling uses normal Dyract signed/capability-protected endpoints; the DataChannel carries fixed binary diagnostic frames only."
                    },
                    Section("Local identity"),
                    _peerIdLabel,
                    copyInviteButton,
                    Section("Directory"),
                    _directoryEntry,
                    registerButton,
                    Section("Remote identity"),
                    _remoteInvitationEditor,
                    loadRemoteButton,
                    _remotePeerLabel,
                    copyPairingButton,
                    Section("Remote permission to reach it"),
                    _remotePairingEditor,
                    validatePairingButton,
                    Section("ICE / STUN"),
                    _stunEntry,
                    new Label { Text = "Leave blank for host-candidate/LAN testing. TURN is intentionally not enabled in this DirectOnly harness." },
                    Section("Connection"),
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { initiatorButton, responderButton }
                    },
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { _pingButton, closeButton }
                    },
                    Section("Status"),
                    _statusLabel,
                    Section("Local diagnostic log"),
                    _logLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ExecuteAsync(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        using var identity = await _identityVault.GetOrCreateAsync();
        _peerIdLabel.Text = $"Peer ID: {identity.PeerId.Value}";
        AppendLog("Secure diagnostic identity ready.");

        _remotePairingEditor.Text = await ReadStoredPairingResponseAsync();

        if (!string.IsNullOrWhiteSpace(_remoteInvitationEditor.Text))
        {
            await LoadRemoteInvitationAsync();
        }

        if (_remoteInvitation is not null && !string.IsNullOrWhiteSpace(_remotePairingEditor.Text))
        {
            await ValidateRemotePairingAsync();
        }
    }

    private async Task RegisterAsync()
    {
        var directory = ParseDirectoryBaseUri(_directoryEntry.Text);
        Preferences.Default.Set(DirectoryPreference, directory.AbsoluteUri);

        using var identity = await _identityVault.GetOrCreateAsync();
        using var httpClient = new HttpClient
        {
            BaseAddress = directory,
            Timeout = TimeSpan.FromSeconds(20)
        };
        var response = await new DirectoryClient(httpClient).RegisterAsync(identity);
        if (!string.Equals(response.PeerId, identity.PeerId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Directory registration returned a different Peer ID.");
        }

        SetStatus($"Registered {response.PeerId}");
        AppendLog("Directory registration succeeded.");
    }

    private async Task CopyLocalInvitationAsync()
    {
        using var identity = await _identityVault.GetOrCreateAsync();
        var invitation = ContactInvitationFactory.Create(identity);
        await Clipboard.Default.SetTextAsync(invitation);
        SetStatus("Local contact invitation copied.");
    }

    private async Task LoadRemoteInvitationAsync()
    {
        var value = _remoteInvitationEditor.Text?.Trim();
        if (!ContactInvitationCodec.TryDecode(value, out var invitation, out var error) || invitation is null)
        {
            throw new InvalidOperationException(error ?? "Remote contact invitation is invalid.");
        }

        using var identity = await _identityVault.GetOrCreateAsync();
        if (string.Equals(invitation.PeerId, identity.PeerId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Remote invitation belongs to this diagnostic device.");
        }

        var previousPeerId = _remoteInvitation?.PeerId;
        _remoteInvitation = invitation;
        _remoteCapability = null;

        if (previousPeerId is not null && !string.Equals(previousPeerId, invitation.PeerId, StringComparison.Ordinal))
        {
            _remotePairingEditor.Text = string.Empty;
            SecureStorage.Default.Remove(RemotePairingSecureKey);
        }

        _remotePeerLabel.Text = $"Remote peer: {invitation.PeerId}\nFingerprint: {ContactInvitationCodec.GetFingerprint(invitation)}";
        Preferences.Default.Set(RemoteInvitationPreference, value ?? string.Empty);
        SetStatus("Remote identity pinned from invitation.");
        AppendLog("Remote contact invitation verified against its Peer ID.");
    }

    private async Task CopyPairingResponseAsync()
    {
        var invitation = RequireRemoteInvitation();
        using var identity = await _identityVault.GetOrCreateAsync();
        var capability = ContactCapabilityFactory.Create(
            identity,
            invitation.PeerId,
            TimeSpan.FromDays(30));
        var response = ContactPairingCodec.Encode(capability);
        await Clipboard.Default.SetTextAsync(response);
        SetStatus("Pairing response copied. Import it on the remote device.");
    }

    private async Task ValidateRemotePairingAsync()
    {
        var invitation = RequireRemoteInvitation();
        var value = _remotePairingEditor.Text?.Trim();
        if (!ContactPairingCodec.TryDecode(value, out var capability, out var decodeError) || capability is null)
        {
            throw new InvalidOperationException(decodeError ?? "Remote pairing response is invalid.");
        }

        if (!string.Equals(capability.IssuerPeerId, invitation.PeerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pairing response was not issued by the pinned remote identity.");
        }

        var remotePublicKey = Convert.FromBase64String(invitation.PublicKey);
        using var identity = await _identityVault.GetOrCreateAsync();
        if (!ContactCapabilityVerifier.TryVerify(
                capability,
                remotePublicKey,
                identity.PeerId.Value,
                out var verificationError))
        {
            throw new InvalidOperationException(
                verificationError ?? "Remote pairing response could not be verified.");
        }

        _remoteCapability = capability;
        await SecureStorage.Default.SetAsync(RemotePairingSecureKey, value ?? string.Empty);
        SetStatus("Remote pairing response verified.");
        AppendLog("Capability permits this local identity to signal the pinned remote peer.");
    }

    private async Task StartInitiatorAsync()
    {
        await CloseConnectionAsync();
        var (remotePeerId, gateway) = await CreateGatewayAsync();
        _gateway = gateway;
        Preferences.Default.Set(StunPreference, _stunEntry.Text?.Trim() ?? string.Empty);

        var controller = new FsWebRtcDiagnosticController(
            Android.App.Application.Context,
            gateway,
            ParseStunUris(_stunEntry.Text));

        _connection = await controller.StartInitiatorAsync(remotePeerId);
        SetStatus($"Offer sent. Session {_connection.SessionId}. Waiting for peer connection...");
        AppendLog($"Initiator session {_connection.SessionId} started.");

        try
        {
            await _connection.Connected.WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("WebRTC did not reach Connected within 45 seconds.");
        }

        _pingButton.IsEnabled = true;
        SetStatus($"Connected. Session {_connection.SessionId}. Ping is ready.");
        AppendLog("Native WebRTC connection reached Connected.");
    }

    private async Task StartResponderAsync()
    {
        await CloseConnectionAsync();
        var (remotePeerId, gateway) = await CreateGatewayAsync();
        _gateway = gateway;
        Preferences.Default.Set(StunPreference, _stunEntry.Text?.Trim() ?? string.Empty);

        var controller = new FsWebRtcDiagnosticController(
            Android.App.Application.Context,
            gateway,
            ParseStunUris(_stunEntry.Text));

        SetStatus("Waiting up to 60 seconds for a valid offer from the pinned peer...");
        _connection = await controller.WaitForOfferAndStartResponderAsync(
            remotePeerId,
            TimeSpan.FromSeconds(60));
        AppendLog($"Responder accepted session {_connection.SessionId}.");

        try
        {
            await _connection.Connected.WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("WebRTC did not reach Connected within 45 seconds.");
        }

        _echoCancellation = new CancellationTokenSource();
        _echoTask = RunEchoLoopAsync(_connection, _echoCancellation.Token);
        _pingButton.IsEnabled = false;
        SetStatus($"Connected as responder. Session {_connection.SessionId}. Echo loop active.");
        AppendLog("Native WebRTC connection reached Connected; binary ping echo enabled.");
    }

    private async Task PingAsync()
    {
        var connection = _connection ?? throw new InvalidOperationException("Start an initiator connection first.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = await connection.PingAsync(timeout.Token);
        SetStatus($"Pong received in {elapsed.TotalMilliseconds:F1} ms.");
        AppendLog($"Diagnostic binary frame RTT: {elapsed.TotalMilliseconds:F1} ms.");
    }

    private async Task RunEchoLoopAsync(
        FsWebRtcDiagnosticConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.RunEchoResponderAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SetStatus($"Echo loop failed: {exception.Message}");
                AppendLog("Responder echo loop stopped with an error.");
            });
        }
    }

    private async Task<(PeerId RemotePeerId, HarnessPeerSignalingGateway Gateway)> CreateGatewayAsync()
    {
        var invitation = RequireRemoteInvitation();
        var capability = _remoteCapability
            ?? throw new InvalidOperationException("Validate the remote pairing response first.");
        var directory = ParseDirectoryBaseUri(_directoryEntry.Text);
        var remotePublicKey = Convert.FromBase64String(invitation.PublicKey);
        using var identity = await _identityVault.GetOrCreateAsync();

        if (!ContactCapabilityVerifier.TryVerify(
                capability,
                remotePublicKey,
                identity.PeerId.Value,
                out var verificationError))
        {
            throw new InvalidOperationException(
                verificationError ?? "Remote pairing response is invalid or expired.");
        }

        if (!PeerId.TryParse(invitation.PeerId, out var remotePeerId))
        {
            throw new InvalidOperationException("Pinned remote Peer ID is invalid.");
        }

        Preferences.Default.Set(DirectoryPreference, directory.AbsoluteUri);
        return (
            remotePeerId,
            new HarnessPeerSignalingGateway(
                directory,
                _identityVault,
                remotePeerId,
                remotePublicKey,
                capability));
    }

    private async Task CloseConnectionAsync()
    {
        _pingButton.IsEnabled = false;
        _echoCancellation?.Cancel();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        if (_echoTask is not null)
        {
            try
            {
                await _echoTask;
            }
            catch (OperationCanceledException)
            {
            }
            _echoTask = null;
        }

        _echoCancellation?.Dispose();
        _echoCancellation = null;
        _gateway?.Dispose();
        _gateway = null;
        SetStatus("Connection closed.");
    }

    private async Task<string> ReadStoredPairingResponseAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(RemotePairingSecureKey) ?? string.Empty;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Stored diagnostic pairing capability could not be read securely.", exception);
        }
    }

    private ContactInvitation RequireRemoteInvitation()
        => _remoteInvitation ?? throw new InvalidOperationException("Load and verify a remote contact invitation first.");

    private static Uri ParseDirectoryBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new ArgumentException("Directory URL must be an HTTPS origin with no credentials, path, query, or fragment.");
        }

        return new UriBuilder(parsed) { Path = "/" }.Uri;
    }

    private static string[] ParseStunUris(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var values = value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length > 4 || values.Any(uri => !uri.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Specify at most four STUN URIs using the stun: scheme. TURN is not enabled in this harness.");
        }

        return values;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
            AppendLog($"ERROR: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SetStatus(string value) => _statusLabel.Text = value;

    private void AppendLog(string value)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {value}";
        _logLabel.Text = string.IsNullOrWhiteSpace(_logLabel.Text)
            ? line
            : $"{_logLabel.Text}\n{line}";
    }

    private static Label Section(string text)
        => new()
        {
            Text = text,
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            Margin = new Thickness(0, 8, 0, 0)
        };
}
