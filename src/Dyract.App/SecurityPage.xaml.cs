using Dyract.App.Security;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class SecurityPage : ContentPage
{
    private readonly string? _fingerprint;
    private readonly IInstallationResetService _installationResetService;
    private readonly Func<Task> _onResetCompleted;
    private bool _resetting;

    public SecurityPage(
        string? peerId,
        string? fingerprint,
        IInstallationResetService installationResetService,
        Func<Task> onResetCompleted)
    {
        InitializeComponent();
        _installationResetService = installationResetService ?? throw new ArgumentNullException(nameof(installationResetService));
        _onResetCompleted = onResetCompleted ?? throw new ArgumentNullException(nameof(onResetCompleted));
        _fingerprint = string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint;

        var identityAvailable = !string.IsNullOrWhiteSpace(peerId) && _fingerprint is not null;
        IdentityStatusLabel.Text = identityAvailable
            ? "Identity storage is readable."
            : "Identity storage is unavailable. Dyract has not generated a replacement identity.";
        PeerIdLabel.Text = identityAvailable ? peerId : "Unavailable";
        FingerprintLabel.Text = identityAvailable ? _fingerprint : "Unavailable";
        CopyFingerprintButton.IsEnabled = identityAvailable;
    }

    private async void OnCopyFingerprintClicked(object? sender, EventArgs e)
    {
        if (_fingerprint is not null)
        {
            await Clipboard.Default.SetTextAsync(_fingerprint);
        }
    }

    private async void OnResetIdentityClicked(object? sender, EventArgs e)
    {
        if (_resetting)
        {
            return;
        }

        var accepted = await DisplayAlertAsync(
            "Reset Dyract identity?",
            "This permanently deletes the local identity, contacts, conversations, messages, queued deliveries and capabilities on this installation. Existing contacts will not recognize the new Peer ID.",
            "Continue",
            "Cancel");
        if (!accepted)
        {
            return;
        }

        var confirmation = await DisplayPromptAsync(
            "Permanent reset",
            "Type RESET to permanently remove this identity and local data.",
            accept: "Reset",
            cancel: "Cancel",
            placeholder: "RESET",
            maxLength: 16);
        if (!string.Equals(confirmation?.Trim(), "RESET", StringComparison.Ordinal))
        {
            ResetStatusLabel.Text = "Reset cancelled. The confirmation text did not match RESET.";
            return;
        }

        _resetting = true;
        ResetIdentityButton.IsEnabled = false;
        ResetStatusLabel.Text = "Resetting local identity and data…";
        var resetCompleted = false;

        try
        {
            await _installationResetService.ResetAsync();
            resetCompleted = true;
            await _onResetCompleted();
            await Navigation.PopToRootAsync();
        }
        catch
        {
            ResetStatusLabel.Text = resetCompleted
                ? "The reset completed, but the new identity could not be initialized. Return to the main screen or restart Dyract to retry initialization."
                : "The reset could not complete. Dyract will resume any pending reset before normal identity initialization on the next launch.";
        }
        finally
        {
            _resetting = false;
            ResetIdentityButton.IsEnabled = true;
        }
    }
}
