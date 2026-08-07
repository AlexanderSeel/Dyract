using Dyract.App.Security;
using Dyract.Storage;

namespace Dyract.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<IIdentityVault, SecureIdentityVault>();
        builder.Services.AddSingleton<ILocalEncryptionKeyProvider, SecureLocalEncryptionKeyProvider>();
        builder.Services.AddSingleton<ILocalStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "dyract-local-v1.db3");
            return new SqliteLocalStore(databasePath, keyProvider);
        });
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
