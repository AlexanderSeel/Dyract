# Dyract.App

`Dyract.App` is the .NET MAUI Android/iOS client.

The current mobile slice intentionally contains only the first security-critical user flow:

- start the app,
- establish the installation boundary,
- load an existing cryptographic identity or create one,
- persist the private identity material through MAUI `SecureStorage`,
- display the derived `dyr_...` Peer ID,
- copy the public Peer ID.

Messaging, contacts and P2P transport are not enabled yet.

## Build prerequisites

Install .NET 10 and the platform workloads required by your development host.

Android:

```bash
dotnet workload install maui-android
dotnet build src/Dyract.App/Dyract.App.csproj -f net10.0-android
```

iOS requires macOS/Xcode plus the iOS workload:

```bash
dotnet workload install maui-ios
dotnet build src/Dyract.App/Dyract.App.csproj -f net10.0-ios
```

`Dyract.Mobile.slnx` contains the app and its shared dependencies. The root `Dyract.slnx` intentionally remains the workload-free server/core/test solution used by the lightweight CI job.

## Identity storage

The bootstrap uses `SecureStorage.Default` for the small PKCS#8 identity key.

On the supported mobile platforms MAUI maps this to platform secure-storage facilities. The application never writes the private identity key to the local chat database or ordinary files.

The current implementation has two deliberate behaviors:

1. It does **not** silently replace an unreadable/corrupt secure identity because doing so would unexpectedly change the user's Peer ID.
2. A fresh installation clears an old Dyract identity entry that may remain in iOS Keychain after uninstall. Until an explicit recovery feature exists, reinstall means a new Dyract identity.

Android app backup is disabled in the manifest so encrypted application data is not silently restored to a different installation/device.

## Security limitation

This is a secure-storage bootstrap, not the final key architecture. The ECDSA private key is exportable in application memory because the shared `PeerIdentity` API currently imports/exports PKCS#8 material.

A later hardening phase should evaluate platform-native non-exportable keys (including Secure Enclave where appropriate), while preserving a well-defined optional recovery/export design.
