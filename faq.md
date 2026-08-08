# Dyract FAQ

## What is Dyract?

Dyract is a direct-first messenger for Android and iPhone. Each installation owns a cryptographic identity. Contacts, user-assigned names, conversations and message bodies are stored locally; the central directory is limited to identity registration and short-lived reachability/signaling metadata.

Dyract is still an experimental implementation and is not production-ready.

## Is Dyract completely serverless?

No. Practical mobile peer-to-peer communication still needs infrastructure for identity registration, NAT traversal/signaling and, later, best-effort mobile wake-up. The architectural goal is to keep that infrastructure **outside the conversation data store and outside the plaintext message path**.

For production-style multi-instance operation, current Dyract can use:

```text
PostgreSQL  durable identity + capability-revocation metadata
Redis       short-lived registration/replay/presence/signaling state
```

Neither is a central chat-history store.

## Why not simply store `GUID -> IP address`?

Because a phone's IP address is neither stable nor necessarily reachable. Mobile devices frequently sit behind NAT/CGNAT, switch between Wi-Fi and cellular networks, change IPv4/IPv6 addresses and lose NAT mappings. Dyract therefore uses signed, short-lived connection candidates and targets ICE/STUN/TURN for actual traversal.

## Is the Peer ID a secret?

No. A `dyr_...` Peer ID is an address. Authentication comes from proving possession of the corresponding private key.

## Why is the Peer ID derived from the public key?

It binds the address to the cryptographic identity without requiring the server to allocate an account number. If public-key material changes, the derived Peer ID also changes rather than silently continuing the old identity.

## Does Dyract still use GUIDs/UUIDs?

Yes for internal identifiers such as messages/challenges/sessions where appropriate. They are not the root user identity. The public peer identity remains the key-derived `dyr_...` value.

## What does the directory know?

The intended server-side data is deliberately narrow.

Durable PostgreSQL metadata can include:

- Peer ID;
- public identity key;
- registration timestamp;
- revoked capability issuer + opaque capability ID + natural expiry;
- migration metadata.

Short-lived Redis state can include:

- registration challenge state for at most two minutes;
- replay markers for about five minutes;
- active reachability candidates for at most two minutes;
- WebRTC signaling envelopes for at most 60 seconds.

The revocation schema deliberately does not store the grantee/contact relationship.

The directory does **not** need user-assigned contact names, address books, conversation bodies, message history or attachments.

See `docs/threat-model.md` for the complete metadata inventory.

## Can the server see IP addresses?

Yes. Any service a client connects to can observe the source network address of that connection, and a reachability service necessarily handles some candidate/network metadata. Dyract's privacy goal is minimization and short retention, not the false claim that infrastructure can never observe network metadata.

Direct peers will also generally learn one another's reachable network address. Hiding peer IPs requires relay-only routing, which is a different privacy/connectivity trade-off.

## Can any registered user resolve another peer's endpoint?

No.

`/api/v1/peer/lookup` returns identity/public-key information only.

`/api/v1/peer/resolve` requires:

1. a fresh signed request from the requester; and
2. a valid capability signed by the target specifically for that requester.

Knowing someone's Peer ID is not sufficient to retrieve their current reachability candidates.

## What is the difference between a contact invitation and a pairing response?

They have intentionally different security meanings.

A contact invitation:

```text
dyract://contact/v1/...
```

contains the peer identity/public key. Importing it lets you **pin who that contact is**.

A pairing response:

```text
dyract://pair/v1/...
```

contains a target-signed, grantee-bound contact capability. Importing it lets you **resolve that target's short-lived reachability and send connection signaling** until the capability expires or is revoked.

Identity pinning therefore does not automatically grant endpoint access.

## Why is pairing reciprocal?

Permission is directional. If Bob signs a capability for Alice, Alice may resolve/signal Bob. That does not automatically mean Bob may resolve Alice.

For both peers to resolve each other, each side issues one signed pairing response for the other side.

## Can someone copy my pairing response to another person?

A response is bound to a specific `GranteePeerId`. If Bob creates a response for Alice and Charlie copies it, Charlie still cannot use it because server verification requires Alice's grantee identity and Alice's fresh signed request.

## How long does pairing authorization last?

The current mobile app creates pairing capabilities with a 30-day default lifetime. The protocol/server enforces a 90-day maximum lifetime.

The issuing device tracks its current grant separately and can revoke it before natural expiry through the signed `/api/v1/capability/revoke` endpoint.

## What does capability revocation do?

A revoked capability is rejected for both:

```text
/api/v1/peer/resolve
/api/v1/signal/send
```

When PostgreSQL is configured, revocation survives process restart and is shared by all directory instances using that database.

