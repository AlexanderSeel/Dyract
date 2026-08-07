# Dyract

**Direct by design.**

Dyract is an experimental privacy-first messenger for Android and iPhone built around direct peer-to-peer communication. Instead of using a central service to hold user profiles, contact lists, conversations, attachments, or message history, each Dyract installation owns a cryptographic identity and stores its data locally.

The central service is intentionally narrow: it authenticates registered peers and, in later phases, will provide temporary reachability/signaling information and best-effort wake-up support. Message transport should be direct whenever the network allows it.

> Dyract is currently an early implementation/protocol experiment. The security model and protocol have not been independently audited and must not yet be treated as production-grade cryptography.

## Core principles

- **Device-owned identity** — a peer owns a locally generated private key; the public Peer ID is derived from its public key.
- **Local-first data** — contacts, display names, conversations, messages and attachments belong on participating devices.
- **Minimal directory** — the server is not a chat-history or user-profile service.
- **Authenticated discovery** — knowing a Peer ID alone is not enough to impersonate that peer or make unsigned directory requests.
- **Direct-first transport** — peers should connect directly using ICE/STUN where possible; an encrypted TURN relay can later be an explicit fallback.
- **Store-and-retry** — when the destination cannot be reached, outgoing data remains in the sender's local outbox until another delivery attempt succeeds.
- **No home-grown primitives** — Dyract defines application protocol behavior, not new cryptographic algorithms.

## Architecture

```text
                         Dyract Directory
                    ASP.NET Core / .NET 10
                 +---------------------------+
                 | peer ID + public key      |
                 | temporary presence (*)    |
                 | signaling (*)             |
                 | wake token (*)            |
                 |                           |
                 | NO profiles               |
                 | NO contacts               |
                 | NO message history        |
                 | NO attachments            |
                 +-------------+-------------+
                               |
                      discovery/signaling
                               |
                 +-------------+-------------+
                 |                           |
             Alice device                Bob device
             local storage               local storage
                 |                           |
                 +====== encrypted P2P =====+

(*) planned, not implemented in the first increment
```

A raw IP address is not sufficient for reliable mobile P2P communication. Cellular networks, NAT/CGNAT, Wi-Fi changes and iOS/Android background restrictions require temporary connection candidates and signaling rather than a permanent `PeerId -> IP` table. The design therefore evolves the original idea into `PeerId -> authenticated, short-lived reachability information`.

## Repository status

The first implementation increment establishes the identity and directory-authentication foundation:

- .NET 10 solution structure
- cryptographic Peer ID derived from the identity public key
- ECDSA P-256 identity key generation/signing using platform cryptography
- challenge/response peer registration
- signed lookup requests
- timestamp and replay-nonce validation
- in-memory directory stores for the prototype server
- reusable .NET directory client
- unit tests for identity/proof behavior

Not implemented yet:

- .NET MAUI Android/iOS UI
- SQLite local chat/contact storage
- secure device-key persistence (Android Keystore / iOS Keychain)
- presence leases and connection candidates
- ICE/STUN and P2P DataChannel transport
- TURN fallback
- APNs/FCM wake-up
- contact capability tokens / QR exchange
- encrypted message/session protocol
- attachments and delivery receipts

See [plan.md](plan.md) for the implementation roadmap and [faq.md](faq.md) for design decisions and limitations.

## Solution layout

```text
Dyract.slnx
src/
  Dyract.Core/       domain primitives such as PeerId
  Dyract.Crypto/     identity key handling and signature verification
  Dyract.Protocol/   versioned request/response contracts and signed payloads
  Dyract.Client/     reusable directory client for the future MAUI app
  Dyract.Server/     minimal ASP.NET Core directory service
tests/
  Dyract.Tests/      unit tests for the initial protocol foundation
```

The MAUI project is intentionally not part of increment 1. This keeps the protocol/server foundation buildable without installing Android/iOS workloads and lets networking/security behavior be proven before UI development starts.

## Requirements

- .NET 10 SDK

Later mobile development will additionally require the .NET MAUI workloads and the normal Android/iOS toolchains.

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

The prototype exposes:

```text
GET  /health
POST /api/v1/identity/challenge
POST /api/v1/identity/register
POST /api/v1/peer/lookup
```

### Registration flow

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

### Signed lookup flow

Only an already registered requester can perform a lookup. The request is signed with the requester's identity key and contains a short-lived timestamp plus a random nonce. The prototype lookup returns the target's identity/public key only; endpoint/presence disclosure is intentionally deferred until contact-capability authorization is implemented.

## Security notes

The first implementation uses **ECDSA P-256 + SHA-256** from `System.Security.Cryptography` because it is available across the .NET targets and uses platform cryptographic implementations. Session encryption and forward secrecy are separate protocol concerns and are not implemented yet.

Before production use the project still requires, at minimum:

1. a formal threat model,
2. protocol review,
3. independent cryptographic/security review,
4. secure private-key persistence on Android/iOS,
5. encrypted local database design,
6. abuse/rate-limit controls,
7. transport fuzzing and replay/downgrade testing,
8. a decision on direct-only versus TURN-assisted connectivity.

## Name

**Dyract** plays on *direct*, *communication* and *peer*: communication should happen as directly as the network permits, with the central service kept outside the conversation itself.

## License

No license has been selected yet. Until one is added, normal copyright rules apply.
