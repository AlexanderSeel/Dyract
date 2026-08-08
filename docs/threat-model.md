# Dyract threat model

## Status and scope

This document is the repository threat model for the current Dyract one-to-one messaging architecture.

It is an engineering threat model, **not** an independent security audit. Its purpose is to make trust assumptions, metadata exposure, expected attacker capabilities, implemented mitigations and unresolved risks explicit enough that code/review decisions can be checked against them.

Scope covered here:

```text
.NET MAUI Android/iOS client
local SecureStorage / Keychain / Keystore boundary
local encrypted SQLite storage
ASP.NET Core directory API
PostgreSQL durable directory metadata
Redis short-lived directory state
contact capability issuance/revocation
presence and WebRTC signaling
transport-independent authenticated session protocol
reliable one-to-one message protocol
experimental FsWebRTC Android transport
```

Future APNs/FCM wake-up, TURN production deployment, attachments, identity recovery and multi-device support must extend this model before they ship.

## Security objectives

Dyract is designed to provide these properties:

1. A PeerId is cryptographically bound to an identity public key.
2. Possession of a PeerId alone does not authorize reachability lookup or signaling.
3. A directory request that changes/discloses security-sensitive state requires proof from the relevant private identity key.
4. A capability is usable only by its exact grantee and can expire or be revoked.
5. A known contact's identity key cannot change silently on the client.
6. Chat messages are not stored by the directory.
7. Outgoing chat messages are durably stored on the sender before network delivery is attempted.
8. Direct peer sessions authenticate the pinned application identities independently of WebRTC/DTLS.
9. Peer-session application data uses authenticated encryption and replay/order protection.
10. Directory reachability/signaling state is short-lived.
11. Horizontally scaled directory instances share security-critical transient state when Redis is configured.
12. Durable capability revocation survives a server restart when PostgreSQL is configured.
13. Infrastructure failure must not silently degrade to a weaker authorization/replay model.
14. Ordinary diagnostics must avoid raw PeerIds, IP addresses, ICE candidate strings, keys and message content.

Dyract does **not** promise anonymity or traffic-analysis resistance.

## Assets

### Device secrets

Highest-value device assets:

- long-term identity private key;
- local-data AES-256-GCM key;
- authenticated-session ephemeral/private key material;
- received and issued contact capabilities;
- locally stored message plaintext after decryption;
- future push credentials/recovery material.

### Device-owned private data

- local contact display names;
- contact list/social graph;
- conversations;
- message bodies;
- outbox state and future attachments;
- read state and future local profile preferences.

### Directory durable data

PostgreSQL currently contains only server-owned durable metadata:

```text
PeerId
identity public key
registration timestamp
revoked capability issuer PeerId
opaque revoked capability ID
revocation natural expiry
migration metadata
```

The revocation schema intentionally omits the grantee.

### Directory transient data

Redis can temporarily contain:

```text
registration challenge state        <= 2 minutes
signed-request replay markers       ~ 5 minutes
presence/reachability leases         <= 2 minutes
WebRTC signaling envelopes           <= 60 seconds
```

Presence/signaling inherently can contain current network candidate metadata.

### Availability assets

- directory API availability;
- PostgreSQL identity/revocation availability;
- Redis transient-state availability;
- STUN/TURN availability once deployed;
- push delivery once implemented;
- local outbox integrity and retry state.

## Trust boundaries

```text
+----------------------- device -----------------------+
|                                                      |
|  user/UI                                              |
|     |                                                 |
|  MAUI app ---- SecureStorage/Keychain/Keystore       |
|     |                                                 |
|  encrypted-field SQLite                              |
|                                                      |
+------------------------|-----------------------------+
                         | HTTPS
                         v
+--------------------- directory ----------------------+
| ASP.NET Core API                                     |
|      |                         |                     |
|      v                         v                     |
| PostgreSQL                  Redis                    |
| durable metadata           short-lived state        |
+------------------------------------------------------+
                         |
                         | discovery/signaling only
                         v
                untrusted public networks
                         |
       +-----------------+------------------+
       |                                    |
       v                                    v
 authenticated peer A  <---- P2P ----> authenticated peer B
```

Important principle: **WebRTC connectivity is not the application trust boundary.** A successful ICE/DTLS channel is still treated as untrusted until the Dyract application handshake verifies the pinned peer identity.

## Threat actors

### A. Unauthenticated internet attacker

Capabilities:

- send arbitrary HTTP requests;
- scan APIs;
- guess or obtain PeerIds;
- send oversized/malformed values;
- attempt resource exhaustion;
- observe their own connection metadata.

