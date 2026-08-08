# Platform identity keys and non-exportable signing

## Current status

Dyract separates the **protocol identity-signing contract** from the current exportable software-key implementation.

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

`PeerIdentity` remains the **shipping** software implementation and still supports PKCS#8 export because the current MAUI `SecureIdentityVault` persists that material through platform `SecureStorage`. This is a transition architecture, not a claim that the current mobile identity key is non-exportable.

Two inactive platform-native signer experiments now also exist:

```text
AndroidKeystoreIdentitySigner
IosSecureEnclaveIdentitySigner
```

Both compile in the shipping Android/iOS project surfaces. Neither is selected by `SecureIdentityVault`, and neither has physical-device/runtime/migration acceptance yet.

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

## Shared platform crypto normalization

Platform key APIs do not expose exactly the same representations as Dyract's current managed crypto API. Conversion is centralized in `Dyract.Crypto` rather than duplicated per platform.

### ECDSA signature encoding

`EcdsaSignatureEncoding.DerToP256P1363(...)` converts canonical ASN.1 DER ECDSA `(r,s)` signatures into Dyract's fixed 64-byte IEEE-P1363 P-256 representation.

It rejects:

- empty/malformed DER;
- trailing ASN.1 data;
- non-positive integers;
- coordinates larger than 32 bytes.

### Public identity encoding

`P256PublicKeyEncoding.UncompressedPointToSubjectPublicKeyInfo(...)` converts the 65-byte uncompressed X9.63 P-256 point:

```text
04 || X[32] || Y[32]
```

into the same SubjectPublicKeyInfo representation already used to derive Dyract PeerIds.

Regression tests prove that converting a generated P-256 raw point preserves the same derived PeerId.

## Why this matters

A platform-native identity key should expose only public identity material and signing operations:

```text
protocol proof bytes
        ↓
IPeerIdentitySigner.Sign(...)
        ↓
platform key handle
        ↓
P-256 signature
        ↓
shared canonical P1363 representation
```

The application does not need to export the private scalar merely to sign a directory request or peer handshake.

## Repository proof of the abstraction

`IdentitySignerAbstractionTests` uses a deliberately non-exporting wrapper that implements only `IPeerIdentitySigner`. The wrapper internally delegates to a test `PeerIdentity`, but does not expose any private-export API to protocol callers.

The test proves the abstraction is sufficient for:

1. contact invitation generation;
2. directory registration;
3. contact capability issuance;
4. capability-protected resolve;
5. signaling send/fetch/ACK;
6. `DYSH` authenticated session establishment;
7. `DYSE` encrypted application data.

A reflection assertion prevents accidental addition of a private-key/PKCS#8 export member to `IPeerIdentitySigner`.

This proves dependency shape only. It does **not** prove hardware-backed or non-exportable mobile key storage.

## Android Keystore experiment

`AndroidKeystoreIdentitySigner` is an inactive Android-only implementation of `IPeerIdentitySigner`.

Current design:

- AndroidKeyStore provider;
- alias-held EC private key;
- `secp256r1` / NIST P-256;
- signing restricted to SHA-256 ECDSA;
- public certificate key exported only as SPKI;
- private key never exported through the Dyract interface;
- Android DER ECDSA signature converted by the shared DER -> P1363 helper.

The implementation now compiles successfully in the shipping `.NET 10` Android Release build.

### Android status that is **not** yet proven

Compile success does not establish:

- physical-device signing behavior;
- alias persistence over process/app lifecycle;
- whether the key is hardware-backed on each supported device;
- StrongBox availability or fallback policy;
- behavior after lock-screen credential changes, OS upgrade or device restore;
- reinstall/uninstall behavior;
- acceptable authentication/biometric-on-use policy;
- migration of existing PKCS#8 identities without changing PeerId.

Therefore the Android signer remains inactive until physical-device/security acceptance is complete.

## iOS Secure Enclave experiment

`IosSecureEnclaveIdentitySigner` is an inactive iOS-only implementation of `IPeerIdentitySigner`.

Current design:

- persistent `SecKey` identified by application tag;
- EC P-256 / `SecTokenID.SecureEnclave` generation request;
- private key query through Keychain/Security APIs;
- signing through `EcdsaSignatureMessageX962Sha256`;
- public key obtained through `SecKey.GetPublicKey()` and external public representation;
- raw X9.63 public point normalized through the shared SPKI helper;
- DER ECDSA signature normalized through the shared P1363 helper;
- private key never exported through the Dyract interface.

The implementation compiles successfully for the `iossimulator-arm64` Release target on the current macOS 26 / Xcode 26.6 CI image.

### iOS status that is **not** yet proven

The simulator does not prove Secure Enclave runtime behavior. Still required on a physical iPhone:

- actual Secure Enclave key generation and lookup;
- signing interoperability with Dyract verification;
- stable public SPKI/PeerId derivation;
- Keychain accessibility semantics;
- passcode/biometric access-control policy;
- process restart persistence;
- uninstall/reinstall Keychain behavior;
- device migration/restore behavior;
- recovery implications for a non-exportable Secure Enclave key.

The iOS signer remains inactive until that evidence and migration policy exist.

## Shipping identity remains unchanged

`SecureIdentityVault` still uses the existing explicit flow:

```text
PeerIdentity.Generate()
        ↓
ExportPkcs8PrivateKey()
        ↓
MAUI SecureStorage
```

This is deliberate. Enabling a newly generated native platform key for an existing installation would change its PeerId unless an explicit identity-continuity mechanism exists.

The platform-native experiments must therefore not be silently selected during an app update.

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

Completed repository prerequisites:

- protocol/client signing no longer requires private-key export;
- canonical platform ECDSA/public-key encoding helpers are implemented/tested;
- Android Keystore signer implementation compiles in Android Release;
- iOS Secure Enclave signer implementation compiles in iOS simulator Release.

Still open:

- Android physical-device Keystore signer runtime evaluation;
- hardware-backed/StrongBox detection and fallback policy;
- iOS physical-device Secure Enclave runtime evaluation;
- Keychain accessibility/biometric policy;
- migration/identity-continuity design for existing installations;
- activation strategy in `SecureIdentityVault`;
- recovery interaction design;
- independent mobile secure-storage review.

Compile-level implementation must not be described as physical non-exportability or hardware-backed runtime acceptance.
