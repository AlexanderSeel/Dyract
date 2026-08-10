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
            return new MigratingLocalStore(InstallationResetService.LocalDatabasePath, keyProvider);
        });
        builder.Services.AddSingleton<SqliteIncomingMessageStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteIncomingMessageStore(InstallationResetService.LocalDatabasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteReadReceiptStore>(services =>
        {
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteReadReceiptStore(InstallationResetService.LocalDatabasePath, localStore);
        });
        builder.Services.AddSingleton<SqliteOutboxDeliveryQueue>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteOutboxDeliveryQueue(InstallationResetService.LocalDatabasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteIssuedCapabilityStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteIssuedCapabilityStore(InstallationResetService.LocalDatabasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteAttachmentReceiveStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteAttachmentReceiveStore(InstallationResetService.LocalDatabasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteAttachmentSendStore>(services =>
        {
            var keyProvider = services.GetRequiredService<ILocalEncryptionKeyProvider>();
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteAttachmentSendStore(InstallationResetService.LocalDatabasePath, keyProvider, localStore);
        });
        builder.Services.AddSingleton<SqliteAttachmentSendMaintenance>(services =>
        {
            var localStore = services.GetRequiredService<ILocalStore>();
            return new SqliteAttachmentSendMaintenance(InstallationResetService.LocalDatabasePath, localStore);
        });
        builder.Services.AddSingleton<IIncomingMessageStore>(services =>
            services.GetRequiredService<SqliteIncomingMessageStore>());
        builder.Services.AddSingleton<IOutgoingDeliveryStore>(services =>
            services.GetRequiredService<SqliteIncomingMessageStore>());
        builder.Services.AddSingleton<IIncomingReadStore>(services =>
            services.GetRequiredService<SqliteReadReceiptStore>());
        builder.Services.AddSingleton<IOutgoingReadStore>(services =>
            services.GetRequiredService<SqliteReadReceiptStore>());
        builder.Services.AddSingleton<IOutboxDeliveryQueue>(services =>
            services.GetRequiredService<SqliteOutboxDeliveryQueue>());
        builder.Services.AddSingleton<IIssuedCapabilityStore>(services =>
            services.GetRequiredService<SqliteIssuedCapabilityStore>());
        builder.Services.AddSingleton<IAttachmentSendStore>(services =>
            services.GetRequiredService<SqliteAttachmentSendStore>());
        builder.Services.AddSingleton<PeerMessageProcessor>();
        builder.Services.AddSingleton<PeerReadReceiptService>();
        builder.Services.AddSingleton<IDirectorySettingsStore, DirectorySettingsStore>();
        builder.Services.AddSingleton<IInstallationResetService, InstallationResetService>();
        builder.Services.AddSingleton<IDirectoryService, DirectoryService>();
        builder.Services.AddSingleton<IDirectorySignalingService, DirectorySignalingService>();
        builder.Services.AddSingleton<IPeerSignalingGateway, DirectoryPeerSignalingGateway>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
