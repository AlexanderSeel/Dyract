# Dyract

**Direct by design.**

Dyract is an experimental privacy-first messenger for Android and iPhone built around direct peer-to-peer communication. Each installation owns a cryptographic identity; contacts, local names, conversations and messages remain on the participating devices. The central service is intentionally narrow: identity registration, short-lived reachability, capability-protected connection signaling and later wake-up — not chat history.

> Dyract is still an implementation/protocol experiment. The cryptographic composition, transport choice and mobile security model have not been independently audited and must not yet be treated as production-grade.

## Core principles

- **Device-owned identity** — a peer owns a locally generated private key; the public `dyr_...` Peer ID is derived from the public key.
- **Local-first data** — contacts, user-assigned names, conversations, message bodies and attachments do not belong in the directory.
- **Minimal directory** — the server does not maintain profiles, a social/contact graph or message history.
- **Pinned identities** — a contact invitation binds a Peer ID to public-key material and the mobile client stores that relationship locally.
- **Explicit reachability authorization** — identity knowledge alone does not reveal an endpoint. A target signs a grantee-bound capability for the exact peer that may resolve or signal it.
- **Revocable authorization** — a tracked issued capability can be revoked before expiry without storing the grantee/contact relationship on the server.
- **Short-lived presence** — published reachability leases expire within two minutes.
- **Short-lived signaling** — offer/answer/ICE negotiation data expires within 60 seconds and is retained only until target ACK or expiry.
- **Store before send** — an outgoing message is encrypted and committed to SQLite together with its outbox row before network delivery is attempted.
- **Direct-first transport** — ICE/STUN direct connectivity is the target; TURN is an explicit fallback when direct establishment is impossible.
- **Fail closed** — configured PostgreSQL/Redis infrastructure is not silently replaced by weaker process-local state when unavailable.
- **No home-grown primitives** — Dyract composes established platform cryptography rather than inventing cryptographic algorithms.

## Architecture

```text
                              Dyract Directory
                         ASP.NET Core / .NET 10
                    +--------------------------------+
                    | identity + capability auth     |
                    | temporary presence leases      |
                    | ephemeral WebRTC signaling     |
                    | replay protection              |
                    | wake routing (*)               |
                    |                                |
                    | NO profiles                    |
                    | NO contact graph               |
                    | NO chat history                |
                    | NO attachments                 |
                    +-------------+------------------+
                                  |
                +-----------------+------------------+
                |                                    |
          PostgreSQL (*)                         Redis (*)
          durable metadata                       TTL state
          - PeerId/public key                    - registration challenges
          - revoked capability IDs               - replay nonces
            + natural expiry                     - presence leases
                                                 - signaling inboxes
                |                                    |
                +-----------------+------------------+
                                  |
                       discovery / negotiation
                                  |
                 +----------------+----------------+
                 |                                 |
             Alice device                       Bob device
              .NET MAUI                          .NET MAUI
                 |                                 |
        encrypted SQLite                  encrypted SQLite
        contacts/messages                 contacts/messages
        transactional outbox              transactional outbox
                 |                                 |
                 +===== authenticated P2P (*) =====+

(*) PostgreSQL and Redis are optional for development; concrete production
    transport and wake-up remain gated by physical-device validation.
```

A raw `PeerId -> IP` table is not sufficient for mobile networks. NAT/CGNAT, Wi-Fi/cellular transitions and background restrictions require authenticated short-lived reachability plus NAT traversal. Dyract therefore treats an endpoint as an expiring connection-candidate set, not a permanent address.

## Current implementation

### Identity, authorization, discovery and signaling

- .NET 10 shared/server solution
- ECDSA P-256 identity generation/signing through `System.Security.Cryptography`
- SHA-256-derived `dyr_...` Peer IDs
- challenge/response registration
- signed identity lookup
- request timestamps + replay nonces
- target-signed, grantee-bound contact capabilities
- 30-day default / 90-day maximum capability lifetime
- signed pre-expiry capability revocation
- revocation blocks both peer resolve and signaling
- signed presence publish/update/removal
- maximum two-minute presence leases
- bounded candidate validation
- capability-protected `/api/v1/peer/resolve`
- capability-protected short-lived signaling send
- target-signed signaling fetch and explicit ACK
- signaling types limited to `offer`, `answer`, `candidate`, `end-of-candidates`, `close`
- signal TTL maximum 60 seconds
- signal payload maximum 32 KiB UTF-8
- maximum 64 pending signals per target and 20 per fetch batch
- request-body limits + process-local per-client API rate limits
- unit, ASP.NET, PostgreSQL and Redis integration coverage