The persistent revocation record contains only:

```text
issuer PeerId
opaque capability ID
natural expiry
```

It does not store the grantee/contact relation.

Revocation cannot erase endpoint information already learned while the grant was valid, and it does not automatically terminate an already-established authenticated peer session.

## How long does presence remain on the server?

At most two minutes; the directory client currently uses a short renewable lease. Publishing again replaces the previous lease, and a peer can explicitly remove presence.

When Redis is configured, presence is TTL-backed and shared across directory instances. Without Redis, the same interface uses an in-memory development implementation.

## What candidate types are supported by the current directory contract?

Up to eight candidates:

```text
kind      host | srflx | relay
protocol  udp | tcp
address   IPv4 | IPv6
port      1..65535
```

Loopback, unspecified, broadcast and multicast addresses are rejected by the server. `Dyract.Transport` performs equivalent client-side validation before a transport attempt.

The candidate contract is a bootstrap representation; the concrete ICE integration may evolve it.

## Why does Dyract use Redis?

Redis provides shared TTL state for server data that must not become durable history but must work across multiple directory instances:

```text
registration challenges
replay nonces
presence leases
WebRTC signaling
```

When Redis is explicitly configured, Dyract fails startup if it cannot connect rather than silently falling back to weaker process-local semantics.

Redis is not used for chat messages.

## Does Redis expose Peer IDs in every key name?

No. Replay, presence and signaling key names use SHA-256-derived tokens instead of raw Peer IDs. Registration challenge keys contain only a random challenge ID.

The short-lived values can still contain identity/reachability data required to perform the operation; hashing key names is metadata minimization, not encryption of the Redis dataset.

## Does Dyract already have an Android/iPhone app?

Yes. The .NET MAUI application currently includes:

- secure first-run identity creation/loading;
- Peer ID and fingerprint display;
- contact invitation copy/import/QR display/QR scan;
- reciprocal pairing-response copy/import/QR display/QR scan;
- encrypted local contact/capability storage;
- separate incoming/outgoing grant state;
- grant revocation UX;
- contact list;
- conversation view;
- locally queued text messages;
- HTTPS directory configuration;
- identity registration;
- paired-contact reachability checks.

Both shipping targets compile in Release CI:

```text
Android  warning-clean

net10.0-ios / iossimulator-arm64
macOS 26 + Xcode 26.6
warning-clean
```

Physical-device iPhone/Android camera, SecureStorage and transport behavior still needs validation.

## Are messages sent peer-to-peer in the shipping app yet?

Not yet.

The shipping app performs the reliability-critical local step: text is encrypted and committed to the local message table together with an outbox row in one SQLite transaction.

The repository also contains an **experimental Android FsWebRTC harness** that can perform the Dyract authenticated session handshake and encrypted `DYRM` message/ACK probe over a DataChannel. That transport has deliberately not been promoted into the shipping app until physical connectivity evidence and the Android native-library blocker are resolved.

## Why store the message before trying the network?

It prevents a transient network failure, process crash or OS suspension from silently losing the user's message.

Conceptually:

```text
Send
  -> encrypt payload
  -> INSERT message = Queued
  -> INSERT outbox row
  -> COMMIT
  -> only then attempt transport
```

The reliable messaging algorithm can retry the exact same MessageId after reconnect/restart.

## Are messages stored in plaintext in SQLite?

No. The current local store encrypts user-content fields with AES-256-GCM before writing them to SQLite.

Encrypted content includes:

- local contact display names;
- received contact capabilities;
- issued/granted contact capabilities;
- text message bodies;
- outbox error details.

The encryption key is a separate random 256-bit key kept through MAUI `SecureStorage`.

## Is the entire SQLite file encrypted?

No. Dyract currently uses **field-level authenticated encryption**, not full-file SQLCipher-style encryption. Operational metadata such as Peer IDs, row relationships and timestamps can still be visible in the SQLite file.

That distinction is intentional and is listed in the threat model rather than being hidden behind a broad "encrypted database" claim.

## Does local storage support schema upgrades?

Yes. `Dyract.Storage` now has an append-only migration ledger.

The real v1 -> v2 migration adds encrypted per-contact issued-capability state and has tests proving existing encrypted contact data remains readable. Malformed or future schema versions fail closed.

See `docs/local-storage-migrations.md`.

## Why use a separate local-data encryption key instead of the identity key?

The keys have different purposes and lifecycle requirements. The identity key proves who the peer is; the local-data key protects device-resident content. Keeping them separate allows future recovery/rotation policies without reusing one cryptographic secret across unrelated roles.

## What happens if the wrong local encryption key is used?

