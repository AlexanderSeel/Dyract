# Local SQLite schema migrations

Dyract treats the local database as device-owned user data. Schema upgrades must therefore be deterministic, transactional and fail closed rather than rebuilding or silently discarding data.

## Current version

```text
SqliteSchemaMigrationRunner.CurrentVersion = 5
```

The historical database bootstrap still records:

```text
schema_info.version = 1
```

That value identifies the original schema. New upgrades are tracked through the append-only migration ledger:

```text
schema_migrations
  version      INTEGER PRIMARY KEY
  name         TEXT
  applied_utc  Unix milliseconds
```

Current migrations:

```text
1  baseline-v1
2  track-issued-contact-capability
3  durable-attachment-receive-state
4  bound-attachment-receive-reservations
5  durable-attachment-send-outbox
```

### Migration 1 — baseline-v1

Migration 1 is an **adoption migration**. It does not rewrite the historical schema. It verifies that the database identifies itself as exactly v1 and records that fact in the migration ledger.

### Migration 2 — track-issued-contact-capability

Migration 2 adds:

```sql
ALTER TABLE contacts
ADD COLUMN granted_capability BLOB NULL;
```

The column stores the capability **this device issued to that contact**, separately from the existing capability received from the contact. The value is AES-256-GCM protected with the local-data key.

### Migration 3 — durable-attachment-receive-state

Migration 3 adds restart-safe partial attachment receive state:

```text
attachment_receives
attachment_receive_chunks
```

The logical key is `(sender_peer_id, attachment_id)`. Chunk rows are deleted automatically when their parent receive state is removed.

The store deliberately keeps only bounded operational geometry in plaintext SQLite columns. Filename, MIME metadata, whole-file SHA-256 and chunk payloads are individually AES-256-GCM protected with the local-data key and associated-data contexts that bind them to the sender/attachment/chunk identity.

This is not a central attachment cache: the data remains inside the device-owned local database.

### Migration 4 — bound-attachment-receive-reservations

Migration 4 adds a SQLite `BEFORE INSERT` trigger that enforces prototype receive reservations atomically at the database boundary:

```text
maximum active receives globally: 16
maximum active receives per sender: 4
maximum declared active bytes globally: 512 MiB
maximum declared active bytes per sender: 200 MiB
```

The trigger uses `RAISE(ABORT, 'attachment_receive_quota')`. Enforcing the limits in SQLite prevents two concurrent application paths from independently passing an in-memory count check and exceeding the reservation policy.

The separate protocol maximum remains 100 MiB per attachment.

### Migration 5 — durable-attachment-send-outbox

Migration 5 adds the sender-owned attachment outbox:

```text
attachment_sends
attachment_send_chunks
```

A queued transfer is scoped by:

```text
(sender PeerId, recipient PeerId, attachment ID)
```

The sender snapshot stores encrypted filename, MIME metadata, SHA-256, chunk payloads and bounded failure diagnostics with the existing local-data key. Scheduling geometry such as attempts, due time, acknowledgement bits, PeerIds, sizes and chunk indices remains visible operational SQLite metadata.

Chunk rows use a deferred foreign key so `SqliteAttachmentSendStore.QueueAsync` can stream canonical chunks into one SQLite transaction, hash the complete snapshot, verify it against the manifest, insert the parent transfer record and commit atomically. A cancellation, malformed chunk sequence, hash mismatch, duplicate attachment ID or quota failure rolls the transaction back rather than leaving a partial sender snapshot.

Sender reservations use the same prototype bounds as receive state:

```text
maximum active sends globally: 16
maximum active sends per recipient: 4
maximum declared active bytes globally: 512 MiB
maximum declared active bytes per recipient: 200 MiB
```

Resume frames update durable per-chunk acknowledgement bits. The sender snapshot is **not** removed merely because the receiver reports no missing chunks; it remains until a manifest-bound final completion acknowledgement is accepted.

## Initialization

The shipping MAUI app resolves `ILocalStore` to `MigratingLocalStore`.

Initialization follows:

```text
historical v1 bootstrap (idempotent)
        ↓
open SQLite migration transaction
        ↓
validate legacy schema metadata
        ↓
validate ordered migration ledger
        ↓
apply each missing migration
        ↓
record each applied migration
        ↓
commit atomically
```

A legacy v1 database therefore upgrades in-place while preserving existing contacts/conversations/messages and adding all current schema objects and ledger rows.

## Invariants

1. Migration versions are positive, sequential integers beginning at 1.
2. An already committed migration is immutable; later policy changes append a new migration.
3. A migration and its ledger entry are committed in the same SQLite transaction.
4. A database claiming a newer schema/migration version than the running app supports is rejected.
5. Missing/gapped/malformed migration history is rejected.
6. Unknown databases are not automatically adopted as Dyract databases.
7. Existing encrypted user content is not decrypted/re-encrypted unless a migration explicitly requires it.
8. A schema migration never deletes conversations/messages merely because conversion failed.
9. Destructive migrations require an explicit backup/recovery design and separate review.
10. The migration runner does not weaken the local encryption boundary; it changes schema metadata/structure only.

## Adding migration v6+

When the next real schema change is needed:

1. increase `SqliteSchemaMigrationRunner.CurrentVersion`;
2. append the next immutable `MigrationDefinition`;
3. keep the migration deterministic and transactional;
4. add previous-version -> current-version tests;
5. add a fresh-database test;
6. add failure/rollback coverage when multiple writes are involved;
7. verify existing encrypted content remains decryptable;
8. run core CI and both mobile platform builds before release.

Do **not** advance the schema for bookkeeping alone.

## Current automated coverage

The suite verifies:

- a fresh migrating database records migrations 1 through 5 exactly once;
- migration 2 creates `contacts.granted_capability`;
- migrations 3 and 4 create attachment receive tables and the receive quota trigger;
- migration 5 creates attachment sender/outbox tables and its quota trigger;
- an existing historical v1 database upgrades to the current version without losing encrypted contact data;
- a database from a newer Dyract build is rejected;
- malformed legacy schema metadata is rejected;
- partial attachment receive state survives a fresh store instance and produces the expected missing ranges;
- exact duplicate receive manifests/chunks are idempotent while changed-content collisions fail closed;
- attachment receive state is sender-peer scoped;
- sensitive receive filename/chunk sentinels do not appear as plaintext in the checkpointed SQLite database;
- the per-sender active receive limit is enforced at the database boundary;
- sender snapshots survive a fresh store instance and preserve canonical chunk retry state;
- sender chunk payloads are encrypted at rest;
- sender snapshot hash mismatch rolls the entire queue operation back;
- progress resumes acknowledge already-received chunks while retaining missing chunks for retransmission;
- final completion is recipient-scoped and removes sender state only when attachment ID and SHA-256 match;
- the per-recipient active send limit is enforced at the database boundary;
- destructive identity reset clears both partial receive and queued send attachment state while still supporting databases created before attachment tables existed.

## Recovery boundary

Schema migration is not a backup system. Identity recovery, encrypted database export/backup and reinstall recovery are separate product/security decisions. Until those exist, migration code stays conservative: preserving an unreadable database for recovery is preferable to deleting it and silently creating an empty replacement.
