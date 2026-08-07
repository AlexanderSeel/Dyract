# Dyract

**Direct by design.**

Dyract is an experimental privacy-first messenger for Android and iPhone built around direct peer-to-peer communication. Instead of using a central service to hold user profiles, contact lists, conversations, attachments, or message history, each Dyract installation owns a cryptographic identity and stores its data locally.

The central service is intentionally narrow: it authenticates peers and holds only the identity/reachability metadata needed to establish a connection. Message transport should be direct whenever the network allows it.

> Dyract is currently an early implementation/protocol experiment. The security model and protocol have not been independently audited and must not yet be treated as production-grade cryptography.

## Core principles

- **Device-owned identity** — a peer owns a locally generated private key; the public Peer ID is derived from its public key.
- **Local-first data** — contacts, display names, conversations, messages and attachments belong on participating devices.
- **Minimal directory** — the server is not a chat-history or user-profile service.
- **Authenticated discovery** — knowing a Peer ID alone is not enough to impersonate that peer or retrieve its current endpoint.
- **Capability-protected reachability** — a target peer explicitly signs permission for a specific peer to resolve its temporary presence.
- **Short-lived presence** — addresses/candidates expire within two minutes and are kept only in the ephemeral presence store.
- **Direct-first transport** — peers should connect directly using ICE/STUN where possible; an encrypted TURN relay can later be an explicit fallback.
- **Store-and-retry** — when the destination cannot be reached, outgoing data remains in the sender's local outbox until another delivery attempt succeeds.
- **No home-grown primitives** — Dyract defines application protocol behavior, not new cryptographic algorithms.

## Architecture

```text
                         Dyract Directory
                    ASP.NET Core / .NET 10
                 +----------------------------+
                 | peer ID + public key       |
                 | temporary presence leases  |
                 | capability verification    |
                 | signaling (*)              |
                 | wake token (*)             |
                 |                            |
                 | NO profiles                |
                 | NO contact graph           |
                 | NO message history         |
                 | NO attachments             |
                 +-------------+--------------+
                               |
                 authenticated discovery only
                               |
                 +-------------+-------------+
                 |                           |
             Alice device                Bob device
              .NET MAUI                   .NET MAUI
             local storage               local storage
                 |                           |
                 +====== encrypted P2P =====+

(*) planned
```

A raw IP address is not sufficient for reliable mobile P2P communication. Cellular networks, NAT/CGNAT, Wi-Fi changes and iOS/Android background restrictions require temporary connection candidates and signaling rather than a permanent `PeerId -> IP` table. Dyract therefore uses `PeerId -> authenticated, short-lived reachability information`.

## Current implementation

The repository currently contains the identity/directory foundation, secure presence discovery, server hardening and the first mobile client slice:

- .NET 10 shared/server solution
- cryptographic `dyr_...` Peer ID derived from the identity public key
- ECDSA P-256 identity generation/signing using `System.Security.Cryptography`
- challenge/response peer registration
- signed identity lookup
- timestamp and replay-nonce validation
- target-signed contact capabilities bound to a specific grantee
- signed presence publish/update and removal
- maximum two-minute presence leases with automatic expiry
- candidate validation and bounded candidate count
- capability-authorized endpoint resolution
- async identity persistence abstraction
- in-memory identity store for zero-setup local/test use
- optional PostgreSQL identity persistence through Npgsql
- per-client registration and peer-operation rate limits
- 64 KiB maximum request-body protection
- reusable .NET directory client
- unit and ASP.NET integration tests, including authorized/unauthorized resolve cases
- .NET MAUI Android/iOS project and mobile solution
- first-run mobile identity creation/loading through MAUI `SecureStorage`
- Peer ID display/copy screen
- Android cleartext networking disabled and app backup disabled
- GitHub Actions restore/build/test for the workload-free .NET solution

Still planned:

- local contact/conversation/message/outbox storage
- QR/contact invitation UX
- directory registration from the mobile app
- non-exportable platform identity-key hardening / Secure Enclave evaluation
- ICE/STUN connectivity and signaling
- optional TURN fallback
- authenticated encrypted peer sessions with forward secrecy
- APNs/FCM wake-up
- attachments, acknowledgements and retry scheduler
- Redis-backed ephemeral presence/signaling for horizontal scaling
- PostgreSQL migration tooling rather than prototype schema bootstrap
- production-grade abuse controls/observability
- independent security review

See [plan.md](plan.md) for the implementation roadmap and [faq.md](faq.md) for design decisions and limitations. Mobile-specific notes are in [`src/Dyract.App/README.md`](src/Dyract.App/README.md).

## Solution layout

```text
Dyract.slnx            server/core/test solution; no mobile workload required
Dyract.Mobile.slnx     MAUI/mobile development solution

src/
  Dyract.App/        .NET MAUI Android/iOS client
  Dyract.Core/       domain primitives such as PeerId
  Dyract.Crypto/     identity key handling and signature verification
  Dyract.Protocol/   versioned wire contracts and canonical signed payloads
  Dyract.Client/     directory/capability client used by the MAUI app
  Dyract.Server/     identity, presence and discovery service
tests/
  Dyract.Tests/      unit + ASP.NET integration tests
```

