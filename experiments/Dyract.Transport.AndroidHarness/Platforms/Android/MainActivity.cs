using Android.App;
using Android.Content.PM;

namespace Dyract.Transport.AndroidHarness;

[Activity(
    Theme = "@style/Maui.MainTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize |
                           ConfigChanges.Orientation |
                           ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
}