Signaling is intentionally **not a chat mailbox**. It carries only short-lived connection-negotiation data. Message bodies remain in the encrypted local outbox until a peer channel can deliver them.

### Durable directory metadata — PostgreSQL

`ConnectionStrings:Dyract` enables PostgreSQL-backed durable state.

Implemented:

- async `IIdentityStore` with PostgreSQL implementation;
- ordered migration ledger;
- transaction-scoped advisory migration lock;
- schema-shape validation before adoption and on later startup;
- migration v1: peer identity registry;
- migration v2: metadata-minimized capability revocations;
- PostgreSQL 18 integration CI;
- restart-persistent revocation tests.

The revocation table stores only:

```text
issuer_peer_id
capability_id
expires_at
```

It deliberately does not store the capability grantee/contact relation.

Without PostgreSQL the server uses in-memory identity/revocation stores for zero-setup development.

See [`docs/server-database-migrations.md`](docs/server-database-migrations.md) and [`docs/capability-revocation.md`](docs/capability-revocation.md).

### Shared short-lived directory state — Redis

`ConnectionStrings:Redis` enables cross-instance transient state through StackExchange.Redis.

Redis-backed interfaces cover:

```text
IRegistrationChallengeStore
IReplayNonceStore
IPresenceStore
ISignalStore
```

Implemented/validated behavior:

- two-minute one-time registration challenges work across instances;
- five-minute signed-request replay markers work across instances;
- two-minute presence leases work across instances;
- WebRTC signaling can be sent/fetched/ACKed across instances;
- signaling capacity/expiry changes are atomic through Redis scripts;
- signaling fetch remains non-destructive until explicit ACK;
- Peer IDs/nonces are SHA-256-derived in replay/signaling key names;
- presence key names use a SHA-256-derived peer token;
- configured Redis is pinged at startup and failure does not silently fall back to local state;
- Redis 8 service-container CI covers the cross-instance semantics.

Without Redis the same interfaces use process-local in-memory implementations.

See [`docs/redis-transient-state.md`](docs/redis-transient-state.md).

### Mobile client

- .NET MAUI Android/iOS app
- first-run identity generation/loading through MAUI `SecureStorage`
- Android Keystore-backed secure storage path
- iOS Keychain path
- Peer ID and identity fingerprint display
- versioned `dyract://contact/v1/...` identity invitations
- QR display and camera scanning for contact invitations
- local contact names stored only on the device
- reciprocal `dyract://pair/v1/...` reachability responses
- pairing QR display/scanning
- imported capability verification against the pinned contact public key and local grantee identity
- separately tracked incoming and outgoing reachability grants
- encrypted local storage of issued capability state
- mobile grant-revocation UX
- HTTPS-only directory configuration
- mobile identity registration with the directory
- capability-protected reachability check for paired contacts
- directory-returned public key pinned against the locally saved contact key
- authenticated directory signaling adapter for transport implementations
- contact list and conversation screen
- locally queued text messages
- Android cleartext networking disabled
- Android application backup disabled for the current privacy model
- Android Release CI: warning-clean
- iOS simulator Release CI on macOS 26 / Xcode 26.6: warning-clean

Physical camera/SecureStorage/transport behavior still requires real-device validation.

### Local storage

`Dyract.Storage` uses SQLite behind `ILocalStore` with an append-only schema migration layer.

Current schema includes local data for:

```text
contacts
conversations
messages
outbox
migration metadata
```

Schema v2 adds separately encrypted per-contact issued-capability state.

User-content fields are encrypted above SQLite with AES-256-GCM. The independent local-data encryption key is stored through MAUI `SecureStorage`; it is deliberately separate from the long-term identity key.

Encrypted fields include:

- local contact display names;
- received contact capabilities;
- issued/granted contact capabilities;
- text message payloads;
- outbox error details.