## Requirements

Core/server development:

- .NET 10 SDK
- PostgreSQL only when durable server identity persistence is enabled

Mobile development additionally requires the .NET MAUI Android/iOS workloads and the normal Android/iOS toolchains. Building iOS requires macOS/Xcode.

## Build server/core/tests

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

## Mobile identity bootstrap

The current app does not expose messaging yet. It starts by loading or creating the installation identity and displaying the derived Peer ID.

The private PKCS#8 identity material is kept through MAUI `SecureStorage`. On Android this uses Keystore-backed encrypted storage; on iOS it uses Keychain. Dyract does not silently replace an unreadable stored identity because that would silently change the Peer ID.

The current key is still exportable in application memory. A later hardening phase will evaluate non-exportable platform-native key handles while keeping recovery/export an explicit product decision.

## Run the directory prototype

Without a database connection string the server uses its in-memory identity store:

```bash
dotnet run --project src/Dyract.Server
```

To persist peer identities in PostgreSQL, configure the standard ASP.NET Core connection string named `Dyract`, for example through an environment variable:

```text
ConnectionStrings__Dyract=Host=localhost;Port=5432;Database=dyract;Username=dyract;Password=...
```

The prototype creates the `peer_identity` table if it does not exist. This is intentionally bootstrap behavior; production deployment should replace automatic schema creation with explicit database migrations.

Presence, replay nonces and registration challenges remain ephemeral even when PostgreSQL is enabled.

## API limits

The prototype currently applies:

```text
registration endpoints   30 requests / minute / client address
peer operations         240 requests / minute / client address
maximum request body     64 KiB
```

These limits are a first abuse-prevention layer, not a final DDoS strategy.

## Current API

```text
GET  /health
POST /api/v1/identity/challenge
POST /api/v1/identity/register
POST /api/v1/peer/lookup
POST /api/v1/presence
POST /api/v1/presence/remove
POST /api/v1/peer/resolve
```

### Registration

```text
Client                                  Directory
  |                                        |
  | public key --------------------------> |
  | <------- challenge + derived PeerId    |
  |                                        |
  | sign registration proof locally       |
  |                                        |
  | signed registration ----------------> |
  | <-------------------------- registered |
```

### Contact capability

Endpoint resolution is deliberately different from ordinary identity lookup.

Bob can create a capability for Alice:

```text
Issuer:       Bob PeerId
Grantee:      Alice PeerId
CapabilityId: random 128-bit identifier
Issued:       timestamp
Expires:      timestamp
Signature:    Bob identity signature
```

The capability is held by Alice. The directory does not need to persist an `Alice -> Bob` friendship row.

### Presence and resolve

```text
Bob                                      Directory
 |                                           |
 | signed candidates + 90s lease ---------->|
 |                                           |

Alice                                       Directory
 |                                             |
 | signed resolve + Bob capability ---------->|
 |                                             | verify Alice signature
 |                                             | verify Bob capability
 |                                             | verify lease is current
 |<----------- Bob temporary candidates ------|
```

A registered peer that merely knows Bob's Peer ID cannot retrieve Bob's presence candidates.

`/peer/lookup` remains an authenticated identity-key lookup and does **not** return reachability information. `/peer/resolve` is the capability-protected operation that can return temporary candidates.

## Current candidate model

The prototype accepts up to eight candidates per lease:

```text
kind:      host | srflx | relay
protocol:  udp | tcp
address:   IPv4 or IPv6
port:      1..65535
priority:  non-negative integer
```

Loopback, unspecified, multicast and broadcast addresses are rejected. The current model is deliberately small; the later ICE transport layer may evolve the wire representation while keeping the same authorization rules.

## Security notes

Long-term identity signatures currently use **ECDSA P-256 + SHA-256** from `System.Security.Cryptography`. Session encryption and forward secrecy are separate protocol concerns and are not implemented yet.

A contact capability is authorization, not authentication by itself. A resolve operation requires both:

1. a fresh signed request proving possession of the requester's private key, and
2. a valid capability signed by the target identity for that requester.

Before production use the project still requires, at minimum:

1. a formal threat model,
2. protocol review,
3. independent cryptographic/security review,
4. hardened/non-exportable mobile key design where practical,
5. encrypted local database design,
6. stronger abuse/DDoS controls and privacy-aware observability,
7. explicit database migration/backup policy,
8. transport fuzzing and replay/downgrade testing,
9. capability revocation/rotation design,
10. a decision on direct-only versus TURN-assisted connectivity.

## Name

**Dyract** plays on *direct*, *communication* and *peer*: communication should happen as directly as the network permits, with the central service kept outside the conversation itself.

## License

No license has been selected yet. Until one is added, normal copyright rules apply.
