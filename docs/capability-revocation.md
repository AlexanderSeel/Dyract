# Contact capability issuance and revocation

Dyract separates identity pinning from reachability authorization.

A saved contact identity answers:

```text
Who is this peer?
```

A contact capability answers:

```text
May this exact peer ask the directory for my current reachability/signaling metadata?
```

The two operations remain independent.

## Capability direction

For a contact Bob stored on Alice's device:

```text
Bob -> Alice capability
```

means Alice may resolve/signal Bob.

```text
Alice -> Bob capability
```

means Bob may resolve/signal Alice.

The shipping UI therefore treats these as two distinct states:

```text
You may resolve them until ...
They may resolve you until ...
```

Revoking Alice's grant to Bob does not remove Bob as a contact and does not revoke the separate grant Bob issued to Alice.

## Issuance policy

Capabilities are:

- signed by the issuer's long-term Dyract identity;
- bound to one exact grantee PeerId;
- assigned a random 128-bit capability ID;
- time bounded;
- currently version 1;
- limited to a maximum lifetime of 90 days;
- issued by the MAUI app with a default lifetime of 30 days.

The mobile app stores the capability it issued to each contact separately from the capability it received from that contact.

Schema v2 adds:

```text
contacts.granted_capability BLOB NULL
```

The value is AES-256-GCM encrypted with the local-data key and associated with the exact contact PeerId.

Copying a pairing response and showing its QR reuse the same still-valid locally tracked capability. Dyract does not silently create multiple simultaneously active grants for the same contact. To rotate immediately, revoke the current grant first and then create a new pairing response.

## Revocation request

The issuer can revoke a capability before its natural expiry through:

```text
POST /api/v1/capability/revoke
```

The signed request contains:

```text
IssuerPeerId
CapabilityId
CapabilityExpiresUnixSeconds
TimestampUnixSeconds
Nonce
Signature
```

Canonical signed proof:

```text
dyract:contact-capability-revoke:v1
<issuer PeerId>
<capability ID>
<original capability expiry>
<request timestamp>
<nonce>
```

The directory verifies:

1. issuer PeerId is valid and registered;
2. request timestamp is fresh;
3. nonce is sufficiently random and has not been replayed;
4. capability ID has the expected 128-bit hexadecimal shape;
5. claimed natural expiry is still in the future and within the supported capability horizon;
6. signature verifies against the issuer's registered public key.

Revocation is idempotent. Repeating a correctly signed revocation for the same capability does not recreate access.

## Server-side metadata boundary

Revocation state deliberately records only:

```text
issuer PeerId
capability ID
natural expiry
```

It does **not** record the grantee PeerId.

Therefore the revocation store does not become a server-side contact/friendship graph. The grantee remains cryptographically embedded in the capability held by the peer, but is not copied into revocation state.

Revoked IDs cease to authorize after their natural expiry. The store also caps active revoked IDs at 512 per issuer as a prototype abuse bound.

### PostgreSQL-backed directory

When `ConnectionStrings:Dyract` is configured, Dyract uses `PostgresCapabilityRevocationStore`. Revocations are persisted by PostgreSQL migration v2 in:

```text
capability_revocation
```

with only:

```text
issuer_peer_id
capability_id
expires_at
```

A successful revoke is committed before the HTTP endpoint returns success. Both resolve and signaling authorization read the durable store, so a server process restart does not resurrect a revoked capability. Multiple directory instances sharing the same PostgreSQL database observe the same revocation state without a per-process cache.

Expired rows are opportunistically removed for the issuer during later revoke operations. Per-issuer capacity checks are serialized with a PostgreSQL advisory transaction lock.

### Zero-config development

Without a PostgreSQL connection string, Dyract uses the in-memory `CapabilityRevocationStore`. That mode is intentionally for local development/tests and does not survive process restart.

## Authorization behavior

A revoked capability is rejected by both:

```text
/api/v1/peer/resolve
/api/v1/signal/send
```

This is important: revocation must not merely hide an IP/ICE candidate while leaving WebRTC signaling usable.

The validation order includes:

- capability version/scope;
- capability ID structure;
- supported lifetime and expiry;
- revocation state;
- issuer signature.

A revoked capability returns the authorization error:

```text
capability_revoked
```

A newly issued capability with a different random capability ID remains usable.

## Local revoke UX

The conversation/contact screen exposes:

```text
Revoke my grant
```

only when:

- this installation has a tracked issued capability for that contact;
- the capability has not naturally expired;
- an HTTPS Dyract directory is configured.

After a successful server revocation, the locally encrypted issued-capability value is cleared. The next pairing QR/copy operation creates a new capability ID.

If revocation fails, the local capability is retained so the user can retry; Dyract does not pretend the remote peer lost access when the directory did not confirm revocation.

## Validation coverage

The PostgreSQL integration suite proves that:

- migration v2 creates the metadata-minimized revocation table;
- no grantee column is present;
- a revocation created by one store instance is visible to a fresh store instance;
- natural expiry stops the row from being considered active;
- malformed revocation schemas are rejected before migration v2 is recorded;
- later schema drift is rejected even when the migration ledger already reports v2 complete.

The normal ASP.NET integration suite separately proves that revoked capabilities cannot use resolve or signaling and that a replacement capability with a new ID remains usable.

## Security boundary

Revocation prevents subsequent authorized directory resolve/signaling with the revoked capability. It cannot retroactively erase metadata a peer already learned while the grant was valid, and it cannot terminate a previously established authenticated peer session by itself.

A later production transport integration should therefore also close/reject future sessions when local policy changes, and define whether revoking a grant should proactively close an already-open connection.
