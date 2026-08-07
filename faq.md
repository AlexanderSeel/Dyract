# Dyract FAQ

## What is Dyract?

Dyract is a direct-first messenger for Android and iPhone. Each installation owns a cryptographic identity. Contacts, user-assigned names, conversations and message bodies are stored locally; the central directory is limited to identity registration and short-lived reachability/signaling metadata.

Dyract is still an experimental implementation and is not production-ready.

## Is Dyract completely serverless?

No. Practical mobile peer-to-peer communication still needs infrastructure for identity registration, NAT traversal/signaling and, later, best-effort mobile wake-up. The architectural goal is to keep that infrastructure **outside the conversation data store and outside the plaintext message path**.

## Why not simply store `GUID -> IP address`?

Because a phone's IP address is neither stable nor necessarily reachable. Mobile devices frequently sit behind NAT/CGNAT, switch between Wi-Fi and cellular networks, change IPv4/IPv6 addresses and lose NAT mappings. Dyract therefore uses signed, short-lived connection candidates and will use ICE/STUN/TURN for actual traversal.

## Is the Peer ID a secret?

No. A `dyr_...` Peer ID is an address. Authentication comes from proving possession of the corresponding private key.

## Why is the Peer ID derived from the public key?

It binds the address to the cryptographic identity without requiring the server to allocate an account number. If public-key material changes, the derived Peer ID also changes rather than silently continuing the old identity.

## Does Dyract still use GUIDs?

GUID/UUID values are used internally for things such as messages and challenges. They are not the root user identity. Message/conversation identifiers use sortable UUIDv7 where appropriate.

## What does the directory know?

The intended server-side data is deliberately narrow:

- Peer ID,
- public identity key,
- short-lived reachability candidates while online,
- protocol/operational metadata,
- later, minimum push-routing information when wake-up is implemented.

Registered identities can optionally persist in PostgreSQL. Presence, registration challenges and replay nonces remain ephemeral in the current implementation.

The directory does **not** need user-assigned contact names, address books, conversation bodies, message history or attachments.

## Can the server see IP addresses?

Yes. Any service a client connects to can observe the source network address of that connection, and a reachability service necessarily handles some candidate/network metadata. Dyract's privacy goal is minimization and short retention, not the false claim that infrastructure can never observe network metadata.

Direct peers will also generally learn one another's reachable network address. Hiding peer IPs requires relay-only routing, which is a different privacy/connectivity trade-off.

## Can any registered user resolve another peer's endpoint?

No.

`/api/v1/peer/lookup` returns identity/public-key information only.

`/api/v1/peer/resolve` requires:

1. a fresh signed request from the requester, and
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

contains a target-signed, grantee-bound contact capability. Importing it lets you **resolve that target's short-lived reachability** until the capability expires.

Identity pinning therefore does not automatically grant endpoint access.

## Why is pairing reciprocal?

Permission is directional. If Bob signs a capability for Alice, Alice may resolve Bob. That does not automatically mean Bob may resolve Alice.

For both peers to resolve each other, each side issues one signed pairing response for the other side.

## Can someone copy my pairing response to another person?

A response is bound to a specific `GranteePeerId`. If Bob creates a response for Alice and Charlie copies it, Charlie still cannot use it because the server/client verification requires Alice's grantee identity and Alice's fresh signed resolve request.

## How long does the current pairing authorization last?

The current mobile bootstrap creates pairing responses with a 30-day lifetime. This is a prototype choice, not a final product policy.

Explicit revocation before expiry is not implemented yet and remains a production requirement.

## How long does presence remain on the server?

At most two minutes; the directory client currently defaults to a 90-second lease. Publishing again replaces the previous lease, and a peer can explicitly remove presence.

The current presence store is ephemeral. A scaled deployment can move it to a TTL-backed store such as Redis without creating permanent IP history.

## What candidate types are supported by the current directory contract?

Up to eight candidates:

```text
kind      host | srflx | relay
protocol  udp | tcp
address   IPv4 | IPv6
port      1..65535
```

Loopback, unspecified, broadcast and multicast addresses are rejected by the server. `Dyract.Transport` now performs equivalent client-side validation before a transport attempt.

