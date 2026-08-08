using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class SecurityPage : ContentPage
{
    private readonly string _fingerprint;

    public SecurityPage(string peerId, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        InitializeComponent();
        _fingerprint = fingerprint;
        PeerIdLabel.Text = peerId;
        FingerprintLabel.Text = fingerprint;
    }

    private async void OnCopyFingerprintClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_fingerprint);
    }
}
