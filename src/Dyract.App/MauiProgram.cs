using Dyract.App.Directory;
using Dyract.App.Security;
using Dyract.Storage;
using Dyract.Transport;

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
        builder.Services.AddSingleton<IDirectorySettingsStore, DirectorySettingsStore>();
        builder.Services.AddSingleton<IDirectoryService, DirectoryService>();
        builder.Services.AddSingleton<IDirectorySignalingService, DirectorySignalingService>();
        builder.Services.AddSingleton<IPeerSignalingGateway, DirectoryPeerSignalingGateway>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
