# Dyract

**Direct by design.**

Dyract is an experimental privacy-first messenger for Android and iPhone built around direct peer-to-peer communication. Each installation owns a cryptographic identity; contacts, local names, conversations and messages remain on the participating devices. The central service is intentionally narrow: identity registration, short-lived reachability, capability-protected connection signaling and later wake-up — not chat history.

> Dyract is still an implementation/protocol experiment. The cryptographic composition and mobile security model have not been independently audited and must not yet be treated as production-grade.

## Core principles

- **Device-owned identity** — a peer owns a locally generated private key; the public `dyr_...` Peer ID is derived from the public key.
- **Local-first data** — contacts, user-assigned names, conversations, message bodies and attachments do not belong in the directory.
- **Minimal directory** — the server does not maintain profiles, a social/contact graph or message history.
- **Pinned identities** — a contact invitation binds a Peer ID to public-key material and the mobile client stores that relationship locally.
- **Explicit reachability authorization** — identity knowledge alone does not reveal an endpoint. A target signs a grantee-bound capability for the exact peer that may resolve/signaling-connect to it.
- **Short-lived presence** — published reachability leases expire within two minutes.
- **Short-lived signaling** — offer/answer/ICE negotiation data expires within 60 seconds and is retained only until target ACK or expiry.
- **Store before send** — an outgoing message is encrypted and committed to SQLite together with its outbox row before network delivery is attempted.
- **Direct-first transport** — ICE/STUN direct connectivity is the target; TURN is an explicit fallback when direct establishment is impossible.
- **No home-grown primitives** — Dyract composes established platform cryptography rather than inventing cryptographic algorithms.

## Architecture

```text
                         Dyract Directory
                    ASP.NET Core / .NET 10
                 +-----------------------------+
                 | Peer ID + public key        |
                 | temporary presence leases   |
                 | capability verification     |
                 | ephemeral ICE signaling     |
                 | wake routing (*)            |
                 |                             |
                 | NO profiles                 |
                 | NO contact graph            |
                 | NO chat history             |
                 | NO attachments              |
                 +--------------+--------------+
                                |
                discovery / negotiation only
                                |
                 +--------------+--------------+
                 |                             |
             Alice device                  Bob device
              .NET MAUI                     .NET MAUI
                 |                             |
        encrypted SQLite             encrypted SQLite
        contacts/messages            contacts/messages
        transactional outbox         transactional outbox
                 |                             |
                 +==== encrypted P2P (*) =====+

(*) concrete transport and wake-up remain in progress/planned
```

A raw `PeerId -> IP` table is not sufficient for mobile networks. NAT/CGNAT, Wi-Fi/cellular transitions and background restrictions require authenticated short-lived reachability plus NAT traversal. Dyract therefore treats an endpoint as an expiring connection candidate set, not a permanent address.

## Current implementation

### Identity, discovery and signaling

- .NET 10 shared/server solution
- ECDSA P-256 identity generation/signing through `System.Security.Cryptography`
- SHA-256-derived `dyr_...` Peer IDs
- challenge/response registration
- signed identity lookup
- request timestamps + replay nonces
- target-signed, grantee-bound contact capabilities
- signed presence publish/update/removal
- max two-minute presence leases
- bounded candidate validation
- capability-protected `/api/v1/peer/resolve`
- capability-protected short-lived signaling send
- target-signed signaling fetch and explicit ACK
- signaling types limited to `offer`, `answer`, `candidate`, `end-of-candidates`, `close`
- signal TTL max 60 seconds
- signal payload max 32 KiB UTF-8
- max 64 pending signals per target and 20 per fetch batch
- async `IIdentityStore`
- zero-setup in-memory identity store
- optional PostgreSQL identity persistence via Npgsql
- request-body limits + per-client API rate limits
- unit and ASP.NET integration coverage

Signaling is intentionally **not a chat mailbox**. It carries only short-lived connection negotiation data. Message bodies remain in the encrypted local outbox until a peer channel can deliver them.

### Mobile client

- .NET MAUI Android/iOS app
- first-run identity generation/loading through MAUI `SecureStorage`
- Peer ID and identity fingerprint display
- versioned `dyract://contact/v1/...` identity invitations
- local contact names stored only on the device
- reciprocal `dyract://pair/v1/...` reachability responses
- imported capability verification against the pinned contact public key and local grantee identity
- 30-day bootstrap pairing capability lifetime
- HTTPS-only directory configuration
- mobile identity registration with the directory
- capability-protected reachability check for paired contacts
- directory-returned public key pinned against the locally saved contact key
- authenticated `IDirectorySignalingService` adapter for future transport implementations
- contact list and conversation screen
- locally queued text messages
- Android cleartext networking disabled
- Android application backup disabled for the current privacy model