Does not possess a registered peer private key.

### B. Malicious registered Dyract peer

Adds the ability to:

- produce valid signatures for its own identity;
- request public identity lookup where allowed;
- receive capabilities deliberately granted to it;
- submit valid but adversarial protocol values within its authorization scope.

### C. Malicious authorized contact

Adds the ability to:

- resolve/signaling-connect to a peer while its capability remains valid;
- learn direct-network information required for P2P connectivity;
- receive/decrypt messages intentionally sent to it;
- retain/screenshot information after receipt.

Revocation cannot erase information already disclosed to this peer.

### D. Network attacker / hostile access network

Capabilities:

- observe IP-level traffic/timing;
- block, delay, reorder or redirect traffic;
- attempt TLS interception where trust can be subverted;
- interfere with STUN/ICE/TURN traffic.

### E. Compromised/malicious directory process or operator

Capabilities can include:

- observe incoming client IP addresses/timing;
- observe the metadata required to service directory requests;
- suppress or alter API responses;
- influence reachability/signaling metadata;
- inspect server-side infrastructure if operational access permits.

The protocol should prevent this actor from forging a peer's long-term identity signature or decrypting end-to-end peer messages, but it cannot prevent service denial or metadata observation inherent in directory participation.

### F. PostgreSQL compromise/dump

Exposes durable identity/revocation metadata and allows destructive/tampering attacks if write access is obtained.

It should not expose contact names, contact lists, conversations, message bodies or attachments because those data are not stored there.

### G. Redis compromise/dump

Can expose currently active registration/presence/signaling metadata within short TTL windows and can damage replay/availability semantics if write/delete access is obtained.

Redis compromise is therefore a security event even though Redis holds no chat history.

### H. Stolen unlocked / rooted / fully compromised device

A fully compromised endpoint can potentially read application memory, invoke the app as the user, capture decrypted content or access OS-protected secrets depending on platform compromise level.

Dyract does not claim to protect plaintext from an attacker who fully controls an unlocked endpoint.

### I. Future push/STUN/TURN provider

Will observe infrastructure-specific metadata such as push tokens, wake timing, TURN source/destination timing or STUN address discovery. These providers must be added to this model before production integration.

## STRIDE analysis

## S — Spoofing

### S1. Attacker claims another PeerId

Threat:

An attacker sends a request using Bob's PeerId.

Current mitigation:

- PeerId is SHA-256-derived from the identity public key;
- security-sensitive operations verify signatures using the registered public key;
- registration proves possession of the corresponding private key through a random challenge;
- direct peer sessions authenticate the pinned long-term identity independently of WebRTC.

Residual risk:

- theft/compromise of Bob's private key is identity compromise;
- identity recovery/multi-device delegation is not yet designed.

### S2. Directory substitutes a new key for an existing contact

Threat:

A malicious/compromised directory attempts to return attacker key material for Bob.

Current mitigation:

- existing contacts pin Bob's public key locally;
- PeerId/public-key binding is recomputed/validated;
- client resolve/session logic compares against the pinned contact identity;
- a different key cannot preserve the same PeerId without a practical SHA-256 collision.

Expected outcome:

Connection/identity validation fails rather than silently adopting the key.

### S3. Attacker copies another peer's reachability capability

Threat:

Charlie obtains a capability Bob issued specifically to Alice.

Current mitigation:

- capability contains exact issuer and grantee PeerIds;
- Bob signs the complete capability;
- resolve/signaling request itself is signed by the requester;
- server verifies `capability.GranteePeerId == requester PeerId`.

Expected outcome:

Charlie cannot use Alice's grant.

### S4. Fake P2P endpoint completes ICE first

Threat:

An attacker or malicious directory steers ICE/signaling to an attacker-controlled endpoint.

Current mitigation:

- Dyract's application handshake authenticates the pinned peer identity after transport establishment;
- WebRTC/DTLS identity is not treated as sufficient application identity.

Expected outcome:

The attacker can cause connection failure/DoS but cannot impersonate the pinned peer without its private identity key.

## T — Tampering

### T1. HTTP request/body modification

Current mitigation:

- HTTPS is required by the mobile directory configuration;
- security-sensitive request bodies are independently signed over canonical/versioned proof payloads;
- timestamps and nonces bind request freshness.

Residual risk:

A compromised endpoint/private key can produce valid malicious requests within its authority.

### T2. Contact invitation/pairing QR manipulation

Current mitigation:

- only Dyract contact/pair payload structures are accepted by the scanner;
- scanning only transports text into the existing import path;
- normal PeerId/key/capability signature checks still execute;
- generic QR URLs are not opened/executed by this flow.

### T3. Redis presence/signaling modification

Threat:

A Redis attacker changes ICE candidates or signaling envelopes.

Current mitigation:

- Redis is server-internal infrastructure, expected to be isolated/authenticated in production;
- short TTL limits historical exposure;
- an attacker-directed connection still must pass Dyract application identity authentication;
- malformed stored presence state fails closed;
- invalid signaling ultimately cannot forge the pinned peer's authenticated session.

Residual risk:

- DoS and metadata manipulation remain possible;
- replay-marker deletion can weaken server replay protection if Redis is compromised;
- production Redis security policy is still pending.

### T4. PostgreSQL identity/revocation modification

Current mitigation:

- schema migrations and critical table shapes fail closed on startup;
- client-side PeerId/key pinning detects identity substitution for known contacts;
- capability signatures remain checked;
- revocation store contains only minimized metadata.

Residual risk:

A write-capable database attacker can cause denial of service, delete revocations or corrupt registry availability. PostgreSQL access therefore remains security-sensitive infrastructure.

### T5. Local SQLite modification

Current mitigation:

- user-content fields are AES-256-GCM authenticated, so ciphertext modification/wrong key causes authentication failure;
- schema versions/migrations are validated and future/malformed states fail closed;
- incoming message dedup/collision rules reject changed content for an existing MessageId.

Residual risk:

Operational metadata is not full-database encrypted/authenticated as one opaque blob; a fully compromised device can bypass application-level assumptions.

### T6. Peer application frame modification/replay

Current mitigation:

- `DYSE` authenticated encryption;
- session/role/transcript binding;
- directional keys;
- monotonic sequence checks;
- replay and out-of-order rejection;
- `DYRM` message/ACK validation inside the authenticated session.

Residual risk:

Independent cryptographic review and fuzz/property testing remain required.

## R — Repudiation

Dyract is not designed as a non-repudiation system.

Privacy goals deliberately avoid permanent central chat/audit history. The directory may retain operational security logs in production, but those logs must be minimized and governed by an explicit retention policy.

Consequences:

- Dyract should not claim that server logs prove a person sent a particular message;
- local message state is primarily for user experience/reliability, not legal audit;
- diagnostic correlation IDs should be designed without logging raw message content/keys/candidate data.

Open work:

- define privacy-aware structured logging/metrics and retention;
- define abuse-investigation evidence that does not become a hidden conversation-history system.

## I — Information disclosure

### I1. Directory metadata visibility

The directory inevitably sees some metadata when clients use it.

Potentially visible at the API/service layer:

- client source IP address;
- request timing/frequency;
- requester/target PeerIds present in signed operations;
- identity public keys;
- active presence candidates;
- WebRTC signaling payloads while active;
- capability IDs/issuer/grantee while validating a supplied capability;
- protocol/application version metadata when introduced.

Dyract's privacy claim is therefore **message/content minimization**, not directory anonymity.

### I2. PostgreSQL disclosure

A database dump reveals durable identity registry and revocation issuer metadata, but should reveal no:

- contact names;
- contact lists;
- conversations;
- message bodies;
- attachments.

### I3. Redis disclosure

A Redis snapshot/memory disclosure can reveal active short-lived state:

- registration public-key/challenge binding;
- presence PeerId + network candidates;
- signaling sender/target/session/payload metadata;
- replay markers (keyed by hashes rather than raw PeerId/nonces).

Mitigations:

- short TTLs;
- explicit ACK deletion for signals;
- no chat history;
- SHA-256-derived key names for replay/presence/signaling peer identifiers;
- production TLS/auth/network isolation still required.

### I4. Direct peer learns network information

Direct P2P connectivity normally reveals network-address information to the peer.

This is inherent in direct communication. `DirectOnly` maximizes that directness; TURN can reduce direct endpoint exposure to the other peer at the cost of relay metadata/infrastructure.

Dyract does not claim to hide a user's IP address from an authorized direct peer.

### I5. Local database/device theft

Current protection:

- message/contact content fields are encrypted with a separate local AES key;
- key is stored through platform SecureStorage;
- Android app backup is disabled.

Residual exposure:

- PeerIds/timestamps/operational metadata remain visible in SQLite;
- unlocked/rooted device compromise can expose decrypted content;
- non-exportable identity-key/Secure Enclave evaluation remains open.

### I6. Traffic analysis

TLS/E2EE hide content, not all traffic metadata.

