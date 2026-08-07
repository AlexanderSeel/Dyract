# Dyract

**Direct by design.**

Dyract is an experimental privacy-first messenger for Android and iPhone built around direct peer-to-peer communication. Instead of using a central service to hold user profiles, contact lists, conversations, attachments, or message history, each Dyract installation owns a cryptographic identity and stores its data locally.

The central service is intentionally narrow: it authenticates peers and holds only short-lived reachability information needed to establish a connection. Message transport should be direct whenever the network allows it.

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
             local storage               local storage
                 |                           |
                 +====== encrypted P2P =====+

(*) planned
```

A raw IP address is not sufficient for reliable mobile P2P communication. Cellular networks, NAT/CGNAT, Wi-Fi changes and iOS/Android background restrictions require temporary connection candidates and signaling rather than a permanent `PeerId -> IP` table. Dyract therefore uses `PeerId -> authenticated, short-lived reachability information`.

## Current implementation

The repository currently contains the identity/directory foundation plus the first secure presence-discovery slice:

- .NET 10 solution structure
- cryptographic `dyr_...` Peer ID derived from the identity public key
- ECDSA P-256 identity generation/signing using `System.Security.Cryptography`
- challenge/response peer registration
- signed identity lookup
- timestamp and replay-nonce validation
- target-signed contact capabilities bound to a specific grantee
- signed presence publish/update
- signed presence removal
- 1-2 minute presence leases with automatic expiry
- candidate validation and bounded candidate count
- capability-authorized endpoint resolution
- in-memory prototype stores with no permanent endpoint history
- reusable .NET directory client
- unit tests for identity, signed proofs, capabilities and presence expiry
- GitHub Actions restore/build/test on .NET 10

Still planned:

- .NET MAUI Android/iOS application
- secure private-key persistence using Android/iOS platform facilities
- SQLite local contacts/messages/outbox
- QR/contact invitation UX
- ICE/STUN connectivity and signaling
- optional TURN fallback
- authenticated encrypted peer sessions with forward secrecy
- APNs/FCM wake-up
- attachments, acknowledgements and retry scheduler
- PostgreSQL identity persistence and production rate limiting
- independent security review

See [plan.md](plan.md) for the implementation roadmap and [faq.md](faq.md) for design decisions and limitations.

## Solution layout

```text
Dyract.slnx
src/
  Dyract.Core/       domain primitives such as PeerId
  Dyract.Crypto/     identity key handling and signature verification
  Dyract.Protocol/   versioned wire contracts and canonical signed payloads
  Dyract.Client/     directory/capability client used by the future MAUI app
  Dyract.Server/     minimal identity, presence and discovery service
tests/
  Dyract.Tests/      protocol, crypto and presence tests
```

The MAUI project is intentionally deferred until the identity/discovery foundation is stable. This keeps the protocol/server buildable without Android/iOS workloads and lets the difficult networking/security behavior be proven before UI development starts.

## Requirements

- .NET 10 SDK

Later mobile development additionally requires the .NET MAUI workloads and normal Android/iOS toolchains.

## Build

```bash
dotnet restore Dyract.slnx
dotnet build Dyract.slnx
dotnet test Dyract.slnx
```

## Run the directory prototype

```bash
dotnet run --project src/Dyract.Server
```

Current API:

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
4. secure private-key persistence on Android/iOS,
5. encrypted local database design,
6. production rate limits and abuse controls,
7. persistent identity storage,
8. transport fuzzing and replay/downgrade testing,
9. capability revocation/rotation design,
10. a decision on direct-only versus TURN-assisted connectivity.

## Name

**Dyract** plays on *direct*, *communication* and *peer*: communication should happen as directly as the network permits, with the central service kept outside the conversation itself.

## License

No license has been selected yet. Until one is added, normal copyright rules apply.
