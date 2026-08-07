using Dyract.App.Security;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class MainPage : ContentPage
{
    private readonly IIdentityVault _identityVault;
    private string? _peerId;
    private bool _initialized;

    public MainPage(IIdentityVault identityVault)
    {
        InitializeComponent();
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            using var identity = await _identityVault.GetOrCreateAsync();
            _peerId = identity.PeerId.Value;
            PeerIdLabel.Text = _peerId;
            CopyPeerIdButton.IsEnabled = true;
            StatusLabel.Text = "Identity ready. Directory registration and contact exchange come next.";
        }
        catch (Exception exception)
        {
            _initialized = false;
            PeerIdLabel.Text = "Identity unavailable";
            CopyPeerIdButton.IsEnabled = false;
            StatusLabel.Text = exception is IdentityVaultException
                ? exception.Message
                : "Dyract could not initialize the local identity.";
        }
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
}