### Local storage

`Dyract.Storage` uses SQLite behind `ILocalStore`.

Implemented tables:

```text
contacts
conversations
messages
outbox
schema_info
```

User-content fields are encrypted above SQLite with AES-256-GCM. The independent local-data encryption key is stored through MAUI `SecureStorage`; it is deliberately separate from the long-term identity key.

Currently encrypted fields include:

- local contact display names,
- stored contact capabilities,
- text message payloads,
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
             +-- future transport may attempt delivery
```

A crash before the transaction commits leaves no half-message. A crash after the commit leaves the message safely queued locally.

### Transport boundary

`Dyract.Transport` already defines a replaceable transport contract:

```text
IPeerTransport
  StartAsync
  ConnectAsync
  AcceptAsync

IPeerConnection
  SendAsync
  ReceiveAsync
```

It also defines `DirectOnly` and `AllowRelay`, validates resolved leases/candidates client-side and refuses relay-only connectivity in `DirectOnly` mode.

No WebRTC/ICE library is a production dependency yet. See [`docs/transport-spike.md`](docs/transport-spike.md) for candidate analysis and physical-device exit criteria.

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

The directory does not need an `Alice is friends with Bob` table.

## Repository layout

```text
Dyract.slnx            workload-free core/server/storage/transport/test solution
Dyract.Mobile.slnx     MAUI/mobile development solution

src/
  Dyract.App/          .NET MAUI Android/iOS client
  Dyract.Client/       directory/signaling client, invitation/capability helpers
  Dyract.Core/         identity/domain primitives
  Dyract.Crypto/       identity cryptography/signature verification
  Dyract.Protocol/     versioned contracts and canonical signed payloads
  Dyract.Server/       identity, presence, discovery and signaling service
  Dyract.Storage/      encrypted local SQLite repositories/outbox
  Dyract.Transport/    replaceable peer transport contracts/safety boundary

tests/
  Dyract.Tests/        unit + ASP.NET integration tests
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
dotnet build src/Dyract.App/Dyract.App.csproj -f net10.0-android
```

iOS on macOS:

```bash
dotnet workload install maui-ios
dotnet build src/Dyract.App/Dyract.App.csproj -f net10.0-ios
```

The Android Release build is validated in GitHub Actions. iOS project/platform files are present, but iOS still needs a macOS CI gate and device-level validation.

## Run the directory

Without a connection string the server uses its in-memory identity store:

```bash
dotnet run --project src/Dyract.Server
```

Optional PostgreSQL identity persistence uses the standard ASP.NET Core connection string named `Dyract`:

```text
ConnectionStrings__Dyract=Host=localhost;Port=5432;Database=dyract;Username=dyract;Password=...
```

The prototype currently creates the `peer_identity` table automatically. Production deployment should replace this bootstrap behavior with explicit migrations.

Presence, challenges, replay nonces and signaling remain ephemeral even when PostgreSQL is enabled.

## API

```text
GET  /health
POST /api/v1/identity/challenge
POST /api/v1/identity/register
POST /api/v1/peer/lookup
POST /api/v1/presence
POST /api/v1/presence/remove
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
signal payload            32 KiB UTF-8
signal TTL                60 seconds
pending signals/target    64
signal fetch batch        20
```

These are first-line abuse controls, not a complete DDoS strategy.

## Security boundaries still open

Before production use Dyract still requires, at minimum:

1. a formal threat model,
2. independent review of the handshake/key schedule once peer sessions exist,
3. non-exportable platform identity-key evaluation where practical,
4. explicit encrypted identity recovery/export design,
5. capability revocation/rotation beyond expiry,
6. ICE/STUN/TURN transport validation on physical Android/iPhone devices,
7. transport protocol fuzzing/replay/downgrade testing,
8. APNs/FCM wake-up design and metadata review,
9. explicit PostgreSQL migrations/backup/retention policy,
10. broader abuse/DDoS controls and privacy-aware observability,
11. mobile secure-storage review,
12. independent application penetration/security testing.

## Next technical milestone

The next major implementation is the concrete `Dyract.Transport` spike: establish a real data-only peer channel using the now-implemented Dyract reachability + signaling layers. The first candidate experiment is native WebRTC/ICE behind the existing transport abstraction, with Android↔Android followed by Android↔iPhone physical-device testing before any library becomes a production commitment.

See [`docs/transport-spike.md`](docs/transport-spike.md), [plan.md](plan.md) and [faq.md](faq.md). Mobile-specific notes are in [`src/Dyract.App/README.md`](src/Dyract.App/README.md).

## Name

**Dyract** plays on *direct*, *communication* and *peer*: communication should happen as directly as the network permits, while the central service stays outside the conversation itself.

## License

No license has been selected yet. Until one is added, normal copyright rules apply.