Peer IDs, timestamps and operational row metadata are not currently hidden by full-database encryption. That distinction is intentional and documented rather than claiming the complete SQLite file is opaque.

Outgoing text queueing is transactional:

```text
user presses Send
       |
       +-- encrypt message body
       +-- INSERT message state = Queued
       +-- INSERT outbox item
       +-- update conversation activity
       |
       +-- COMMIT
             |
             +-- transport may attempt delivery
```

A crash before the transaction commits leaves no half-message. A crash after the commit leaves the message safely queued locally.

See [`docs/local-storage-migrations.md`](docs/local-storage-migrations.md) and [`docs/reliable-messaging.md`](docs/reliable-messaging.md).

### Transport boundary

`Dyract.Transport` defines replaceable transport contracts:

```text
IPeerTransport
IPeerConnection
```

It also defines `DirectOnly` and `AllowRelay`, validates resolved leases/candidates client-side and refuses relay-only connectivity in `DirectOnly` mode.

The isolated FsWebRTC Android experiment currently proves at compile/harness level:

- native PeerConnection setup;
- STUN/ICE configuration;
- offer/answer/trickle signaling;
- DataChannel OPEN gating;
- bounded binary frames;
- authenticated Dyract session handshake;
- encrypted `DYRT` ping/pong;
- encrypted `DYRM` message/ACK probe;
- privacy-safe ICE-path diagnostics.

FsWebRTC is **not** a shipping-app dependency yet. Its Android native library still triggers `XA0141` for Android's 16 KiB page-size requirement, and real-device connectivity evidence is still required before promotion.

See [`docs/transport-spike.md`](docs/transport-spike.md).

## Contact and pairing flow

Identity exchange and reachability authorization are deliberately separate.

```text
Alice                                      Bob
  |                                         |
  | ---- dyract://contact/v1/... ---------> |
  | <--- dyract://contact/v1/... ---------- |
  |                                         |
  | both pin the other's public identity    |
  |                                         |
  | ---- Alice-signed pair response ------> |
  | <--- Bob-signed pair response ----------|
  |                                         |
  | each response is bound to its grantee   |
```

For example, Bob's pairing response for Alice means:

```text
issuer   = Bob
grantee  = Alice
```

Alice can store it and use it to resolve Bob and send Bob connection-negotiation signals. Copying that response to Charlie does not authorize Charlie because Charlie cannot satisfy the grantee identity/signature checks.

If Bob later revokes that issued capability, the directory rejects it for both resolve and new signaling. Revocation does not delete the contact and cannot retroactively erase endpoint information already learned while the grant was valid.

The directory never needs an `Alice is friends with Bob` table.

## Repository layout

```text
Dyract.slnx                 core/server/storage/transport/test solution
Dyract.Mobile.slnx          MAUI/mobile solution
Dyract.TransportSpike.slnx  isolated FsWebRTC experiment/harness

src/
  Dyract.App/               .NET MAUI Android/iOS client
  Dyract.Client/            directory/signaling client, invitation/capability helpers
  Dyract.Core/              identity/domain primitives
  Dyract.Crypto/            identity/session cryptography
  Dyract.Protocol/          versioned contracts and canonical signed payloads
  Dyract.Server/            identity, presence, discovery and signaling service
  Dyract.Storage/           encrypted local SQLite repositories/outbox
  Dyract.Transport/         replaceable peer transport contracts/safety boundary

experiments/
  Dyract.Transport.FsWebRtcProbe/
  Dyract.Transport.AndroidHarness/

tests/
  Dyract.Tests/
```

## Build core/server/tests

Requirements: .NET 10 SDK.

```bash
dotnet restore Dyract.slnx
dotnet build Dyract.slnx
dotnet test Dyract.slnx
```

## Build mobile

Android:

```bash
dotnet workload install maui-android
```

Install the Android SDK components and accept their licenses. Substitute the
paths for the local Android SDK and JDK installation:

```powershell
dotnet build .\src\Dyract.App\Dyract.App.csproj -t:InstallAndroidDependencies -f net10.0-android `
  -p:AndroidSdkDirectory=C:\dev\android-sdk `
  -p:JavaSdkDirectory=C:\dev\jdk `
  -p:AcceptAndroidSdkLicenses=True
