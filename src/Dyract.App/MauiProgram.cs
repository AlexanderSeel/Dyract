using Dyract.App.Security;

namespace Dyract.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<IIdentityVault, SecureIdentityVault>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
