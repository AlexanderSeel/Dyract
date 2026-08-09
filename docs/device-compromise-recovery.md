# Device compromise, loss and identity recovery

## Status and purpose

This document records Dyract's current stolen-device/recovery security model and the decisions required before encrypted identity recovery/export ships.

The explicit local destructive-reset path is implemented. Encrypted identity export/restore is not.

This is an engineering analysis, not an independent mobile-security audit. The goal is to avoid recovery features that weaken the core identity model or silently replace an identity after secure-storage failure.

## Current identity model

Each installation owns one long-term P-256 identity. Its public `dyr_...` PeerId is derived from the public identity key.

The shipping MAUI app currently stores an exportable PKCS#8 private-key representation through MAUI `SecureStorage`:

```text
PeerIdentity.Generate()
        |
        +-- ExportPkcs8PrivateKey()
        |
        +-- SecureStorage.Default.SetAsync(...)
```

On Android, MAUI SecureStorage uses the platform-protected storage path backed by Android cryptographic facilities. On iOS, it uses Keychain. Dyract also uses an installation marker because iOS Keychain data can survive uninstall while ordinary app preferences do not.

Current reinstall rule:

> A reinstall without an explicit recovery flow is a new installation and therefore a new identity.

If secure identity storage was initialized but cannot be read/imported, Dyract deliberately fails instead of silently generating another identity. Silent regeneration would change the PeerId while presenting itself as the same installation.

The security/recovery screen remains reachable from this failure state so the user can make an explicit destructive-reset decision.

## Current local-data model

The long-term identity key and local-data key are separate.

The local-data key protects selected SQLite content fields with AES-256-GCM. Therefore possession of the identity private key alone is not intended to be sufficient to decrypt the local message/contact database.

Likewise, backing up only SQLite without its local-data key is not a usable recovery mechanism.

This separation is intentional:

```text
identity key     -> peer authentication / PeerId continuity
local-data key   -> local content confidentiality
SQLite           -> local encrypted content + operational metadata
```

## Threat scenarios

### 1. Phone lost while locked

Expected protection depends primarily on the operating system's device-lock and secure-storage protections.

Dyract's application-layer goals are:

- no server-side conversation history is available for download by an attacker;
- no identity private key is stored by the Dyract directory;
- no local-data encryption key is stored by the Dyract directory;
- contacts/messages cannot be reconstructed from server identity metadata.

Residual risk:

- a sufficiently capable device/OS compromise may recover application secrets;
- current identity storage uses exportable PKCS#8 material inside the app process, not a proven non-exportable hardware identity key;
- notification previews or OS backups can become separate disclosure channels and require explicit policy.

### 2. Phone stolen while unlocked

Treat this as high-risk endpoint compromise.

An attacker with an unlocked application/device may be able to:

- read already decrypted local content through the UI;
- invoke Dyract as the user;
- issue/revoke capabilities;
- establish authenticated sessions as the stolen identity;
- potentially extract secrets from application memory or platform storage depending on device compromise level.

Dyract cannot cryptographically distinguish the legitimate user from an attacker controlling the already-authorized unlocked endpoint.

Future mitigations can reduce exposure but cannot eliminate this boundary:

- optional app re-authentication/biometric gate;
- inactivity locking;
- platform non-exportable identity keys;
- OS/device attestation only if its privacy/cost tradeoffs are explicitly accepted;
- a separately authenticated remote device-revocation design if multi-device/recovery is introduced.

### 3. Rooted/jailbroken or fully compromised phone

Assume loss of confidentiality and authentication for that endpoint.

A fully privileged attacker can potentially:

- inspect application memory;
- hook cryptographic calls;
- capture plaintext before encryption/after decryption;
- invoke private-key operations;
- alter local database/application state.

Dyract must not claim protection against an attacker that fully controls the endpoint at runtime.

The security response is identity/device replacement and contact re-verification, not an attempt to make plaintext inaccessible to root while actively being displayed by the app.

### 4. SecureStorage entry missing or unreadable

Current rule: **fail closed**.

Dyract must not automatically call `PeerIdentity.Generate()` merely because a previously initialized secure identity cannot be read.

Reasons include:

