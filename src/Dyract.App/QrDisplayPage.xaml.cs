using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Dyract.App;

public partial class QrDisplayPage : ContentPage
{
    private readonly string _value;

    public QrDisplayPage(
        string title,
        string value,
        string explanation,
        string copyButtonText = "Copy value")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);

        InitializeComponent();
        _value = value;

        Title = title;
        HeadingLabel.Text = title;
        ExplanationLabel.Text = explanation;
        CopyButton.Text = copyButtonText;
        QrCodeView.Value = value;
        StatusLabel.Text = "Ready to scan.";
    }

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_value);
        StatusLabel.Text = "Copied.";
    }
}