The candidate contract is a bootstrap representation; the concrete ICE implementation may evolve it.

## Does Dyract already have an Android/iPhone app?

Yes, there is now a .NET MAUI application with:

- secure first-run identity creation/loading,
- Peer ID and fingerprint display,
- contact invitation import/copy,
- reciprocal pairing-response import/copy,
- encrypted local contact storage,
- contact list,
- conversation view,
- locally queued text messages,
- HTTPS directory configuration,
- identity registration,
- paired contact reachability checks.

The Android Release build is validated in GitHub Actions. iOS project support is present, but macOS/iOS CI and physical iPhone validation are still required.

## Are messages actually sent peer-to-peer yet?

Not yet. Pressing Send currently performs the important local reliability step first: the text is encrypted and committed to the local message table together with an outbox row in one SQLite transaction.

The concrete ICE/DataChannel implementation is the next major networking phase. Until that is connected, messages remain `Queued` locally.

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

The future outbox worker can retry safely after restart.

## Are messages stored in plaintext in SQLite?

No. The current local store encrypts user-content fields with AES-256-GCM before writing them to SQLite.

Currently encrypted:

- local contact display names,
- stored contact capabilities,
- text message bodies,
- outbox error details.

The encryption key is a separate random 256-bit key kept through MAUI `SecureStorage`.

## Is the entire SQLite file encrypted?

No. Dyract currently uses **field-level authenticated encryption**, not full-file SQLCipher-style encryption. Operational metadata such as Peer IDs, row relationships and timestamps can still be visible in the SQLite file.

That distinction is intentional. A future threat-model review may decide stronger metadata/full-file protection is required.

## Why use a separate local-data encryption key instead of the identity key?

The keys have different purposes and lifecycle requirements. The identity key proves who the peer is; the local-data key protects device-resident content. Keeping them separate allows future recovery/rotation policies without reusing one cryptographic secret across unrelated roles.

## What happens if the wrong local encryption key is used?

AES-GCM authentication fails rather than returning plausible corrupted plaintext. Automated tests explicitly verify that reopening the same local database with a different key raises an authentication failure.

## Where is the identity private key stored?

The current MAUI implementation stores exportable PKCS#8 identity material through `SecureStorage.Default`. On Android this uses Keystore-backed encrypted storage; on iOS it uses Keychain.

The private key is not stored in SQLite or an ordinary application file.

The current shared crypto implementation still requires the key to be exportable in application memory. Non-exportable platform-native keys/Secure Enclave are a later hardening task.

## What if secure identity storage becomes unreadable?

Dyract does not silently create a replacement key. Doing so would silently create a different Peer ID and could misrepresent that installation as a continuation of the old identity. A future recovery/reset UI must make that transition explicit.

## What happens after uninstall/reinstall?

Until explicit recovery exists, a fresh install is defined as a new identity/local-data boundary.

The code accounts for iOS Keychain entries potentially surviving uninstall, while Android application backup is disabled under the current privacy model.

A future recovery feature must be explicit and encrypted. The directory must never possess a recoverable copy of the private key.

## Can I configure my own directory server in the app?

Yes. The current mobile app lets the user configure a directory origin and register the local identity.

For the current security baseline, the URL must be an HTTPS origin such as:

```text
https://directory.example.com/
```

Credentials, paths, query strings and fragments are rejected.

## What happens when the app checks a paired contact's reachability?

Before calling the directory, the app re-validates the stored pairing capability against:

- the pinned contact public key,
- the local grantee Peer ID,
- capability expiry,
- the target signature.

After the server responds, the app also checks that the returned Peer ID and public key exactly match the locally pinned contact identity before accepting the reachability result.

## Does Dyract already have a peer transport implementation?

It has the **transport abstraction and safety boundary**, not a concrete ICE engine yet.

`Dyract.Transport` defines:

- `IPeerTransport`,
- `IPeerConnection`,
- inbound/outbound connection flow,
- `DirectOnly` and `AllowRelay` modes,
- validated `PeerConnectionDescriptor` conversion from directory reachability,
- client-side candidate/lease validation.

The next spike will put a real ICE/DataChannel implementation behind that contract.

## Why not immediately commit to one WebRTC library?

