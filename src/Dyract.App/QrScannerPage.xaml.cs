using Dyract.Protocol;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;

namespace Dyract.App;

public partial class QrScannerPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _accepted;

    public QrScannerPage()
    {
        InitializeComponent();
        ScannerView.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false,
            CharacterSet = "UTF-8"
        };
    }

    public Task<string?> Result => _result.Task;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            if (!BarcodeScanning.IsSupported)
            {
                StatusLabel.Text = "No supported camera is available on this device.";
                return;
            }

            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
            }

            if (status != PermissionStatus.Granted)
            {
                StatusLabel.Text = "Camera permission is required only while scanning Dyract QR codes.";
                return;
            }

            if (Volatile.Read(ref _accepted) == 0)
            {
                ScannerView.IsDetecting = true;
                StatusLabel.Text = "Point the camera at a Dyract QR code.";
            }
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Camera could not start ({exception.GetType().Name}).";
        }
    }

    protected override void OnDisappearing()
    {
        ScannerView.IsDetecting = false;
        _result.TrySetResult(null);
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (Volatile.Read(ref _accepted) != 0)
        {
            return;
        }

        var value = e.Results
            .Select(result => result.Value?.Trim())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsDyractOnboardingValue(value))
        {
            Dispatcher.Dispatch(() =>
                StatusLabel.Text = "That QR code is not a Dyract contact or pairing value.");
            return;
        }

        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            return;
        }

        ScannerView.IsDetecting = false;
        Dispatcher.Dispatch(async () =>
        {
            _result.TrySetResult(value);
            await Navigation.PopAsync();
        });
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            return;
        }

        ScannerView.IsDetecting = false;
        _result.TrySetResult(null);
        await Navigation.PopAsync();
    }

    private static bool IsDyractOnboardingValue(string value)
        => value.StartsWith(ContactInvitationCodec.Prefix, StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith(ContactPairingCodec.Prefix, StringComparison.OrdinalIgnoreCase);
}
