# Platform identity keys and non-exportable signing

## Current status

Dyract now separates the **protocol identity-signing contract** from the current exportable software-key implementation.

Shared protocol/client code depends on:

```csharp
public interface IPeerIdentitySigner
{
    PeerId PeerId { get; }
    byte[] ExportPublicKey();
    byte[] Sign(ReadOnlySpan<byte> payload);
}
```

The contract intentionally has no private-key export operation.

`PeerIdentity` remains the current software implementation and still supports PKCS#8 export because the shipping MAUI `SecureIdentityVault` currently persists that material through platform `SecureStorage`. This is a transition architecture, not a claim that the current mobile identity key is non-exportable.

## Protocol consumers migrated to the signer boundary

The following operations no longer require concrete `PeerIdentity` or access to private-key bytes:

- contact invitation creation;
- contact capability issuance;
- directory registration;
- signed peer lookup;
- presence publish/removal;
- capability revocation;
- capability-protected resolve;
- signaling send/fetch/ACK;
- authenticated `DYSH` initiator hello signing;
- authenticated `DYSH` responder response signing.

The session handshake wire format, public-key representation and P-256 signature format are unchanged.

## Why this matters

A platform-native identity key should ideally expose only public identity material and signing operations:

```text
protocol proof bytes
        ↓
IPeerIdentitySigner.Sign(...)
        ↓
platform key handle
        ↓
P-256 IEEE-P1363 signature
```

The application should not need to export the private scalar merely to sign a directory request or peer handshake.

This boundary is prerequisite work for Android Keystore / iOS Secure Enclave implementations.

## Repository proof

`IdentitySignerAbstractionTests` uses a deliberately non-exporting wrapper that implements only `IPeerIdentitySigner`. The wrapper internally delegates to a test `PeerIdentity`, but does not expose any private-export API to protocol callers.

The test proves the abstraction is sufficient for:

1. contact invitation generation;
2. directory registration;
3. contact capability issuance;
4. capability-protected resolve;
5. signaling send/fetch/ACK;
6. `DYSH` authenticated session establishment;
7. `DYSE` encrypted application data.

A reflection assertion also prevents accidental addition of a private-key/PKCS#8 export member to `IPeerIdentitySigner`.

This proves dependency shape only. It does **not** prove hardware-backed or non-exportable mobile key storage.

## Android target design

The next Android-specific experiment should evaluate generating the Dyract P-256 identity directly in Android Keystore and retaining only an alias/key handle.

Acceptance questions:

- can Keystore produce the exact ECDSA/SHA-256 signature semantics expected by Dyract;
- can signatures be converted/produced in the fixed 64-byte IEEE-P1363 representation used by the protocol;
- is the public SPKI stable and sufficient to derive the existing PeerId;
- what hardware-backed/StrongBox availability exists on supported devices;
- what fallback policy applies when hardware backing is unavailable;
- what happens after lock-screen credential changes, device transfer/restore, app reinstall and OS upgrade;
- should key use require biometric/device authentication, and what usability/background-delivery effects follow;
- how migration from the existing PKCS#8 SecureStorage identity would work without silently changing PeerId.

No Android hardware/non-exportable claim should be made until physical-device tests verify these properties.

## iOS target design

The iOS experiment should evaluate a Keychain/Secure Enclave P-256 signing key referenced by a persistent key handle rather than exported private bytes.

Acceptance questions:

- whether Secure Enclave ECDSA signatures interoperate with the current Dyract P-256 identity format;
- DER vs IEEE-P1363 signature conversion and canonical validation;
- stable public SPKI/PeerId derivation;
- Keychain accessibility class;
- passcode/biometric access-control policy;
- reinstall and Keychain persistence behavior;
- device migration and recovery implications;
- whether a restored identity can remain the same identity without exporting the Secure Enclave private key.

Secure Enclave evaluation remains open until tested on a physical iPhone.

## Migration boundary

Dyract already has installations whose identity is represented as PKCS#8 in `SecureStorage`. Replacing the implementation must not silently generate a new identity merely because a platform-native key path is introduced.

A production migration design needs one of these explicit outcomes:

1. retain the existing identity in the legacy protected form;
2. explicitly create a new platform-native identity and tell the user/contacts that the PeerId changed;
3. use a reviewed identity migration/recovery mechanism capable of preserving continuity where technically possible.

Do not automatically rotate identities during an app upgrade.

## Recovery interaction

Non-exportable keys improve resistance to key extraction but complicate recovery.

Identity continuity across a destroyed device cannot rely on exporting a Secure Enclave/Keystore key after the fact. Recovery therefore requires a separately designed trust mechanism such as a pre-provisioned encrypted recovery credential or a future master/device identity hierarchy.

See `docs/device-compromise-recovery.md`.

## Current PLAN acceptance

Completed repository prerequisite:

- protocol/client signing no longer requires private-key export.

Still open:

- Android Keystore non-exportable signer implementation and physical-device evaluation;
- hardware-backed/StrongBox fallback policy;
- iOS Keychain/Secure Enclave signer implementation and physical-device evaluation;
- migration/identity-continuity design for existing installations;
- recovery interaction design;
- independent mobile secure-storage review.

These remain separate PLAN items and must not be marked complete from the abstraction alone.
