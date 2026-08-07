namespace Dyract.Transport.AndroidHarness;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<HarnessIdentityVault>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
