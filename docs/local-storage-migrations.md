# Local SQLite schema migrations

Dyract treats the local database as device-owned user data. Schema upgrades must therefore be deterministic, transactional and fail closed rather than rebuilding or silently discarding data.

## Current version

```text
SqliteSchemaMigrationRunner.CurrentVersion = 2
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
```

### Migration 1 — baseline-v1

Migration 1 is an **adoption migration**. It does not rewrite the historical schema. It verifies that the database identifies itself as exactly v1 and records that fact in the migration ledger.

### Migration 2 — track-issued-contact-capability

Migration 2 is the first real schema upgrade:

```sql
ALTER TABLE contacts
ADD COLUMN granted_capability BLOB NULL;
```

The column stores the capability **this device issued to that contact**, separately from the existing capability received from the contact.

The issued capability is encrypted by `SqliteIssuedCapabilityStore` with AES-256-GCM and the local-data key. Its associated-data context includes the exact contact PeerId.

This enables targeted grant reuse and pre-expiry revocation without putting the outgoing permission into the incoming-capability field or creating a server-side contact graph.

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

A legacy v1 database therefore becomes:

```text
existing encrypted contacts/messages remain unchanged
        +
granted_capability column
        +
schema_migrations rows 1 and 2
```

## Invariants

1. Migration versions are positive, sequential integers beginning at 1.
2. An already released migration is immutable.
3. A migration and its ledger entry are committed in the same SQLite transaction.
4. A database claiming a newer schema/migration version than the running app supports is rejected.
5. Missing/gapped/malformed migration history is rejected.
6. Unknown databases are not automatically adopted as Dyract databases.
7. Existing encrypted user content is not decrypted/re-encrypted unless a migration explicitly requires it.
8. A schema migration never deletes conversations/messages merely because conversion failed.
9. Destructive migrations require an explicit backup/recovery design and separate review.
10. The migration runner does not weaken the local encryption boundary; it changes schema metadata/structure only.

## Adding migration v3+

When the next real schema change is needed:

1. increase `SqliteSchemaMigrationRunner.CurrentVersion`;
2. append the next immutable `MigrationDefinition`;
3. keep the migration deterministic and transactional;
4. add previous-version -> current-version tests;
5. add a fresh-database test;
6. add failure/rollback coverage when multiple writes are involved;
7. verify existing encrypted contacts/conversations/messages remain decryptable;
8. run core CI and both mobile platform builds before release.

Conceptually:

```csharp
new(3, "meaningful-schema-change", """
    ALTER TABLE example ADD COLUMN value TEXT NULL;
    """)
```

Do **not** advance the schema for bookkeeping alone. Migration 2 exists because it adds real per-contact issued-grant state.

## Current automated coverage

The suite verifies:

- a fresh migrating database records migrations 1 and 2 exactly once;
- migration 2 creates `contacts.granted_capability`;
- an existing historical v1 database upgrades to v2 without losing encrypted contact data;
- a database from a newer Dyract build is rejected;
- malformed legacy schema metadata is rejected;
- issued capabilities round-trip through encrypted local storage;
- raw SQLite issued-capability bytes do not contain the stored plaintext value;
- a wrong local encryption key fails AEAD authentication;
- issued capabilities cannot be attached to an unknown contact.

## Recovery boundary

Schema migration is not a backup system. Identity recovery, encrypted database export/backup and reinstall recovery are separate product/security decisions. Until those exist, migration code stays conservative: preserving an unreadable database for recovery is preferable to deleting it and silently creating an empty replacement.