- temporary OS/key-store failure could otherwise become permanent identity loss;
- contacts would see an unexpected key/PeerId change;
- queued/local data could become detached from its identity semantics;
- silent regeneration creates an unsafe illusion of continuity.

The current flow distinguishes:

```text
first installation -> generate identity
known installation + readable key -> load identity
known installation + unreadable key -> fail closed + expose security/reset screen
explicit reset confirmed -> destroy local identity/data and generate new identity
explicit verified restore -> future encrypted recovery flow, not implemented
```

### 5. App uninstall/reinstall

Current intended semantics are a new identity unless the user explicitly performs a future recovery flow.

On iOS this matters because Keychain material may survive uninstall. The current installation marker removes the old Dyract identity on a fresh installation rather than accidentally resurrecting an identity without an explicit recovery decision.

A future restore feature must change this deliberately, not by relying on undocumented/implicit platform persistence.

### 6. Device destroyed with no recovery material

Under the current model, identity and local history are lost.

This is a deliberate consequence of not maintaining central chat/key backups.

Dyract says this in the security UI. A privacy-first system should not quietly introduce cloud escrow merely to make device replacement convenient.

## Identity recovery design requirements

Any future identity recovery/export mechanism must meet all of these requirements before implementation is considered complete.

### Explicit opt-in

Recovery/export must be an intentional user action. A server-side recoverable private-key copy must not appear as an implicit side effect of registration.

### End-to-end encrypted recovery material

The directory/operator must not receive a plaintext identity private key or local-data key.

If a recovery bundle is stored remotely, encryption must occur on the endpoint using credentials/key material unavailable to the storage provider.

### Strong key derivation

A human passphrase must not be used directly as an encryption key. The selected password-based KDF and parameters need a separate reviewed design with memory-hard derivation, versioned parameters and upgradeability.

### Versioned authenticated container

A recovery bundle needs a strict versioned authenticated format including, at minimum:

```text
format version
identity PeerId / public-key fingerprint
encrypted identity private-key material
optional encrypted local-data key
KDF parameters / salt when passphrase based
creation timestamp
integrity/authentication tag
```

Message history should not automatically be included merely because identity recovery exists. Identity continuity and conversation backup are separate products/security decisions.

### Restore must verify identity binding

Before accepting restored key material Dyract must:

1. import/validate the key;
2. derive its PeerId;
3. compare the result with recovery metadata shown to the user;
4. refuse mismatched/corrupt packages;
5. never silently merge incompatible local databases/identities.

### Recovery must not bypass contact key-change semantics

Restoring the same identity preserves the same PeerId/public key.

Generating a replacement identity is different and must be visible to contacts. Dyract must not use a recovery UI to silently teach contacts to accept a new identity key.

### Recovery codes are secrets

If Dyract introduces recovery codes/phrases, possession should be modeled as authority over the recovered identity. They therefore require:

- secure presentation;
- explicit warning not to share;
- no analytics/logging/clipboard persistence by default where avoidable;
- rotation/revocation semantics where technically possible;
- documented behavior after suspected disclosure.

## Implemented reset semantics

The shipping app now provides an explicit **Reset identity and local data** action.

It requires two deliberate steps:

1. acknowledge a destructive warning;
2. type the exact confirmation word `RESET`.

The reset is coordinated by a persisted `pending reset` marker. Once the destructive operation starts, UI cancellation does not interrupt the reset. If the process is terminated mid-reset, Dyract completes the pending reset before ordinary identity initialization on the next launch.

The implemented reset performs:

```text
mark reset pending
        ↓
transactionally remove outbox/messages/conversations/contacts
        ↓
SQLite secure_delete + WAL truncate + VACUUM best effort
        ↓
clear directory configuration
        ↓
remove long-term identity SecureStorage value
        ↓
remove local-data-key SecureStorage value
        ↓
clear identity/local-data initialization markers
        ↓
clear reset-pending marker
        ↓
generate a fresh identity and local-data key through normal initialization
```

The SQLite schema and migration ledger are preserved so already-created store instances remain valid after the reset. User rows, including incoming/issued capabilities stored with contacts, are removed.

The new local-data key means pre-reset encrypted content is no longer decryptable through Dyract even if encrypted remnants exist outside the logical database after deletion.