AES-GCM authentication fails rather than returning plausible corrupted plaintext. Automated tests verify that reopening the same local database with a different key raises an authentication failure.

## Where is the identity private key stored?

The current MAUI implementation stores exportable PKCS#8 identity material through `SecureStorage.Default`. On Android this uses Keystore-backed encrypted storage; on iOS it uses Keychain.

The private key is not stored in SQLite or an ordinary application file.

The current shared crypto implementation still requires the key to be exportable in application memory. Non-exportable platform-native keys/Secure Enclave remain hardening tasks.

## What if secure identity storage becomes unreadable?

Dyract does not silently create a replacement key. Doing so would create a different Peer ID and could misrepresent that installation as a continuation of the old identity. A future recovery/reset UI must make that transition explicit.

## What happens after uninstall/reinstall?

Until explicit recovery exists, a fresh install is defined as a new identity/local-data boundary unless platform-secure storage intentionally preserves that identity.

The implementation accounts for iOS Keychain entries potentially surviving uninstall, while Android application backup is disabled under the current privacy model.

A future recovery feature must be explicit and encrypted. The directory must never possess a recoverable copy of the private key.

## Can I configure my own directory server in the app?

Yes. The current mobile app lets the user configure a directory origin and register the local identity.

For the current security baseline, the URL must be an HTTPS origin such as:

```text
https://directory.example.com/
```

Credentials, paths, query strings and fragments are rejected.

Server deployments can independently configure PostgreSQL and Redis connection strings.

## What happens when the app checks a paired contact's reachability?

Before calling the directory, the app re-validates the stored pairing capability against:

- the pinned contact public key;
- the local grantee Peer ID;
- capability expiry;
- the target signature.

The server also checks the capability scope, signature, lifetime and revocation state plus the requester's fresh signed proof/replay nonce.

After the server responds, the app checks that the returned Peer ID and public key exactly match the locally pinned contact identity before accepting the reachability result.

## Does Dyract have a peer transport implementation?

It has a production-neutral transport abstraction plus an isolated experimental Android implementation/harness.

`Dyract.Transport` defines:

- `IPeerTransport`;
- `IPeerConnection`;
- inbound/outbound connection flow;
- `DirectOnly` and `AllowRelay` modes;
- validated descriptors from directory reachability;
- client-side candidate/lease validation.

The FsWebRTC Android experiment implements enough WebRTC/DataChannel behavior to compile/package and exercise the authenticated Dyract protocol in a diagnostic harness.

It is **not** yet the shipping transport.

## Why is FsWebRTC still experimental?

Two reasons dominate:

1. the physical Wi-Fi/NAT/cellular/IPv6/background matrix still needs real-device evidence;
2. its bundled `libjingle_peerconnection_so.so` currently triggers Android `XA0141` because it does not satisfy the 16 KiB page-size requirement.

The repository therefore keeps FsWebRTC isolated until those issues are resolved or another binding is selected.

## What is DirectOnly mode?

The transport API models two policies:

```text
DirectOnly
AllowRelay
```

In `DirectOnly`, relay candidates are removed and the connection fails if no direct candidate remains. `AllowRelay` can later use TURN when direct ICE cannot create a path.

## Does P2P always mean packets go directly between phones?

No. NAT/firewall conditions can make a direct path impossible. ICE first attempts suitable direct/server-reflexive candidates. TURN can relay encrypted packets when necessary.

A TURN relay is still different from a central message store: it forwards ciphertext transiently rather than keeping chat history. It does, however, observe network metadata.

## Would TURN be able to read messages?

Not if Dyract's application-layer session is used correctly. TURN must not possess the Dyract application-session keys.

Transport encryption alone is deliberately not treated as the complete Dyract identity/E2E security model.

## Is the authenticated encrypted peer-session protocol implemented?

Yes, as transport-independent protocol code and in the Android diagnostic harness.

It currently includes:

- ephemeral P-256 ECDH;
- long-term ECDSA identity signatures;
- pinned PeerId/public-key verification;
- handshake transcript/session/role binding;
- HKDF-SHA256 directional keys;
- AES-256-GCM `DYSE` frames;
- monotonic sequence/replay/out-of-order rejection;
- version/size bounds;
- adversarial unit tests.

This is still **not an audited cryptographic design**. Independent review plus fuzz/property testing remain release gates, and a reviewed Noise/Double-Ratchet-style evolution may still replace/extend the current composition.

## What is implemented for reliable messaging?

The transport-neutral reliability layer already implements/tests:

