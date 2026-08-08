using Dyract.App.Directory;
using Dyract.App.Security;
using Dyract.Client;
using Dyract.Storage;
using Dyract.Transport;
using ZXing.Net.Maui.Controls;

namespace Dyract.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseBarcodeReader();

        builder.Services.AddSingleton<IIdentityVault, SecureIdentityVault>();
        builder.Services.AddSingleton<ILocalEncryptionKeyProvider, SecureLocalEncryptionKeyProvider>();
        builder.Services.AddSingleton<ILocalStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "dyract-local-v1.db3");
            return new SqliteLocalStore(databasePath, keyProvider);
        });
        builder.Services.AddSingleton<SqliteIncomingMessageStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "dyract-local-v1.db3");
            return new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteOutboxDeliveryQueue>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            var databasePath = Path.Combine(FileSystem.AppDataDirectory, "dyract-local-v1.db3");
            return new SqliteOutboxDeliveryQueue(databasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<IIncomingMessageStore>(services =>
            services.GetRequiredService<SqliteIncomingMessageStore>());
        builder.Services.AddSingleton<IOutgoingDeliveryStore>(services =>
            services.GetRequiredService<SqliteIncomingMessageStore>());
        builder.Services.AddSingleton<IOutboxDeliveryQueue>(services =>
            services.GetRequiredService<SqliteOutboxDeliveryQueue>());
        builder.Services.AddSingleton<PeerMessageProcessor>();
        builder.Services.AddSingleton<IDirectorySettingsStore, DirectorySettingsStore>();
        builder.Services.AddSingleton<IDirectoryService, DirectoryService>();
        builder.Services.AddSingleton<IDirectorySignalingService, DirectorySignalingService>();
        builder.Services.AddSingleton<IPeerSignalingGateway, DirectoryPeerSignalingGateway>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
