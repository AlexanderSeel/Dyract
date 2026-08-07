# Dyract.App

`Dyract.App` is the .NET MAUI Android/iOS client for Dyract.

The current application is no longer only an identity bootstrap. It now implements the complete local/offline foundation plus directory discovery/signaling adapters; the concrete peer transport is the next major milestone.

## Current mobile flow

The app can currently:

1. establish a fresh-install boundary,
2. securely load/create the installation identity,
3. display/copy the `dyr_...` Peer ID and fingerprint,
4. create/import `dyract://contact/v1/...` identity invitations,
5. save local-only contact names in encrypted local storage,
6. create/import reciprocal `dyract://pair/v1/...` reachability permissions,
7. configure an HTTPS Dyract directory origin,
8. register the installation identity,
9. perform capability-protected reachability checks for paired contacts,
10. open local conversations,
11. encrypt and transactionally queue outgoing text messages,
12. expose authenticated signaling through `IDirectorySignalingService` for the upcoming ICE/DataChannel transport.

Queued messages are **not yet delivered over the network**. The UI deliberately reports them as locally queued rather than pretending they are sent/delivered.

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

`Dyract.Mobile.slnx` contains the app and shared dependencies. `Dyract.slnx` remains the workload-free core/server/storage/transport/test solution used by core CI.

The Android Release app is built in GitHub Actions. iOS source/project support exists, but macOS/iOS CI and physical iPhone validation are still required.

## Identity storage

`SecureIdentityVault` uses `SecureStorage.Default` for the PKCS#8 identity material.

On Android/iOS MAUI maps this to platform secure-storage facilities. The application never writes the identity private key into SQLite or an ordinary app file.

Two deliberate behaviors remain:

1. Dyract does **not** silently replace an unreadable/corrupt identity because that would unexpectedly change the Peer ID.
2. A fresh install clears an old Dyract identity entry that may remain in iOS Keychain after uninstall. Until recovery exists, reinstall means a new Dyract identity.

Android application backup is disabled under the current privacy model.

### Current identity limitation

The shared ECDSA identity remains exportable in application memory because `PeerIdentity` imports/exports PKCS#8 material.

Production hardening should evaluate platform-native non-exportable keys (including Secure Enclave where appropriate) while keeping any recovery/export flow explicit and encrypted.

## Local data storage

`Dyract.App` consumes `Dyract.Storage` through `ILocalStore`.

The local SQLite database stores:

```text
contacts
conversations
messages
outbox
schema metadata
```

User-content fields are encrypted with AES-256-GCM before SQLite writes. The encryption key is a separate random 256-bit secret held through MAUI SecureStorage.

Currently encrypted fields include:

- local contact display names,
- imported contact capabilities,
- text message payloads,
- outbox error details.

This is field encryption, not a claim that every byte/metadata field in the SQLite file is hidden.

### Transactional outbox

Pressing Send performs:

```text
encrypt text
INSERT message = Queued
INSERT outbox item
UPDATE conversation activity
COMMIT
```

Only after the local transaction commits may the future transport worker attempt network delivery.

## Contact identity vs pairing

Dyract deliberately separates two actions.

### Identity invitation

```text
dyract://contact/v1/...
```

Pins:

- contact Peer ID,
- public identity key,
- displayed security fingerprint.

It does **not** grant endpoint access.

### Pairing response

```text
dyract://pair/v1/...
```

Contains a target-signed capability for one exact grantee. The current bootstrap lifetime is 30 days.

For both peers to resolve/signal each other, both sides exchange a pairing response. The imported response is verified against the saved contact public key and local grantee identity before encrypted storage.

## Directory integration

The configured directory must be an HTTPS origin, for example:

```text
https://directory.example.com/
```

Credentials, paths, query strings and fragments are rejected.

`IDirectoryService` handles:

- local identity registration,
- capability-protected contact reachability lookup,
- server public-key result pinning against the saved local contact identity.

## Signaling integration

`IDirectorySignalingService` wraps `PeerSignalingClient` for the future transport adapter.

It exposes:

```text
SendAsync
FetchAsync
AcknowledgeAsync
```

Before sending negotiation data to a contact, it re-validates the saved capability against the pinned contact public key and the local Peer ID.

The server accepts only short-lived connection-negotiation signal types. Chat message bodies do not use signaling.

See [`../../docs/signaling.md`](../../docs/signaling.md).

## Transport

The shipping app does not yet reference a WebRTC library.

`Dyract.Transport` provides the library-neutral `IPeerTransport` and `IPeerConnection` contracts. The current native WebRTC package evaluation lives under `experiments/` so package/API/runtime risk can be proven without coupling the application to one implementation.

See [`../../docs/transport-spike.md`](../../docs/transport-spike.md).

## Next mobile milestone

The next milestone is a physical-device data-only peer-channel proof:

```text
Android A
  -> directory registration/presence
  -> create ICE/WebRTC offer
  -> Dyract signaling
Android B
  -> fetch/ACK offer
  -> answer + candidates
  -> Dyract signaling
A/B
  -> DataChannel open
  -> fixed diagnostic byte frames
```

After that works across the required network matrix, the separate authenticated/forward-secret Dyract peer-session protocol and outbox delivery worker can be layered above the transport.
