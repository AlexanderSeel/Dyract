# Dyract FAQ

## What is Dyract?

Dyract is a direct-first messenger concept for Android and iPhone. Each installation owns a cryptographic identity and stores contacts, conversations, messages and attachments locally. A small central directory helps authenticated peers find one another, but it is not intended to store chat history.

## Is Dyract completely serverless?

No. A practical mobile messenger needs some infrastructure for identity registration, reachability discovery/signaling and, if good background delivery is desired, wake-up routing. Dyract's goal is to keep that infrastructure outside the conversation data path whenever possible.

## Why not simply store `GUID -> IP address` on the server?

Because an IP address is not a stable mobile endpoint. Phones frequently sit behind NAT/CGNAT, switch between Wi-Fi and cellular networks, change addresses and lose NAT mappings. A usable design needs short-lived connection candidates and NAT traversal rather than a permanent address table.

## Is the Peer ID a secret?

No. A Peer ID is similar to an address. It can be shared. Authentication comes from proving possession of the corresponding private key.

## Why derive the Peer ID from the public key?

It cryptographically binds the address to the identity key. The directory cannot legitimately assign an unrelated public key to the same Peer ID without finding a cryptographic hash collision. It also makes first-run identity creation independent from a central account-number allocator.

## Is a GUID still used?

The architecture no longer needs a random server-issued GUID as the root identity. Internally Dyract may still use GUID/UUID values for messages, challenge IDs and other records, but the public Peer ID is derived from the public identity key.

## What does the server know?

The intended production directory may know:

- Peer ID,
- public identity key,
- short-lived reachability/ICE information while a peer is available,
- protocol version,
- a push-routing token where background wake-up is enabled,
- operational/security metadata required for abuse prevention.

The current prototype stores identities in memory and short-lived presence separately in an ephemeral in-memory lease store.

It should not hold user-assigned contact names, address books, conversation bodies, attachments or message history.

## Can the server see users' IP addresses?

Yes, to some extent this is unavoidable. Any server a client connects to can observe the source network address of that connection. A directory that helps establish P2P connectivity also handles reachability metadata. The goal is therefore minimization and short retention, not the false claim that the infrastructure can never observe an IP address.

Direct peers may also learn one another's network addresses. Users who require IP hiding would need relay-only routing, which is a different privacy/connectivity trade-off.

## Can any registered user look up another user's IP?

No. Dyract now separates ordinary identity lookup from endpoint resolution.

`/api/v1/peer/lookup` is an authenticated identity/public-key lookup and returns no connection candidates.

`/api/v1/peer/resolve` can return a target's temporary presence only when the requester provides both:

1. a fresh request signed by the requester's identity, and
2. a valid contact capability signed by the target identity specifically for that requester.

Knowing a Peer ID alone is therefore insufficient to retrieve endpoint candidates.

## What is a contact capability?

It is a signed authorization object issued by one peer to another. The current capability contains:

```text
Version
IssuerPeerId
GranteePeerId
CapabilityId
Issued time
Expiry time
Issuer signature
```

For example, Bob can issue a capability to Alice that authorizes Alice to resolve Bob's temporary reachability data. The directory verifies Bob's signature when Alice presents the capability.

The server does not need to store an `Alice is Bob's contact` row.

## Can a contact capability be copied to someone else?

The current capability is bound to a specific `GranteePeerId`, so copying Alice's capability to Charlie does not help Charlie. A resolve request must also be freshly signed by the grantee's private key.

## Can a capability be revoked?

Not yet before its expiry time. Expiration is implemented, but explicit capability revocation/rotation is still a production requirement. Until that exists, capabilities should use a reasonable lifetime rather than being treated as permanent credentials.

## How long does presence stay on the server?

A published presence lease may last at most two minutes; the client currently defaults to 90 seconds. Publishing again replaces the previous lease. A peer can also explicitly remove its presence with a signed request.

Expired leases are removed from the in-memory presence store as it is accessed or updated. Production scaling can move this data to Redis or another TTL-based ephemeral store without creating permanent IP history.

## What connection candidates are accepted now?

The bootstrap model accepts up to eight `host`, `srflx`, or `relay` candidates using UDP or TCP and IPv4/IPv6 addresses. Ports must be valid and loopback, unspecified, broadcast and multicast addresses are rejected.

This is only the directory representation. The later transport phase should use a mature ICE/STUN implementation rather than inventing a custom NAT traversal algorithm.

## Are messages stored on the server while the recipient is offline?

Not in the intended architecture. An undelivered message remains in the sender's local outbox. The sender retries when connectivity is available.