`PRAGMA secure_delete`, WAL truncation and `VACUUM` reduce ordinary SQLite residue, but Dyract does **not** claim forensic secure erase of flash storage. Filesystem snapshots, device backups, wear-leveling and a fully compromised OS are outside the guarantee.

No push-token state exists yet. Before push ships, the reset coordinator must be extended and tested to remove any identity-bound APNs/FCM association.

After reset, Dyract generates a new PeerId. Existing contacts must treat it as a new identity unless a separately reviewed identity-migration protocol is introduced.

## Remote revocation limitation

The current single-device identity model has no independent credential capable of remotely revoking a stolen identity.

A local reset **does not revoke the old identity remotely**. If an attacker still possesses the old private key, the directory cannot safely accept a claim from the new unrelated identity that the old key is stolen.

Existing old-identity capability grants are no longer useful to the reset installation itself and naturally expire, but reset is not a remote compromise-remediation protocol.

Solving remote revocation requires a new trust mechanism, for example one of:

- previously provisioned offline recovery/revocation key;
- multi-device master identity with device certificates;
- strongly authenticated recovery credential;
- a redesigned identity hierarchy.

Each option materially changes the current trust model and requires its own threat analysis. Dyract must not implement support-driven/manual identity takeover as an undocumented back door.

## Multi-device implication

A future multi-device architecture should avoid copying one unrestricted long-term private key to every device if practical.

A stronger target architecture is:

```text
master/recovery identity
        |
        +-- signed device credential A
        +-- signed device credential B
        +-- signed device credential C
```

This can make individual-device revocation possible without changing the public identity, but introduces device lists, synchronization and revocation state that must be privacy-reviewed before implementation.

This is deferred until one-to-one single-device messaging is proven.

## Platform-key hardening still required

Current PKCS#8 export/import is a foundation, not the final production key boundary.

Before claiming hardware-backed/non-exportable identity protection, Dyract still needs platform-specific evaluation of:

### Android

- generating the identity key directly in Android Keystore;
- hardware-backed/StrongBox availability and fallback policy;
- ECDSA interoperability with Dyract's P-256 wire identity;
- authentication requirements on key use;
- migration/backup behavior;
- behavior on lock-screen credential changes and device restore.

### iOS

- Keychain accessibility class selection;
- Secure Enclave P-256 signing feasibility for the identity;
- non-exportable key reference persistence;
- biometric/passcode binding policy;
- reinstall/Keychain persistence semantics;
- device migration/recovery implications.

Until that work is implemented and independently reviewed, documentation should say that Dyract uses platform SecureStorage, not that its identity key is guaranteed hardware non-exportable.

## Recovery/security UX state

Implemented:

```text
Your PeerId / fingerprint
identity storage readable/unavailable state
recovery configured: no
explicit destructive reset identity and local data
reset reachable after unreadable initialized identity
contact capability/revocation controls elsewhere in contact UX
```

Still required before recovery can be claimed:

```text
reviewed encrypted recovery package export
verified restore flow
reviewed password/KDF design
physical-device reset validation on Android and iOS
app-lock option if adopted
```

Destructive or identity-changing actions show that contacts must verify/add the new identity again and that local reset is not remote revocation.

## Logging and telemetry rules

Never place these values in ordinary logs/analytics:

- private identity key/export;
- local-data key;
- recovery phrase/code/passphrase;
- decrypted recovery bundle;
- contact/message content;
- raw SecureStorage values.

Reset failures shown to the user use bounded status text rather than serializing secret-bearing exception contents into UI or telemetry.

## Current acceptance conclusion

Repository-side stolen-device/recovery analysis and destructive-reset implementation are complete when this document is kept aligned with implementation.

This does **not** mean encrypted identity recovery is implemented, and it does **not** constitute a mobile secure-storage audit.

Current decisions are:

- unreadable initialized identity fails closed;
- the explicit security screen remains available from that failure state;
- explicit two-step destructive identity/local-data reset is implemented and resumable;
- reset rotates both identity and local-data secrets and creates a visibly new PeerId;
- reinstall without explicit recovery means a new identity;
- there is no central key/message backup;
- there is currently no remote stolen-device revocation credential;
- encrypted identity recovery/export/restore remains future reviewed work;
- non-exportable Android Keystore / iOS Secure Enclave identity keys remain an open production-hardening evaluation.