A network observer can still infer:

- connection times;
- approximate traffic volumes;
- directory usage;
- peer-to-peer communication endpoints when observable at the network layer.

An anonymity network/mixnet is explicitly outside the current scope.

## D — Denial of service

### D1. Oversized/malformed API requests

Current controls:

- Kestrel request-body cap: 64 KiB;
- JSON max depth;
- bounded Base64/key/signature inputs;
- bounded presence candidate count/shape;
- bounded signal payload/count/fetch/ACK sizes.

### D2. Request flooding

Current controls:

- ASP.NET fixed-window per-client-address limits;
- separate registration and peer-operation policies;
- bounded server stores;
- Redis signaling capacity enforced atomically per target.

Open risk:

The ASP.NET rate limiter is process-local. Multiple instances need an edge/distributed abuse-control strategy before production horizontal scaling.

### D3. Signaling mailbox abuse

Current controls:

- exact target-issued capability required for send;
- maximum 64 pending items per target;
- maximum 60-second TTL;
- maximum 32 KiB payload;
- target fetch maximum 20;
- ACK/expiry deletes state;
- Redis capacity changes are atomic across instances.

### D4. Infrastructure outage

PostgreSQL or Redis failure can make directory operations unavailable.

Dyract intentionally prefers fail-closed unavailability over silently weakening security semantics.

Production work:

- HA topology;
- timeouts/circuit behavior;
- monitoring;
- backup/recovery for PostgreSQL;
- Redis availability policy.

### D5. P2P/NAT failure

Direct connectivity can fail due to CGNAT/firewalls/mobile suspension.

Current response:

- sender keeps local durable outbox;
- retries are bounded/backed off;
- TURN is an explicit future fallback policy;
- mobile wake-up remains future work.

No central chat queue is introduced merely to hide this availability limitation.

## E — Elevation of privilege

### E1. Registered peer obtains endpoint metadata without grant

Current mitigation:

- PeerId lookup and presence resolution are separate operations;
- `/peer/resolve` and `/signal/send` require target-issued capability;
- capability scope/signature/lifetime/revocation are verified;
- request itself must be signed by the grantee.

### E2. Capability survives issuer revocation

Current mitigation:

- signed revocation endpoint;
- PostgreSQL durable revocation when configured;
- revocation checked by both resolve and signaling;
- revocation survives fresh server/store instance;
- replacement grant requires a different capability ID.

Residual boundary:

Revocation does not retroactively close an already authenticated P2P session or erase metadata already disclosed. Production transport policy must define whether local revocation proactively closes active sessions.

### E3. Cross-target signaling ACK

Current mitigation:

- ACK is signed by target;
- signal inbox selected by authenticated target PeerId;
- Redis ACK script only removes IDs from that target's hashed inbox keys;
- cross-target tests verify isolation.

### E4. Server-side deserialization/script injection

Current controls:

- JSON request depth/size bounds;
- protocol shape validation;
- Redis Lua scripts are static source; untrusted values are passed through ARGV/KEYS rather than concatenated into script code;
- SQL uses parameterized Npgsql commands;
- migration SQL is static repository-controlled code.

Remaining work:

- protocol/API fuzzing;
- dependency/SBOM automation;
- penetration testing;
- deployment hardening.

## Metadata inventory

| Data | Where | Typical lifetime | Why it exists | Current minimization |
| --- | --- | --- | --- | --- |
| PeerId + public key | PostgreSQL | durable | identity registry | no profile/contact data |
| Registration timestamp | PostgreSQL | durable | registry metadata | no usage history table |
| Revoked capability issuer/id/expiry | PostgreSQL | until cleanup/expiry policy | prevent grant resurrection | no grantee column |
| Registration challenge | Redis | <= 2 min | proof-of-private-key registration | random ID key, TTL, consumed once |
| Replay marker | Redis | ~5 min | reject signed-request replay | SHA-256-derived key, opaque value |
| Presence lease | Redis | <= 2 min | authorized endpoint discovery | hashed key, short TTL |
| WebRTC signaling | Redis | <= 60 sec | connection negotiation | hashed target keys, ACK/TTL deletion |
| Source IP/timing | server/network logs | deployment-defined | networking/operations | retention/log design still open |
| Contact names/list | device only | user-controlled | UX | never directory data |
| Message bodies/history | device only | user-controlled | messaging | never directory data |
| Outbox | sender device | until ACK/user lifecycle | reliability | encrypted content field |

The table describes intended application storage. Infrastructure/platform logs, backups and observability can accidentally expand retention; production deployment must explicitly prevent that.

