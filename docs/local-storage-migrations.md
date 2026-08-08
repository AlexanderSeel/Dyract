# Local SQLite schema migrations

Dyract treats the local database as device-owned user data. Schema upgrades must therefore be deterministic, transactional and fail closed rather than rebuilding or silently discarding data.

## Current state

The original local schema shipped before a formal migration runner existed. It records:

```text
schema_info.version = 1
```

`SqliteSchemaMigrationRunner` now adds an append-only migration ledger:

```text
schema_migrations
  version      INTEGER PRIMARY KEY
  name         TEXT
  applied_utc  Unix milliseconds
```

Migration `1 / baseline-v1` is an **adoption migration**. It does not rewrite the existing schema. It verifies that the historical database identifies itself as exactly v1, then records that fact in the new ledger.

The shipping MAUI app resolves `ILocalStore` to `MigratingLocalStore`. Initialization therefore follows:

```text
historical v1 bootstrap (idempotent)
        ↓
open migration transaction
        ↓
validate legacy schema metadata
        ↓
validate migration ledger
        ↓
apply missing ordered migrations
        ↓
record each applied migration
        ↓
commit atomically
```

## Invariants

1. Migration versions are positive, sequential integers beginning at 1.
2. An already released migration is immutable.
3. A migration and its ledger entry are committed in the same SQLite transaction.
4. A database claiming a newer schema/migration version than the running app supports is rejected.
5. Missing/gapped/malformed migration history is rejected.
6. Unknown databases are not automatically adopted as Dyract databases.
7. Existing encrypted user content must not be decrypted/re-encrypted unless a migration explicitly requires it.
8. A schema migration must never delete conversations/messages merely because conversion failed.
9. Destructive migrations require an explicit backup/recovery design and separate review.
10. The migration runner does not weaken the local encryption boundary; it changes schema metadata/structure only.

## Adding migration v2+

When a real schema change is needed:

1. increase `SqliteSchemaMigrationRunner.CurrentVersion`;
2. append a `MigrationDefinition` with the next version and a stable name;
3. provide idempotence only through the migration ledger—do not use a migration to repeatedly mutate data;
4. add upgrade tests beginning from the previous released schema;
5. add a fresh-database test;
6. add a failure/rollback test when the migration performs multiple writes;
7. verify existing encrypted contacts/conversations/messages still decrypt after upgrade;
8. run core CI and both mobile platform builds before release.

Conceptually:

```csharp
new(2, "add-example-column", """
    ALTER TABLE example ADD COLUMN value TEXT NULL;
    """)
```

Do **not** insert artificial columns merely to advance the version. The migration version should change only when the schema actually changes.

## Tests currently required

The current automated suite verifies:

- a fresh database records `baseline-v1` exactly once;
- an existing historical v1 database is adopted without losing encrypted contact content;
- a database from a newer Dyract build is rejected;
- malformed legacy schema metadata is rejected.

Every later migration should add explicit previous-version → current-version coverage.

## Recovery boundary

Schema migrations are not a backup system. Identity recovery, encrypted database export/backup and reinstall recovery are separate product/security decisions. Until those exist, migration code should be deliberately conservative: preserving an unreadable database for manual recovery is preferable to deleting it and creating an empty replacement.
