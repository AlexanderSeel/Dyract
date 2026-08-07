# Dyract peer signaling

## Purpose

Dyract signaling exists only to establish or tear down a direct peer transport session. It is **not** a chat mailbox, offline-message queue, profile store or contact graph.

The current endpoints are:

```text
POST /api/v1/signal/send
POST /api/v1/signal/fetch
POST /api/v1/signal/ack
```

The signaling layer is transport-agnostic. Payloads are opaque strings to the directory so a concrete ICE/WebRTC implementation can carry offer/answer and trickle-candidate material without coupling the server to one WebRTC library.

## Supported signal types

```text
offer
answer
candidate
end-of-candidates
close
```

Arbitrary application/message types are rejected.

## Authorization model

### Send

Alice may send a negotiation signal to Bob only when all of the following hold:

1. Alice is a registered Dyract peer.
2. Bob is a registered Dyract peer.
3. Alice signs the exact signal request with Alice's identity key.
4. The request has a fresh timestamp and replay nonce.
5. Alice presents a valid Bob-issued `ContactCapability` whose grantee is Alice.
6. The capability is still valid and Bob's signature verifies against Bob's registered public key.

Therefore knowing Bob's Peer ID does not allow an arbitrary registered peer to place negotiation data in Bob's signaling inbox.

### Fetch

Only Bob can fetch Bob's inbox. Fetch requires a fresh request signed with Bob's identity private key.

### ACK

Only Bob can acknowledge/delete entries in Bob's inbox. ACK also requires a fresh Bob-signed request.

Signal IDs are target-scoped in the server store, so possessing another peer's signal ID does not allow deletion from that other peer's inbox.

## Signed send proof

The sender signature covers, in canonical form:

```text
sender PeerId
target PeerId
capability ID
session ID
signal type
SHA-256(signal payload)
signal expiry
request timestamp
request nonce
```

Hashing the payload into the canonical proof prevents payload substitution without forcing arbitrary SDP/ICE text directly into the line-oriented proof representation.

## Bounds

Current prototype limits:

```text
signal lifetime           <= 60 seconds
client default lifetime    45 seconds
signal payload             <= 32 KiB UTF-8
pending signals / target   <= 64
fetch batch                <= 20
ACK batch                  <= 20 unique IDs
session ID                 128-bit hexadecimal
signal ID                  random 128-bit hexadecimal
```

The global API request limit remains 64 KiB.

These limits make signaling intentionally unsuitable for storing normal chat history or attachments.

## Fetch / ACK semantics

Fetch is non-destructive:

```text
Bob -> fetch
       receives offer/candidate
       processes it locally
Bob -> ACK signal ID
       server removes it
```

This is deliberate. Deleting on fetch could lose a negotiation signal if the mobile process is suspended or the transport adapter throws immediately after the HTTP response.

Unacknowledged signals disappear automatically at expiry.

## Store characteristics

The current prototype uses `SignalStore`, an in-memory bounded TTL store.

Properties:

- no disk persistence;
- no PostgreSQL signaling table;
- purge on store operations;
- per-target bounded inbox;
- ordered fetch by creation time and signal ID;
- explicit target-scoped ACK.

A horizontally scaled deployment can later use Redis or another TTL-capable ephemeral system while preserving the same semantics.

## Mobile integration

The shared client exposes `PeerSignalingClient`:

```text
SendAsync
FetchAsync
AckAsync
CreateSessionId
```

The MAUI application wraps it with `IDirectorySignalingService`.

Before sending to a contact, the MAUI adapter re-verifies the locally stored pairing capability against:

- the contact's pinned public key,
- the local Peer ID as grantee,
- capability signature and expiry.

The future transport implementation should depend on the signaling abstraction rather than instantiate arbitrary `HttpClient` requests itself.

## Expected ICE/DataChannel flow

```text
Alice                                   Bob
  |                                      |
  | create session ID                    |
  | create local WebRTC offer            |
  |                                      |
  | -- signed offer signal ------------> |
  |                                      | fetch + process
  |                                      | ACK offer
  |                                      | create answer
  | <----------- signed answer signal -- |
  | process + ACK                        |
  |                                      |
  | <------ trickle ICE candidates ----> |
  |                                      |
  | ------- end-of-candidates ---------> |
  | <-------- end-of-candidates -------- |
  |                                      |
  | ======== DataChannel opens ========= |
```

The actual native/managed WebRTC APIs are adapter details behind `IPeerTransport`.

## Security boundary

Successful signaling does **not** prove a secure Dyract chat session.

The later peer-session layer must independently provide:

- authentication against the pinned Dyract identities;
- ephemeral key agreement;
- forward secrecy;
- transcript binding;
- version/downgrade protection;
- sequence/replay protection.

WebRTC/DTLS transport encryption is useful transport protection but must not silently replace Dyract's own identity/session guarantees.

## Things signaling must never become

Do not add:

- chat message bodies as a new signal type;
- attachments or attachment chunks;
- user display names/profiles;
- persistent conversation IDs/history;
- multi-day signal lifetimes;
- server-side friendship/contact records.

If a future product decision introduces an asynchronous encrypted mailbox, design it explicitly as a separate subsystem with a separate threat/privacy model rather than stretching this signaling layer into one.