## What if sender and recipient are never online at the same time?

With strict device-to-device storage, the message cannot be delivered until there is overlapping reachability. A wake-up notification can improve this, but mobile platforms do not guarantee indefinite background execution or guaranteed silent push delivery.

A future optional encrypted mailbox would improve asynchronous delivery but would deliberately change the original server-minimal model and therefore requires a separate design decision.

## Does P2P always mean a direct socket between the phones?

No. NAT/firewall conditions sometimes make a direct route impossible. Dyract should attempt direct ICE/STUN connectivity first. An optional TURN relay can forward already encrypted packets when a direct route cannot be established.

## Would a TURN server be able to read messages?

Not if Dyract establishes its own end-to-end encrypted peer session correctly. TURN sees packet metadata and relays ciphertext; it should not possess the end-to-end session keys. This still exposes more network metadata than a successful direct connection, which is why relay usage should be visible/configurable.

## Will there be a strict direct-only mode?

That is a planned product option to evaluate. A strict mode would refuse relay transport and therefore provide stronger adherence to direct connectivity, but messages will fail/delay on networks where NAT traversal cannot create a path.

## How are requests to the directory authenticated?

Registration uses challenge/response. Security-sensitive operations such as lookup, presence publication/removal and endpoint resolution use protocol-specific signed payloads containing a timestamp and random nonce. The server verifies the signature against the registered public key, rejects stale timestamps and rejects nonce replays.

Endpoint resolution additionally verifies a target-signed contact capability.

## What cryptography does the first implementation use?

Long-term identity signatures currently use ECDSA P-256 with SHA-256 via `System.Security.Cryptography`. This is an implementation starting point, not a completed messaging cryptosystem.

The future peer-session protocol still needs an ephemeral key agreement, forward secrecy, transcript authentication, replay protection, version negotiation and external review.

## Why not implement custom encryption?

Designing a secure cryptographic primitive is substantially harder than designing an application protocol. Dyract should use established, reviewed primitives/libraries and keep custom code limited to protocol composition and application behavior.

## Where is the private key stored?

In the bootstrap library the key can be exported for tests/prototyping, but production mobile code must protect it using Android Keystore and iOS Keychain/Secure Enclave facilities as appropriate. It must not be stored as plain SQLite data.

## What happens if I uninstall the app?

If the identity private key has no backup, uninstalling/clearing app data means losing that identity. That is a legitimate secure default. A later recovery/export feature must be explicit and encrypted; the directory should not silently keep recoverable private keys.

## Will Dyract use phone numbers or email addresses?

They are not required by the architecture. Contact exchange can use a Peer ID plus a cryptographic contact capability, ideally through QR or a copyable invitation link.

## Can users search for other users?

Not in the privacy-first model. Exact invitation/Peer ID exchange avoids creating a globally searchable directory and reduces enumeration risk.

## How do I know a contact's key has changed?

The client should pin the contact's identity fingerprint. If a previously known identity unexpectedly presents different key material, the app must show a security warning rather than silently trusting the replacement.

Because the current Peer ID itself is derived from the public key, a different public key normally produces a different Peer ID rather than transparently replacing the original identity.

## What about multiple phones/tablets for one user?

Multi-device identity is deliberately deferred. The simplest first model is one peer identity per installation. A future account/master identity could sign per-device certificates, but that introduces message fan-out, synchronization and device-revocation complexity.

## Can Dyract support group chat?

Eventually, but groups significantly change the protocol. Membership changes, sender keys, history access, offline fan-out and multi-device behavior all require separate design work. One-to-one messaging should be secure and reliable first.

## Can Dyract support voice/video calls?

Potentially. ICE/STUN/TURN groundwork overlaps with real-time media connectivity, but calls are outside the initial scope.

## What will message status mean?

The UI should distinguish states accurately, for example:

```text
Queued locally
Connecting
Sent to peer connection
Delivered and stored by peer
Read (if receipts are enabled)
Failed/retrying
```

"Sent" must not be displayed as "delivered" merely because the message entered the local outbox.

## How private is Dyract?

Dyract aims to minimize central data, not make impossible anonymity claims. Network participants can observe some metadata; the directory sees authenticated requests and source connections; direct peers can generally observe connectivity metadata; mobile push providers see push-routing activity. The security/privacy documentation should state these limitations precisely.

## Is the current code production ready?

No. Identity registration and capability-protected short-lived presence are now implemented, but the project still requires API integration tests, persistent identity storage, rate limits, mobile secure key storage, capability revocation, P2P transport, a reviewed encrypted session protocol and an independent security assessment before production use.