Mobile NAT traversal is one of the highest-risk technical parts of the project. The abstraction lets Dyract test a candidate implementation on physical Android/iPhone devices and replace it if the real connectivity/background behavior is insufficient.

SIPSorcery is currently a candidate for the spike, but it has not been selected as the permanent transport dependency.

## What is DirectOnly mode?

The transport API already models two policies:

```text
DirectOnly
AllowRelay
```

In `DirectOnly`, relay candidates are removed and the connection fails if no direct candidate remains. `AllowRelay` can later use TURN when direct ICE cannot create a path.

## Does P2P always mean packets go directly between phones?

No. NAT/firewall conditions can make a direct path impossible. ICE first attempts suitable direct/server-reflexive candidates. TURN can relay encrypted packets when necessary.

A TURN relay is still different from a central message store: it forwards ciphertext transiently rather than keeping chat history. It does, however, observe network metadata.

## Would TURN be able to read messages?

Dyract should establish its own authenticated end-to-end peer session above the transport. TURN must not possess those application-session keys.

Transport encryption alone must not be treated as the complete Dyract identity/E2E security model.

## Is the encrypted peer-session protocol implemented yet?

No. The next security layer still needs:

- ephemeral key agreement,
- forward secrecy,
- authentication of the handshake transcript with the pinned long-term identities,
- sequence/replay protection,
- version/downgrade protection,
- independent review.

## What do message states mean?

The intended lifecycle is:

```text
Queued -> Connecting -> Sent -> Delivered -> Read
                     \-> Retry/Failed
```

`Queued` means safely stored locally. It does not mean the recipient has received anything.

`Delivered` should only be set after an authenticated peer ACK confirms the message was stored by the other device.

## Are offline messages ever uploaded to the directory?

No under the current architecture. They remain in the sender's encrypted local outbox until direct/relay transport can deliver them.

If the sender and receiver never have overlapping reachability, delivery cannot complete without introducing some form of asynchronous encrypted mailbox, which would be a deliberate future architecture change.

## What about Android/iOS background restrictions?

They are not solved by the directory alone. A later phase will add best-effort APNs/FCM wake routing with an opaque wake payload containing no chat content.

Mobile platforms do not guarantee indefinite background execution or guaranteed silent push delivery, so the UI must report delivery state accurately.

## Does the API have abuse protection?

The current prototype includes separate per-client rate-limit budgets for registration and authenticated peer operations plus a 64 KiB request-body limit.

Production still requires broader DDoS/abuse controls, retention rules and privacy-aware operational monitoring.

## Can users search for each other by name, phone number or email?

No global discovery is required by the privacy-first architecture. Contact exchange uses exact cryptographic invitations. Phone numbers and email addresses are not required.

## How do I verify a contact?

A contact invitation includes the public-key material bound to the Peer ID, and the app displays a short fingerprint. The intended higher-assurance flow is to compare that fingerprint through an independent trusted channel.

Because Peer ID is derived from the key, a changed identity normally results in a different Peer ID rather than a silent key replacement.

## What about multiple devices per person?

Deferred. The current model is one cryptographic identity per installation. Multi-device support would require a master/account identity, signed device certificates, fan-out, synchronization and revocation semantics.

## What about group chats, calls, bots or cloud backup?

Deferred until secure/reliable one-to-one messaging is proven. Each materially changes the privacy, key-management or delivery model.

## How private is Dyract?

Dyract minimizes central data; it does not claim anonymity. The directory sees authenticated requests and connection metadata. Direct peers generally see one another's network metadata. Push providers will later see wake-routing activity. Local SQLite metadata is not fully hidden by the current field-encryption design.

These limitations should remain explicit rather than being obscured by broad "zero knowledge" marketing claims.

## Is the current code production-ready?

No. Significant foundations are now implemented — identity, capability-protected discovery, PostgreSQL option, API hardening, encrypted local storage, reciprocal contact pairing, local transactional outbox, MAUI directory integration and replaceable transport contracts — but production still requires a real ICE/STUN/TURN implementation, authenticated forward-secret peer sessions, ACK/retry delivery, mobile wake-up behavior, migrations/operations, iOS CI/device tests and independent security review.
