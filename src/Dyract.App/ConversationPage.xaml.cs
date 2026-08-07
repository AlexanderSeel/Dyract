using Dyract.App.Security;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.App;

public partial class ConversationPage : ContentPage
{
    private readonly ILocalStore _localStore;
    private readonly IIdentityVault _identityVault;
    private readonly LocalContact _contact;
    private LocalConversation? _conversation;
    private string? _ownPeerId;
    private bool _initialized;
    private bool _sending;

    public ConversationPage(
        ILocalStore localStore,
        IIdentityVault identityVault,
        LocalContact contact)
    {
        InitializeComponent();
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        _contact = contact ?? throw new ArgumentNullException(nameof(contact));

        Title = contact.DisplayName;
        ContactNameLabel.Text = contact.DisplayName;
        ContactPeerIdLabel.Text = contact.PeerId;
        ContactFingerprintLabel.Text = $"Fingerprint: {ContactInvitationCodec.GetFingerprint(contact.IdentityPublicKey)}";
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
        }
        catch (Exception exception)
        {
            ConversationStatusLabel.Text = $"Conversation unavailable: {exception.Message}";
            SendButton.IsEnabled = false;
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
        ConversationStatusLabel.Text = "Local conversation ready. Network delivery is not connected yet.";
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
            ConversationStatusLabel.Text = "Message committed locally and queued for delivery. P2P transport comes next.";
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
}
