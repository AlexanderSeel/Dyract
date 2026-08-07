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

It should not hold user-assigned contact names, address books, conversation bodies, attachments or message history.

## Can the server see users' IP addresses?

Yes, to some extent this is unavoidable. Any server a client connects to can observe the source network address of that connection. A directory that helps establish P2P connectivity also handles reachability metadata. The goal is therefore minimization and short retention, not the false claim that the infrastructure can never observe an IP address.

Direct peers may also learn one another's network addresses. Users who require IP hiding would need relay-only routing, which is a different privacy/connectivity trade-off.

## Can any registered user look up another user's IP?

That is explicitly **not** the intended production behavior. The bootstrap implementation's signed lookup returns identity/public-key information only. Before endpoint lookup is added, Dyract should implement contact-capability authorization so knowing a Peer ID alone is insufficient to retrieve reachability information.

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

The first implementation uses challenge/response registration and signed lookup requests. Signed requests include a protocol-specific canonical payload, a timestamp and a random nonce. The server verifies the signature using the registered public key and rejects stale or replayed nonces.

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

The client should pin the contact's identity fingerprint. If a previously known Peer ID/public-key relationship unexpectedly changes, the app must show a security warning rather than silently trusting the replacement.

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

No. The repository currently contains the identity/directory bootstrap. It still requires integration tests, persistent storage, rate limits, mobile secure key storage, contact authorization, P2P transport, a reviewed encrypted session protocol and an independent security assessment before production use.
