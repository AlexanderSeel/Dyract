# PostgreSQL schema migrations

Dyract's directory stores only server-owned metadata. PostgreSQL currently persists the durable identity registry; presence, replay nonces, signaling and capability revocations remain short-lived prototype state.

The server no longer relies on ad-hoc table bootstrap inside `PostgresIdentityStore`. Schema ownership is centralized in:

```text
PostgresSchemaMigrator
PostgresSchemaInitializer
```

## Current schema version

```text
PostgresSchemaMigrator.CurrentVersion = 1
```

Migration ledger:

```sql
CREATE TABLE dyract_schema_migrations
(
    version     integer PRIMARY KEY,
    name        text NOT NULL,
    applied_at  timestamptz NOT NULL
);
```

Current migration:

```text
1  create-peer-identity
```

which owns:

```sql
CREATE TABLE peer_identity
(
    peer_id       text PRIMARY KEY,
    public_key    bytea NOT NULL,
    registered_at timestamptz NOT NULL
);
```

## Startup behavior

When a PostgreSQL `ConnectionStrings:Dyract` value is configured, the server registers the PostgreSQL identity store and runs the schema migrator before normal operation.

Conceptually:

```text
open PostgreSQL connection
        ↓
begin transaction
        ↓
pg_advisory_xact_lock(DYRACT)
        ↓
create/validate migration ledger
        ↓
validate ordered migration history
        ↓
apply missing migrations
        ↓
validate critical table shape
        ↓
record migration rows
        ↓
commit transaction
```

The transaction-scoped PostgreSQL advisory lock serializes concurrent application instances during migration. The lock is released automatically with the transaction.

## Existing database adoption

Migration 1 uses `CREATE TABLE IF NOT EXISTS`, but that does **not** mean an arbitrary pre-existing table is trusted.

Before recording migration 1, Dyract validates that `peer_identity` has the expected critical shape:

- `peer_id text NOT NULL`;
- `public_key bytea NOT NULL`;
- `registered_at timestamp with time zone NOT NULL`;
- primary key exactly on `peer_id`.

If an existing table does not match, startup fails and the migration transaction rolls back. Dyract does not silently alter an unknown/malformed production identity table.

## Migration invariants

1. Migration versions are positive sequential integers beginning at 1.
2. A released migration is immutable.
3. Schema changes and their migration-ledger rows commit in the same transaction.
4. Multiple instances are serialized with the migration advisory lock.
5. A migration history with gaps is rejected.
6. A database with a migration version newer than the running build is rejected.
7. Critical existing schema shapes are validated before adoption.
8. Migration failure must fail application startup rather than falling back to an empty identity registry.
9. Database credentials are never embedded in migration source or workflow logs.
10. Future destructive migrations require an explicit backup/rollback plan and separate review.

## Adding migration v2+

For a real server schema change:

1. increment `PostgresSchemaMigrator.CurrentVersion`;
2. append the next immutable migration definition;
3. add previous-version → current-version integration coverage;
4. test concurrent migrators;
5. test failure/rollback behavior;
6. validate any security-sensitive resulting table/index/constraint shape;
7. run the PostgreSQL CI job before release.

Do not rewrite migration 1 to change a deployed schema. Add migration 2 instead.

## PostgreSQL CI

`.github/workflows/ci.yml` includes a dedicated PostgreSQL integration job using PostgreSQL 18.

The job sets a test-only connection string through:

```text
DYRACT_POSTGRES_TEST_CONNECTION
```

and executes only the real PostgreSQL migration tests against the service container.

Current integration coverage proves:

- fresh migration succeeds;
- running the migrator repeatedly is idempotent;
- `PostgresIdentityStore` works after migration;
- four concurrent migrators serialize correctly;
- malformed existing identity schema is rejected and rolled back;
- a future migration version is rejected.

The ordinary unit/integration suite still runs without requiring PostgreSQL; the dedicated CI job ensures the PostgreSQL-specific tests are not accidentally skipped in repository validation.

## Production boundary

PostgreSQL currently persists identity registrations only. Dyract's remaining ephemeral server state is intentionally separate:

```text
presence leases
replay nonces
WebRTC signaling
capability revocations
```

For a horizontally scaled production directory, those TTL datasets need shared state—typically Redis or another TTL-capable distributed store—while preserving Dyract's metadata-minimization rules. In particular, capability revocation state must remain durable across process restarts for the remaining lifetime of the revoked capability without adding the grantee/contact graph to server storage.
