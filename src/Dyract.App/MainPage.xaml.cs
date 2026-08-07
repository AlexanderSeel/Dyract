using Dyract.App.Security;
using Dyract.Client;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class MainPage : ContentPage
{
    private readonly IIdentityVault _identityVault;
    private readonly ILocalStore _localStore;
    private string? _peerId;
    private string? _contactInvitation;
    private bool _initialized;
    private bool _busy;

    public MainPage(IIdentityVault identityVault, ILocalStore localStore)
    {
        InitializeComponent();
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
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

            await LoadContactsAsync();
        }
        catch (Exception exception)
        {
            _initialized = false;
            PeerIdLabel.Text = "Identity unavailable";
            CopyPeerIdButton.IsEnabled = false;
            CopyInviteButton.IsEnabled = false;
            AddContactButton.IsEnabled = false;
            StatusLabel.Text = exception is IdentityVaultException
                ? exception.Message
                : "Dyract could not initialize its local identity or encrypted data store.";
        }
    }

    private async Task InitializeAsync()
    {
        using var identity = await _identityVault.GetOrCreateAsync();
        await _localStore.InitializeAsync();

        _peerId = identity.PeerId.Value;
        _contactInvitation = ContactInvitationFactory.Create(identity);
        var publicKey = identity.ExportPublicKey();

        PeerIdLabel.Text = _peerId;
        FingerprintLabel.Text = $"Fingerprint: {ContactInvitationCodec.GetFingerprint(publicKey)}";
        CopyPeerIdButton.IsEnabled = true;
        CopyInviteButton.IsEnabled = true;
        AddContactButton.IsEnabled = true;
        StatusLabel.Text = "Identity and encrypted local storage ready.";
        _initialized = true;
    }

    private async Task LoadContactsAsync()
    {
        if (!_initialized)
        {
            return;
        }

        var contacts = await _localStore.GetContactsAsync();
        ContactsView.ItemsSource = contacts;
        NoContactsLabel.IsVisible = contacts.Count == 0;
    }

    private async void OnCopyPeerIdClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_peerId))
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(_peerId);
        StatusLabel.Text = "Peer ID copied.";
    }

    private async void OnCopyInviteClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_contactInvitation))
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(_contactInvitation);
        StatusLabel.Text = "Contact invitation copied. Share it through a channel you trust.";
    }

    private async void OnAddContactClicked(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        AddContactButton.IsEnabled = false;

        try
        {
            if (!ContactInvitationCodec.TryDecode(InvitationEditor.Text?.Trim(), out var invitation, out var error))
            {
                StatusLabel.Text = error ?? "Contact invitation is invalid.";
                return;
            }

            if (invitation is null || string.Equals(invitation.PeerId, _peerId, StringComparison.Ordinal))
            {
                StatusLabel.Text = "You cannot add this installation as its own contact.";
                return;
            }

            var fingerprint = ContactInvitationCodec.GetFingerprint(invitation);
            var defaultName = $"Peer {invitation.PeerId[^6..]}";
            var displayName = await DisplayPromptAsync(
                "Save contact",
                $"Security fingerprint: {fingerprint}\n\nChoose a name stored only on this device.",
                accept: "Save",
                cancel: "Cancel",
                placeholder: "Local contact name",
                maxLength: 128,
                initialValue: defaultName);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                StatusLabel.Text = "Contact was not added.";
                return;
            }

            await _localStore.UpsertContactAsync(new ContactDraft(
                invitation.PeerId,
                Convert.FromBase64String(invitation.PublicKey),
                displayName.Trim()));

            InvitationEditor.Text = string.Empty;
            await LoadContactsAsync();
            StatusLabel.Text = $"{displayName.Trim()} saved locally. Endpoint authorization is established in the next pairing step.";
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Contact could not be saved: {exception.Message}";
        }
        finally
        {
            _busy = false;
            AddContactButton.IsEnabled = _initialized;
        }
    }

    private async void OnContactSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LocalContact contact)
        {
            return;
        }

        ContactsView.SelectedItem = null;
        await Navigation.PushAsync(new ConversationPage(_localStore, _identityVault, contact));
    }
}
