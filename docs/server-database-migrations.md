# PostgreSQL schema migrations

Dyract's directory stores only server-owned metadata. PostgreSQL persists the durable identity registry and, as of schema v2, metadata-minimized capability revocations. Presence leases, replay nonces and WebRTC signaling remain short-lived prototype state.

The server no longer relies on ad-hoc table bootstrap inside `PostgresIdentityStore`. Schema ownership is centralized in:

```text
PostgresSchemaMigrator
PostgresSchemaInitializer
```

## Current schema version

```text
PostgresSchemaMigrator.CurrentVersion = 2
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

Current migrations:

```text
1  create-peer-identity
2  persist-capability-revocations
```

Migration 1 owns:

```sql
CREATE TABLE peer_identity
(
    peer_id       text PRIMARY KEY,
    public_key    bytea NOT NULL,
    registered_at timestamptz NOT NULL
);
```

Migration 2 owns:

```sql
CREATE TABLE capability_revocation
(
    issuer_peer_id text NOT NULL REFERENCES peer_identity(peer_id) ON DELETE CASCADE,
    capability_id  text NOT NULL,
    expires_at     timestamptz NOT NULL,
    PRIMARY KEY (issuer_peer_id, capability_id)
);

CREATE INDEX ix_capability_revocation_expires_at
    ON capability_revocation(expires_at);
```

The revocation table deliberately contains no grantee/contact column. It records only the issuer, opaque capability ID and natural expiry required to deny a previously issued grant.

## Startup behavior

When a PostgreSQL `ConnectionStrings:Dyract` value is configured, the server registers both `PostgresIdentityStore` and `PostgresCapabilityRevocationStore` and runs the schema migrator before normal operation.

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
revalidate final physical schema
        ↓
commit transaction
```

The transaction-scoped PostgreSQL advisory lock serializes concurrent application instances during migration. The lock is released automatically with the transaction.

Critical schema validation runs on every startup, not only when a migration is first applied. A manually modified or corrupted table is therefore rejected even when the migration ledger itself appears complete.

## Existing database adoption

Migrations use `CREATE TABLE IF NOT EXISTS`, but that does **not** mean arbitrary pre-existing tables are trusted.

Before recording migration 1, Dyract validates that `peer_identity` has the expected critical shape:

- `peer_id text NOT NULL`;
- `public_key bytea NOT NULL`;
- `registered_at timestamp with time zone NOT NULL`;
- primary key exactly on `peer_id`.

Before recording migration 2, Dyract validates that `capability_revocation` has:

- `issuer_peer_id text NOT NULL`;
- `capability_id text NOT NULL`;
- `expires_at timestamp with time zone NOT NULL`;
- primary key exactly on `(issuer_peer_id, capability_id)`;
- no column whose name contains `grantee`.

If a critical table does not match, startup fails and the migration transaction rolls back. Dyract does not silently alter an unknown or malformed production schema.

## Capability revocation durability

With PostgreSQL configured, revocation authorization uses `PostgresCapabilityRevocationStore` directly. A successful revocation is committed before the API returns success, and later `/peer/resolve` and `/signal/send` authorization checks query the same durable state.

This means a server process restart does not resurrect a revoked capability. Multiple server instances sharing the same database also observe the same revocation records rather than relying on per-process cache hydration.

The store:

- deletes expired revocations opportunistically for the issuer;
- serializes per-issuer capacity checks with a PostgreSQL advisory transaction lock;
- preserves the 512-active-revocation prototype limit per issuer;
- remains idempotent for the same capability ID;
- can extend an existing revocation to the supplied natural expiry;
- stores no grantee/contact relationship.

Without a PostgreSQL connection string, the server intentionally falls back to `CapabilityRevocationStore`, the in-memory development/test implementation.

## Migration invariants

1. Migration versions are positive sequential integers beginning at 1.
2. A released migration is immutable.
3. Schema changes and their migration-ledger rows commit in the same transaction.
4. Multiple instances are serialized with the migration advisory lock.
5. A migration history with gaps is rejected.
6. A database with a migration version newer than the running build is rejected.
7. Critical schema shapes are validated before adoption and again on later startups.
8. Migration failure must fail application startup rather than falling back to an empty identity registry.
9. Database credentials are never embedded in migration source or workflow logs.
10. Future destructive migrations require an explicit backup/rollback plan and separate review.

## Adding migration v3+

For a real server schema change:

1. increment `PostgresSchemaMigrator.CurrentVersion`;
2. append the next immutable migration definition;
3. add previous-version → current-version integration coverage;
4. test concurrent migrators;
5. test failure/rollback behavior;
6. validate any security-sensitive resulting table/index/constraint shape;
7. ensure completed migration histories are revalidated for drift where appropriate;
8. run the PostgreSQL CI job before release.

Do not rewrite migrations 1 or 2 to change a deployed schema. Add migration 3 instead.

## PostgreSQL CI

`.github/workflows/ci.yml` includes a dedicated PostgreSQL integration job using PostgreSQL 18.

The job sets a test-only connection string through:

```text
DYRACT_POSTGRES_TEST_CONNECTION
```

and executes the real PostgreSQL migration tests against the service container.

Current integration coverage proves:

- fresh v1 + v2 migration succeeds;
- running the migrator repeatedly is idempotent;
- `PostgresIdentityStore` works after migration;
- revocation survives creation of a fresh `PostgresCapabilityRevocationStore` instance;
- expired revocations no longer authorize as active;
- the revocation table contains no grantee column;
- four concurrent migrators serialize correctly;
- malformed existing identity schema is rejected and rolled back;
- malformed revocation schema is rejected without recording migration 2;
- schema drift after a fully recorded migration history is rejected on later startup;
- a future migration version is rejected.

The ordinary unit/integration suite still runs without requiring PostgreSQL; the dedicated CI job ensures PostgreSQL-specific tests are not accidentally skipped in repository validation.

## Production boundary

PostgreSQL now persists:

```text
identity registrations
capability revocations
```

The remaining ephemeral server state is still separate:

```text
presence leases
replay nonces
WebRTC signaling
```

For a horizontally scaled production directory, those short-lived datasets still need shared TTL-capable state, typically Redis or an equivalent service. Capability revocations no longer depend on that future work for correctness because PostgreSQL already provides durable, cross-instance state, although a TTL-oriented store could later be evaluated as an optimization while preserving the same no-grantee metadata boundary.
