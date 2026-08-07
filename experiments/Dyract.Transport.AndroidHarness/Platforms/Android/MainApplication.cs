using Android.App;
using Android.Runtime;
using Microsoft.Maui;

namespace Dyract.Transport.AndroidHarness;

[Application]
public sealed class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