- transactional message + outbox commit;
- versioned `DYRM` text frames and delivery ACKs;
- durable receive before ACK;
- duplicate receive idempotency;
- changed-content collision rejection;
- duplicate ACK re-emission after lost ACK;
- exact-peer ACK authorization;
- due-only outbox selection;
- deterministic retry of the same MessageId/content;
- ACK-timeout retry and bounded backoff;
- privacy-safe persisted failure codes;
- two-database lost-first-ACK proof.

The shipping mobile scheduler is intentionally not connected to the experimental transport yet.

## What do message states mean?

The intended lifecycle is:

```text
Queued -> Connecting -> Sent -> Delivered -> Read
                     \-> Retry/Failed
```

`Queued` means safely stored locally. It does not mean the recipient received anything.

`Delivered` should only be set after an authenticated peer ACK confirms durable receive on the other device.

## Are offline messages ever uploaded to the directory?

No under the current architecture. They remain in the sender's encrypted local outbox until direct/relay transport can deliver them.

If sender and receiver never have overlapping reachability, delivery cannot complete without introducing some form of asynchronous encrypted mailbox. That would be a deliberate future architecture change, not something the directory silently does today.

## What about Android/iOS background restrictions?

They are not solved by the directory alone. A later phase will add best-effort APNs/FCM wake routing with an opaque wake payload containing no chat content.

Mobile platforms do not guarantee indefinite background execution or guaranteed silent push delivery, so the UI must report delivery state accurately.

## Does the API have abuse protection?

The current server includes separate fixed-window per-client-address limits for registration and peer operations plus a 64 KiB request-body limit and bounded protocol inputs/stores.

The ASP.NET limiter is currently process-local. Production horizontal scaling still requires edge/distributed rate limiting or equivalent global abuse controls plus privacy-aware operational monitoring.

## What happens if PostgreSQL or Redis is unavailable?

If those services are not configured, Dyract intentionally runs the zero-setup in-memory development implementations.

If Redis **is configured**, startup pings it and fails if the service cannot be reached. Runtime Redis/PostgreSQL failures propagate as failures instead of silently weakening replay/authorization/shared-state semantics.

Production deployments therefore need proper HA, authentication, TLS/network policy, monitoring and PostgreSQL backup/recovery.

## Can users search for each other by name, phone number or email?

No global discovery is required by the privacy-first architecture. Contact exchange uses exact cryptographic invitations. Phone numbers and email addresses are not required.

## How do I verify a contact?

A contact invitation includes the public-key material bound to the Peer ID, and the app displays a short fingerprint. The higher-assurance flow is to compare that fingerprint through an independent trusted channel.

Because Peer ID is derived from the key, a changed identity normally results in a different Peer ID rather than a silent key replacement.

## What about multiple devices per person?

Deferred. The current model is one cryptographic identity per installation. Multi-device support would require a master/account identity, signed device certificates, fan-out, synchronization and revocation semantics, and would materially change the threat model.

## What about group chats, calls, bots or cloud backup?

Deferred until secure/reliable one-to-one messaging is proven. Each materially changes the privacy, key-management or delivery model.

## How private is Dyract?

Dyract minimizes central data; it does **not** claim anonymity.

The directory can see authenticated request and connection metadata. Redis can temporarily hold reachability/signaling state. Direct peers generally see one another's network metadata. Future push/TURN providers will see infrastructure-specific metadata. Local SQLite operational metadata is not fully hidden by the current field-encryption design.

Dyract's meaningful privacy property is that central infrastructure is not the owner of contact names, conversation history, message bodies or attachments.

See `docs/threat-model.md` for explicit properties, non-properties and attacker models.

## Is there a formal threat model?

Yes. `docs/threat-model.md` now documents:

- assets and trust boundaries;
- attacker classes;
- STRIDE analysis;
- directory/PostgreSQL/Redis/device metadata inventory;
- expected outcomes for concrete attacks;
- privacy properties and non-properties;
- release-blocking and production-hardening risks;
- security acceptance gates.

It is an internal engineering threat model, not an independent audit.

## Is the current code production-ready?

No.

Significant foundations are implemented and CI-validated — identity, capability-protected discovery, durable revocation, shared Redis transient state, encrypted local storage/migrations, QR onboarding, authenticated peer-session protocol, reliable messaging core, Android/iOS Release builds and an FsWebRTC diagnostic harness.

Production still requires, among other things:

- physical Android/iPhone transport evidence;
- resolution/replacement of the FsWebRTC 16 KiB native-library issue;
- shipping transport/scheduler integration;
- APNs/FCM wake behavior;
- non-exportable identity-key/recovery decisions;
- independent cryptographic review;
- fuzz/property and penetration testing;
- production Redis/PostgreSQL/abuse-control/observability policy;
- SBOM/dependency automation.
