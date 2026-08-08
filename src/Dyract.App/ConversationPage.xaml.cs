using Dyract.App.Directory;
using Dyract.App.Security;
using Dyract.Client;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class ConversationPage : ContentPage
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromDays(30);

    private readonly ILocalStore _localStore;
    private readonly IIdentityVault _identityVault;
    private readonly IDirectoryService _directoryService;
    private readonly LocalContact _contact;
    private LocalConversation? _conversation;
    private string? _ownPeerId;
    private bool _initialized;
    private bool _sending;
    private bool _resolving;
    private bool _creatingPairing;

    public ConversationPage(
        ILocalStore localStore,
        IIdentityVault identityVault,
        IDirectoryService directoryService,
        LocalContact contact)
    {
        InitializeComponent();
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        _directoryService = directoryService ?? throw new ArgumentNullException(nameof(directoryService));
        _contact = contact ?? throw new ArgumentNullException(nameof(contact));

        Title = contact.DisplayName;
        ContactNameLabel.Text = contact.DisplayName;
        ContactPeerIdLabel.Text = contact.PeerId;
        ContactFingerprintLabel.Text = $"Fingerprint: {ContactInvitationCodec.GetFingerprint(contact.IdentityPublicKey)}";
        PairingStateLabel.Text = GetPairingStateText(contact);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            await LoadMessagesAsync();
            UpdateReachabilityButton();
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Conversation unavailable: {exception.Message}";
            SendButton.IsEnabled = false;
            CopyPairingResponseButton.IsEnabled = false;
            ShowPairingQrButton.IsEnabled = false;
            ResolveContactButton.IsEnabled = false;
        }
    }

    private async Task InitializeAsync()
    {
        await _localStore.InitializeAsync();
        using var identity = await _identityVault.GetOrCreateAsync();
        _ownPeerId = identity.PeerId.Value;
        _conversation = await _localStore.GetOrCreateConversationAsync(_contact.PeerId);
        _initialized = true;
        SendButton.IsEnabled = true;
        CopyPairingResponseButton.IsEnabled = true;
        ShowPairingQrButton.IsEnabled = true;
        UpdateReachabilityButton();
        ConversationStatusLabel.Text = "Local conversation ready. Production P2P message transport is not connected yet.";
    }

    private async Task LoadMessagesAsync()
    {
        if (_conversation is null)
        {
            return;
        }

        var messages = await _localStore.GetMessagesAsync(_conversation.ConversationId);
        MessagesView.ItemsSource = messages;

        if (messages.Count > 0)
        {
            MessagesView.ScrollTo(messages[^1], position: ScrollToPosition.End, animate: false);
        }
    }

    private async void OnCopyPairingResponseClicked(object? sender, EventArgs e)
    {
        if (!_initialized || _creatingPairing)
        {
            return;
        }

        SetPairingBusy(true);
        try
        {
            var pairing = await CreatePairingResponseAsync();
            await Clipboard.Default.SetTextAsync(pairing.Response);
            ConversationStatusLabel.Text =
                $"Pairing response copied. {_contact.DisplayName} may import it to resolve you until {pairing.ExpiresAt.ToLocalTime():g}.";
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Pairing response could not be created: {exception.Message}";
        }
        finally
        {
            SetPairingBusy(false);
        }
    }

    private async void OnShowPairingQrClicked(object? sender, EventArgs e)
    {
        if (!_initialized || _creatingPairing)
        {
            return;
        }

        SetPairingBusy(true);
        try
        {
            var pairing = await CreatePairingResponseAsync();
            await Navigation.PushAsync(new QrDisplayPage(
                $"Pair {_contact.DisplayName}",
                pairing.Response,
                $"This QR grants only {_contact.DisplayName}'s pinned Peer ID permission to resolve your temporary reachability metadata until {pairing.ExpiresAt.ToLocalTime():g}.",
                "Copy pairing response"));
            ConversationStatusLabel.Text =
                $"Pairing QR created for {_contact.DisplayName}; expires {pairing.ExpiresAt.ToLocalTime():g}.";
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Pairing QR could not be created: {exception.Message}";
        }
        finally
        {
            SetPairingBusy(false);
        }
    }

    private async Task<(string Response, DateTimeOffset ExpiresAt)> CreatePairingResponseAsync()
    {
        using var identity = await _identityVault.GetOrCreateAsync();
        var capability = ContactCapabilityFactory.Create(
            identity,
            _contact.PeerId,
            PairingLifetime);
        return (
            ContactPairingCodec.Encode(capability),
            DateTimeOffset.FromUnixTimeSeconds(capability.ExpiresUnixSeconds));
    }

    private void SetPairingBusy(bool busy)
    {
        _creatingPairing = busy;
        CopyPairingResponseButton.IsEnabled = _initialized && !busy;
        ShowPairingQrButton.IsEnabled = _initialized && !busy;
    }

    private async void OnResolveContactClicked(object? sender, EventArgs e)
    {
        if (_resolving || !_initialized)
        {
            return;
        }

        _resolving = true;
        ResolveContactButton.IsEnabled = false;
        try
        {
            var result = await _directoryService.ResolveAsync(_contact);
            if (!result.IsReachable)
            {
                ConversationStatusLabel.Text =
                    $"{_contact.DisplayName} is registered but has no current reachability lease.";
                return;
            }

            var expiry = result.LeaseExpiresAt?.ToLocalTime();
            ConversationStatusLabel.Text = expiry is null
                ? $"{_contact.DisplayName} is reachable with {result.Candidates.Count} connection candidate(s)."
                : $"{_contact.DisplayName} is reachable with {result.Candidates.Count} candidate(s) until {expiry:g}.";
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Reachability check failed: {exception.Message}";
        }
        finally
        {
            _resolving = false;
            UpdateReachabilityButton();
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
        => await QueueMessageAsync();

    private async void OnComposerCompleted(object? sender, EventArgs e)
        => await QueueMessageAsync();

    private async Task QueueMessageAsync()
    {
        if (_sending || _conversation is null || string.IsNullOrWhiteSpace(_ownPeerId))
        {
            return;
        }

        var text = ComposerEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;

        try
        {
            await _localStore.QueueOutgoingTextAsync(
                _conversation.ConversationId,
                _ownPeerId,
                _contact.PeerId,
                text);

            ComposerEntry.Text = string.Empty;
            await LoadMessagesAsync();
            ConversationStatusLabel.Text = "Message committed locally and queued for delivery. Production P2P transport is not connected yet.";
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Message could not be queued: {exception.Message}";
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = _initialized;
        }
    }

    private void UpdateReachabilityButton()
    {
        ResolveContactButton.IsEnabled =
            _initialized &&
            !_resolving &&
            _contact.Capability is not null &&
            _directoryService.ConfiguredBaseUri is not null;
    }

    private static string GetPairingStateText(LocalContact contact)
    {
        if (contact.Capability is null)
        {
            return "You cannot resolve this contact yet. Import their pairing response first.";
        }

        if (!ContactPairingCodec.TryDecode(contact.Capability, out var capability, out _) || capability is null)
        {
            return "Stored pairing authorization is unreadable and should be replaced.";
        }

        var expiry = DateTimeOffset.FromUnixTimeSeconds(capability.ExpiresUnixSeconds);
        return expiry <= DateTimeOffset.UtcNow
            ? "Stored pairing authorization has expired. Ask this contact for a new pairing response."
            : $"You may resolve this contact until {expiry.ToLocalTime():g}.";
    }
}