## Privacy properties and non-properties

### Dyract is intended to provide

- no central chat-history store;
- no server-side contact-name list;
- no server-side friendship/contact graph by design;
- capability-gated reachability/signaling;
- short retention of active connection-negotiation state;
- local content encryption at rest;
- authenticated end-to-end peer application sessions.

### Dyract does not currently provide

- anonymity;
- metadata-hiding against the directory operator;
- IP hiding from an authorized direct peer;
- protection from a fully compromised unlocked endpoint;
- guaranteed immediate delivery while both peers cannot run/connect;
- post-compromise security/double-ratchet guarantees;
- deniable messaging;
- non-repudiation;
- multi-device recovery guarantees.

## Expected attack outcomes

### Attacker knows only Bob's PeerId

Expected:

- may be able to request deliberately public/allowed identity information;
- cannot obtain Bob's active presence candidates through `/peer/resolve` without Bob-issued capability;
- cannot submit new signaling to Bob without that capability.

### Charlie steals a Bob->Alice capability string

Expected:

- Charlie's signed request identifies Charlie;
- capability grantee identifies Alice;
- scope validation rejects the request.

### Attacker replays Alice's captured signed resolve request

Expected:

- first valid request may succeed within authorization scope;
- repeated nonce is rejected;
- Redis mode makes this rejection consistent across server instances.

### Bob revokes Alice's capability and directory restarts

Expected with PostgreSQL:

- revocation remains present;
- resolve/signaling remains unauthorized after restart.

Expected without PostgreSQL development mode:

- revocation store is process-local and restart loses it; this mode is not the horizontal/production topology.

### Directory/Redis returns attacker-controlled ICE data

Expected:

- connection may be redirected or fail;
- attacker still cannot complete the Dyract authenticated peer session as Bob without Bob's private identity key.

### Directory is unavailable

Expected:

- new discovery/signaling may fail;
- already connected peers can continue according to transport implementation;
- outgoing messages remain in local outbox for retry;
- no central fallback message queue is created.

## Risk register

### High / release-blocking

1. **Physical transport validation incomplete** — Android FsWebRTC behavior across real Wi-Fi/NAT/cellular/IPv6/lifecycle scenarios remains unproven.
2. **FsWebRTC Android 16 KiB native-library warning (`XA0141`)** — current binding is not production-ready until resolved/replaced.
3. **Independent cryptographic review missing** — session composition/key schedule must be reviewed before production trust claims.
4. **Protocol fuzz/property testing missing** — parser/state-machine robustness still needs adversarial automation.
5. **Mobile identity-key hardening incomplete** — non-exportable hardware-backed/Secure Enclave strategy still needs evaluation.

### Medium / production-hardening

1. Redis production TLS/authentication/network policy not yet defined.
2. Process-local API rate limits do not provide global multi-instance abuse control.
3. Privacy-aware logging/metrics/retention policy is not yet implemented.
4. PostgreSQL backup/recovery/retention policy is not yet defined.
5. Identity recovery/reset UX and security model are incomplete.
6. APNs/FCM wake metadata model is not yet implemented/reviewed.
7. TURN policy/deployment and relay metadata implications remain unresolved.
8. Local SQLite exposes operational metadata such as PeerIds/timestamps.
9. No independent penetration test has been performed.

## Security acceptance gates before public production

At minimum:

- [ ] physical Android transport matrix completed and documented;
- [ ] Android 16 KiB transport blocker resolved or transport binding replaced;
- [ ] iOS transport adapter implemented and physical Android<->iPhone tested;
- [ ] independent review of authenticated-session cryptography;
- [ ] protocol/property fuzz suite added;
- [ ] endpoint/API penetration test completed;
- [ ] non-exportable identity-key/Secure Enclave decision documented;
- [ ] recovery/reset threat model implemented;
- [ ] Redis TLS/auth/network isolation deployed;
- [ ] distributed/edge rate limiting and abuse controls deployed;
- [ ] privacy-safe logging/metrics retention reviewed;
- [ ] PostgreSQL backup/restore tested;
- [ ] dependency/SBOM automation active;
- [ ] wake-up metadata model reviewed before APNs/FCM rollout.

## Change-control rule

Any feature that adds a new party, storage system, identifier, discovery mechanism or retention path must update this threat model before it is considered complete.

Examples:

```text
multi-device identity
cloud backup
public usernames/search
push notifications
TURN relay
attachments
voice/video
bots/channels
```

Those features can materially change Dyract's metadata graph even when message encryption remains strong.
