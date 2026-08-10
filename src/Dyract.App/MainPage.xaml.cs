using Dyract.App.Directory;
using Dyract.App.Security;
using Dyract.Client;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class MainPage : ContentPage
{
    private readonly IIdentityVault _identityVault;
    private readonly ILocalStore _localStore;
    private readonly IIssuedCapabilityStore _issuedCapabilityStore;
    private readonly IDirectoryService _directoryService;
    private readonly IInstallationResetService _installationResetService;
    private readonly IServiceProvider _services;
    private string? _peerId;
    private string? _fingerprint;
    private string? _contactInvitation;
    private bool _initialized;
    private bool _busy;
    private bool _scanning;
    private bool _directoryBusy;

    public MainPage(
        IIdentityVault identityVault,
        ILocalStore localStore,
        IIssuedCapabilityStore issuedCapabilityStore,
        IDirectoryService directoryService,
        IInstallationResetService installationResetService,
        IServiceProvider services)
    {
        InitializeComponent();
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _issuedCapabilityStore = issuedCapabilityStore ?? throw new ArgumentNullException(nameof(issuedCapabilityStore));
        _directoryService = directoryService ?? throw new ArgumentNullException(nameof(directoryService));
        _installationResetService = installationResetService ?? throw new ArgumentNullException(nameof(installationResetService));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await _installationResetService.CompletePendingResetAsync();

            if (!_initialized)
            {
                await InitializeAsync();
            }

            await LoadContactsAsync();
        }
        catch (Exception exception)
        {
            _initialized = false;
            _peerId = null;
            _fingerprint = null;
            _contactInvitation = null;
            PeerIdLabel.Text = "Identity unavailable";
            FingerprintLabel.Text = "Fingerprint: unavailable";
            CopyPeerIdButton.IsEnabled = false;
            CopyInviteButton.IsEnabled = false;
            ShowInviteQrButton.IsEnabled = false;
            SecuritySettingsButton.IsEnabled = true;
            ScanQrButton.IsEnabled = false;
            AddContactButton.IsEnabled = false;
            SaveDirectoryButton.IsEnabled = false;
            RegisterDirectoryButton.IsEnabled = false;
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
        _fingerprint = ContactInvitationCodec.GetFingerprint(publicKey);
        var configuredDirectory = _directoryService.ConfiguredBaseUri;

        PeerIdLabel.Text = _peerId;
        FingerprintLabel.Text = $"Fingerprint: {_fingerprint}";
        DirectoryUrlEntry.Text = configuredDirectory?.AbsoluteUri ?? string.Empty;
        DirectoryStatusLabel.Text = configuredDirectory is null
            ? "No directory configured."
            : $"Configured: {configuredDirectory}";
        CopyPeerIdButton.IsEnabled = true;
        CopyInviteButton.IsEnabled = true;
        ShowInviteQrButton.IsEnabled = true;
        SecuritySettingsButton.IsEnabled = true;
        ScanQrButton.IsEnabled = true;
        AddContactButton.IsEnabled = true;
        SaveDirectoryButton.IsEnabled = true;
        RegisterDirectoryButton.IsEnabled = configuredDirectory is not null;
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

    private async void OnShowInviteQrClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_contactInvitation))
        {
            return;
        }

        await Navigation.PushAsync(new QrDisplayPage(
            "My contact QR",
            _contactInvitation,
            "This QR contains your public Dyract contact invitation. It does not contain your private identity key or local contact/message data.",
            "Copy contact invite"));
    }

    private async void OnSecuritySettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SecurityPage(
            _peerId,
            _fingerprint,
            _installationResetService,
            OnInstallationResetCompletedAsync));
    }

    private async Task OnInstallationResetCompletedAsync()
    {
        _initialized = false;
        _peerId = null;
        _fingerprint = null;
        _contactInvitation = null;
        InvitationEditor.Text = string.Empty;
        ContactsView.ItemsSource = null;
        NoContactsLabel.IsVisible = true;

        await InitializeAsync();
        await LoadContactsAsync();
        StatusLabel.Text = "Dyract was reset. A new identity and local-data key were created.";
    }

    private async void OnScanQrClicked(object? sender, EventArgs e)
    {
        if (!_initialized || _scanning)
        {
            return;
        }

        _scanning = true;
        ScanQrButton.IsEnabled = false;
        try
        {
            var scanner = new QrScannerPage();
            await Navigation.PushAsync(scanner);
            var value = await scanner.Result;
            if (string.IsNullOrWhiteSpace(value))
            {
                StatusLabel.Text = "QR scan cancelled.";
                return;
            }

            InvitationEditor.Text = value;
            StatusLabel.Text = "Dyract QR scanned. Tap Verify and import to run the normal identity/capability checks.";
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"QR scanner unavailable ({exception.GetType().Name}). You can still paste the value manually.";
        }
        finally
        {
            _scanning = false;
            ScanQrButton.IsEnabled = _initialized;
        }
    }

    private void OnSaveDirectoryClicked(object? sender, EventArgs e)
    {
        if (!_initialized || _directoryBusy)
        {
            return;
        }

        try
        {
            var uri = _directoryService.Configure(DirectoryUrlEntry.Text ?? string.Empty);
            DirectoryUrlEntry.Text = uri.AbsoluteUri;
            DirectoryStatusLabel.Text = $"Configured: {uri}";
            RegisterDirectoryButton.IsEnabled = true;
        }
        catch (ArgumentException exception)
        {
            RegisterDirectoryButton.IsEnabled = _directoryService.ConfiguredBaseUri is not null;
            DirectoryStatusLabel.Text = exception.Message;
        }
    }

    private async void OnRegisterDirectoryClicked(object? sender, EventArgs e)
    {
        if (!_initialized || _directoryBusy)
        {
            return;
        }

        _directoryBusy = true;
        SaveDirectoryButton.IsEnabled = false;
        RegisterDirectoryButton.IsEnabled = false;

        try
        {
            var result = await _directoryService.RegisterAsync();
            DirectoryStatusLabel.Text =
                $"Registered {ShortPeerId(result.PeerId)} at {result.BaseUri}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Security.SecurityException)
        {
            DirectoryStatusLabel.Text = $"Directory registration failed: {exception.Message}";
        }
        finally
        {
            _directoryBusy = false;
            SaveDirectoryButton.IsEnabled = _initialized;
            RegisterDirectoryButton.IsEnabled = _initialized && _directoryService.ConfiguredBaseUri is not null;
        }
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
            var value = InvitationEditor.Text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                StatusLabel.Text = "Paste or scan a Dyract contact invitation or pairing response first.";
                return;
            }

            if (value.StartsWith(ContactPairingCodec.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                await ImportPairingResponseAsync(value);
            }
            else
            {
                await ImportContactInvitationAsync(value);
            }
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Contact data could not be imported: {exception.Message}";
        }
        finally
        {
            _busy = false;
            AddContactButton.IsEnabled = _initialized;
        }
    }

    private async Task ImportContactInvitationAsync(string value)
    {
        if (!ContactInvitationCodec.TryDecode(value, out var invitation, out var error))
        {
            StatusLabel.Text = error ?? "Contact invitation is invalid.";
            return;
        }

        if (invitation is null || string.Equals(invitation.PeerId, _peerId, StringComparison.Ordinal))
        {
            StatusLabel.Text = "You cannot add this installation as its own contact.";
            return;
        }

        var existing = await _localStore.GetContactAsync(invitation.PeerId);
        var fingerprint = ContactInvitationCodec.GetFingerprint(invitation);
        var defaultName = existing?.DisplayName ?? $"Peer {invitation.PeerId[^6..]}";
        var displayName = await DisplayPromptAsync(
            existing is null ? "Save contact" : "Update contact",
            $"Security fingerprint: {fingerprint}\n\nChoose a name stored only on this device.",
            accept: "Save",
            cancel: "Cancel",
            placeholder: "Local contact name",
            maxLength: 128,
            initialValue: defaultName);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            StatusLabel.Text = "Contact was not changed.";
            return;
        }

        await _localStore.UpsertContactAsync(new ContactDraft(
            invitation.PeerId,
            Convert.FromBase64String(invitation.PublicKey),
            displayName.Trim(),
            existing?.Capability));

        InvitationEditor.Text = string.Empty;
        await LoadContactsAsync();
        StatusLabel.Text = existing?.Capability is null
            ? $"{displayName.Trim()} saved locally. Open the contact and exchange pairing responses next."
            : $"{displayName.Trim()} updated. Existing endpoint authorization was preserved.";
    }

    private async Task ImportPairingResponseAsync(string value)
    {
        if (string.IsNullOrWhiteSpace(_peerId))
        {
            StatusLabel.Text = "Local identity is not ready.";
            return;
        }

        if (!ContactPairingCodec.TryDecode(value, out var capability, out var error) || capability is null)
        {
            StatusLabel.Text = error ?? "Pairing response is invalid.";
            return;
        }

        var contact = await _localStore.GetContactAsync(capability.IssuerPeerId);
        if (contact is null)
        {
            StatusLabel.Text = "Pairing response is from an unknown peer. Import that peer's contact invitation first.";
            return;
        }

        if (!ContactCapabilityVerifier.TryVerify(
                capability,
                contact.IdentityPublicKey,
                _peerId,
                out var verificationError))
        {
            StatusLabel.Text = verificationError ?? "Pairing response could not be verified.";
            return;
        }

        if (contact.Capability is not null &&
            ContactPairingCodec.TryDecode(contact.Capability, out var existingCapability, out _) &&
            existingCapability is not null &&
            existingCapability.ExpiresUnixSeconds >= capability.ExpiresUnixSeconds)
        {
            StatusLabel.Text = "A pairing authorization with the same or later expiry is already stored.";
            InvitationEditor.Text = string.Empty;
            return;
        }

        await _localStore.UpsertContactAsync(new ContactDraft(
            contact.PeerId,
            contact.IdentityPublicKey,
            contact.DisplayName,
            ContactPairingCodec.Encode(capability)));

        InvitationEditor.Text = string.Empty;
        await LoadContactsAsync();
        var expiry = DateTimeOffset.FromUnixTimeSeconds(capability.ExpiresUnixSeconds).ToLocalTime();
        StatusLabel.Text = $"{contact.DisplayName} paired. Endpoint discovery is authorized until {expiry:g}.";
    }

    private async void OnContactSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LocalContact contact)
        {
            return;
        }

        ContactsView.SelectedItem = null;
        var page = ActivatorUtilities.CreateInstance<ConversationPage>(_services, contact);
        await Navigation.PushAsync(page);
    }

    private static string ShortPeerId(string peerId)
        => peerId.Length <= 18 ? peerId : $"{peerId[..10]}…{peerId[^6..]}";
}
