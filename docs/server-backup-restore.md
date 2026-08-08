# Server metadata backup and restore policy

## Scope

Dyract deliberately keeps the durable server dataset small.

The PostgreSQL directory currently persists only:

```text
peer_identity
capability_revocation
dyract_schema_migrations
```

The backup policy therefore protects **directory identity continuity and active capability-revocation state**, not user conversations.

Dyract's server does not contain chat history, contact display names or attachments to recover.

## Data classification

### Durable PostgreSQL data — back up

```text
peer_identity.peer_id
peer_identity.public_key
peer_identity.registered_at
capability_revocation.issuer_peer_id
capability_revocation.capability_id
capability_revocation.expires_at
dyract_schema_migrations.*
```

Although public identity keys are not secret keys, the combined registry is operational/security metadata and must be access controlled.

Capability revocations are especially important for continuity: restoring a snapshot that predates a still-active revocation can temporarily resurrect a grant until it naturally expires. Restore procedures therefore need explicit freshness/reconciliation rules.

### Redis state — do not treat as durable backup data

Current Redis datasets are intentionally short lived:

```text
registration challenges
replay nonces
presence leases
WebRTC signaling inboxes
shared request-rate counters
```

Dyract does not require application-level backup/restore of those values.

After a Redis loss/restart:

- challenges must be requested again;
- replay windows reset, which is an availability/security consideration and should be recorded operationally;
- clients republish presence;
- peers repeat signaling as needed;
- rate-limit windows restart.

Redis persistence settings may still be chosen for infrastructure availability, but those files are not a source of authoritative long-term Dyract user data.

### Never include in a server backup

Dyract directory backup processes must never invent or collect:

- device private identity keys;
- local-data encryption keys;
- recovery secrets;
- contact display names/contact lists;
- conversation/message/attachment content;
- decrypted device databases;
- APNs/FCM message content.

If future server features require new durable tables, this document and the threat model must be updated before production deployment.

## Backup format

For PostgreSQL, prefer a native logical backup such as `pg_dump` custom format or a managed-service equivalent that preserves transactional consistency and supports controlled restore.

Conceptual command:

```bash
pg_dump \
  --format=custom \
  --no-owner \
  --no-privileges \
  --file=dyract-directory-<timestamp>.dump \
  "$DYRACT_POSTGRES_BACKUP_CONNECTION"
```

Do not place credentials directly in shell history, repository files, CI logs or backup filenames. Use the hosting platform's protected credential mechanism.

Physical managed-database snapshots are also acceptable if their encryption/access/restore semantics are documented and tested.

## Encryption and access

Production backups must:

1. be encrypted at rest using the backup/storage platform;
2. be encrypted in transit during creation/copy/restore;
3. be readable only by the minimum operator/service roles required for backup and recovery;
4. use credentials distinct from ordinary read-only observability users;
5. have access/audit logging enabled where the platform supports it;
6. never expose database credentials in backup artifacts;
7. never be published as CI artifacts from production data.

If an additional application-controlled encryption layer is adopted, its encryption key must be stored separately from the backup object itself.

## Retention policy

A concrete production retention period must be selected with the hosting/legal/operations context. Dyract's privacy posture favors the shortest period that still meets disaster-recovery needs.

Before production launch, record:

```text
backup frequency
retention duration
number of retained generations
geographic/region replication policy
delete/expiry mechanism
operator roles
restore RPO target
restore RTO target
```

Do not keep indefinite historical identity-registry snapshots by default.

## Revocation freshness rule

A database restore can roll back `capability_revocation` rows. This is a security-sensitive difference from ordinary identity metadata.

Production operations must choose one of these reviewed approaches:

1. managed database point-in-time recovery with an RPO short enough for the accepted threat model;
2. an append-only/replicated security-event strategy for active revocations;
3. post-restore reconciliation from another authoritative security log that itself follows Dyract metadata-minimization rules.

Until such production infrastructure is selected, the minimum restore rule is:

> Restore the newest verified database state available and treat the loss window as a security incident requiring review before public traffic resumes.

Do not silently restore an old backup and immediately resume service while pretending all previous revocations remain enforced.

## Restore procedure

A controlled restore should follow this sequence:

```text
stop / isolate public directory traffic
        ↓
provision clean PostgreSQL target
        ↓
restore newest verified backup / PITR point
        ↓
start one Dyract instance against restored DB
        ↓
PostgresSchemaMigrator runs
        ↓
schema history + table shapes validate
        ↓
verify identity/revocation health checks/tests
        ↓
review backup-loss / revocation-loss window
        ↓
reconnect fresh Redis (empty is acceptable)
        ↓
clients republish presence / renegotiate
        ↓
resume traffic
```

Dyract's startup migrator must remain fail-closed. A malformed/future/incompatible restored schema is not auto-rebuilt or silently replaced.

## Restore verification

At minimum verify:

- migration ledger reaches the expected application schema version;
- `peer_identity` schema shape matches the running build;
- `capability_revocation` schema shape contains no grantee/contact-graph column;
- registered identity rows are readable through `PostgresIdentityStore`;
- active revocation rows are readable through `PostgresCapabilityRevocationStore`;
- an expired revocation is treated as expired;
- a known active revocation is still enforced before reopening traffic;
- configured Redis state can start empty and clients can reconstruct transient state.

Never validate a production restore by sending real user metadata into public CI.

## Restore drills

A backup is not considered operationally trustworthy merely because it was created successfully.

Before production launch and periodically afterward, execute a restore drill into an isolated environment using production-equivalent schema/backup tooling but appropriately protected data.

Record:

```text
backup identifier/time
restore start/end time
schema validation result
application smoke-test result
revocation freshness/loss-window result
operator/remediation notes
```

A failed drill blocks claims that backup/restore is production-ready.

## Deletion and decommissioning

Expired backups should be removed by lifecycle policy, including replicated copies where the storage platform permits it.

When a production environment is permanently decommissioned, separately delete:

- active database copies;
- retained backups/snapshots after the approved retention/hold period;
- database/backup credentials;
- derived temporary restore environments.

## Current PLAN acceptance

Repository-side **server metadata backup/restore policy** is defined by this document.

Still open before production:

- choose concrete PostgreSQL hosting/PITR mechanism;
- set explicit RPO/RTO/frequency/retention values;
- configure backup encryption/access/lifecycle controls;
- execute and record a production-equivalent restore drill;
- decide the accepted/reconciled capability-revocation rollback window.

Therefore the policy/documentation task can be marked complete, while operational backup/restore deployment and drill validation remain open infrastructure tasks.