```

Build the mobile solution with the same paths:

```powershell
dotnet build .\Dyract.Mobile.slnx `
  -p:AndroidSdkDirectory=C:\dev\android-sdk `
  -p:JavaSdkDirectory=C:\dev\jdk
```

Do not pass `-f net10.0-android` when building `Dyract.Mobile.slnx`: its shared
libraries target `net10.0`. `JavaSdkDirectory` is an MSBuild property; setting
`JAVA_HOME` alone does not configure this build. `setx` only affects PowerShell
sessions opened after the command.

iOS simulator on macOS:

```bash
dotnet workload install maui-ios maui-android
dotnet build src/Dyract.App/Dyract.App.csproj \
  -f net10.0-ios \
  -p:RuntimeIdentifier=iossimulator-arm64 \
  --configuration Release
```

GitHub Actions currently validates:

- core build/tests;
- PostgreSQL 18 migrations/revocations;
- Redis 8 transient-state semantics;
- Android Release build;
- iOS simulator Release build on macOS 26 / Xcode 26.6;
- FsWebRTC Android probe/harness build.

## Run the directory

Zero-setup development uses in-memory implementations:

```bash
dotnet run --project src/Dyract.Server
```

Optional durable PostgreSQL metadata:

```text
ConnectionStrings__Dyract=Host=localhost;Port=5432;Database=dyract;Username=dyract;Password=...
```

Optional shared transient Redis state:

```text
ConnectionStrings__Redis=localhost:6379,abortConnect=true
```

For horizontally scaled production deployment, both durable PostgreSQL and shared Redis are expected; TLS/authentication/secrets/network policy still need deployment-specific hardening.

## API

```text
GET  /health
POST /api/v1/identity/challenge
POST /api/v1/identity/register
POST /api/v1/peer/lookup
POST /api/v1/presence
POST /api/v1/presence/remove
POST /api/v1/capability/revoke
POST /api/v1/peer/resolve
POST /api/v1/signal/send
POST /api/v1/signal/fetch
POST /api/v1/signal/ack
```

Initial limits:

```text
registration endpoints    30 requests/minute/client address
peer operations          240 requests/minute/client address
maximum request body      64 KiB
presence lease             2 minutes
replay marker              5 minutes
signal payload            32 KiB UTF-8
signal TTL                60 seconds
pending signals/target    64
signal fetch batch        20
```

The current ASP.NET rate limiter is process-local and is only a first-line abuse control, not a complete distributed DDoS strategy.

## Security boundaries still open

Before public production use Dyract still requires, at minimum:

1. formal STRIDE + metadata/privacy threat modeling;
2. independent cryptographic review of the peer session/key schedule;
3. protocol fuzz/property/replay/downgrade testing;
4. non-exportable platform identity-key / Secure Enclave evaluation;
5. explicit encrypted identity recovery/export design;
6. physical Android/iPhone ICE/STUN/TURN validation;
7. resolution of the FsWebRTC Android 16 KiB native-library blocker or selection of another transport binding;
8. APNs/FCM wake-up design and metadata review;
9. PostgreSQL backup/retention/recovery policy;
10. production Redis TLS/authentication/network policy;
11. distributed/global abuse controls and privacy-aware observability;
12. mobile secure-storage review;
13. independent application penetration/security testing;
14. SBOM/dependency automation.

## Next technical milestone

The concrete transport is now gated by physical evidence rather than missing directory infrastructure. The next major milestone is to run the Android FsWebRTC harness on two real devices, starting on the same Wi-Fi, then across NAT/cellular/IPv6 transitions. Only after those results should a transport implementation be promoted behind the shipping app's `Dyract.Transport` abstraction.

In parallel, platform-independent work can continue on recovery UX, threat/fuzz testing, production Redis/security policy, SBOM automation and wake-up infrastructure.

See [`docs/transport-spike.md`](docs/transport-spike.md), [`docs/redis-transient-state.md`](docs/redis-transient-state.md), [plan.md](plan.md) and [faq.md](faq.md). Mobile-specific notes are in [`src/Dyract.App/README.md`](src/Dyract.App/README.md).

## Name

**Dyract** plays on *direct*, *communication* and *peer*: communication should happen as directly as the network permits, while the central service stays outside the conversation itself.

## License

No license has been selected yet. Until one is added, normal copyright rules apply.
