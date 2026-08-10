# Production secret management

## Status

Dyract now enforces a repository-side baseline for production connection secrets. Actual cloud/hosting secret-manager deployment, access control, rotation and incident procedures remain deployment responsibilities.

## Repository rule

Real credentials must not be committed to:

- `appsettings.json`;
- environment-specific checked-in settings;
- source code;
- test fixtures intended for source control;
- command lines/scripts that become ordinary logs or shell history.

The checked-in server configuration keeps the PostgreSQL connection string empty by default.

## Enforced production source

For the current PostgreSQL and Redis integrations, Production accepts configured connection values only from deployment environment/platform settings:

```text
ConnectionStrings__Dyract
ConnectionStrings__Redis
```

`ProductionSecretPolicy` reads these values directly in Production instead of accepting an arbitrary `IConfiguration` provider as the credential source.

This means:

- Development/test can continue to use ordinary local configuration providers.
- If PostgreSQL or Redis is completely unconfigured, the existing documented optional fallback remains possible.
- If a Production connection is present in `IConfiguration` but the corresponding deployment environment value is absent, startup fails closed.
- A deployment environment value wins directly; the application does not re-resolve the credential through another provider afterward.
- Policy exceptions name only the connection and required environment-variable key, never the credential value/endpoint/password.

This intentionally prevents a checked-in `appsettings.Production.json`, command-line argument, or accidental provider-precedence change from silently becoming the Production database/Redis credential source.

## Secret-manager integration

The environment-variable requirement is the current **enforceable application boundary**, not a recommendation to maintain plaintext `.env` files on production hosts.

A production platform should populate these process settings from its managed secret facility, for example through a protected application setting, secret reference, workload secret mount projected into process configuration, or equivalent platform-native mechanism.

Do not commit the resolved value to the repository merely because the application consumes it through an environment variable.

If the selected hosting provider supports a stronger passwordless/managed-identity database mechanism, Dyract should extend `ProductionSecretPolicy` deliberately for that authenticated source rather than bypassing the policy with an arbitrary configuration provider.

## PostgreSQL credential requirements

Production PostgreSQL access should use a dedicated Dyract workload identity/credential with only the permissions required by the directory schema and migration policy.

Operational requirements:

- no shared human/admin credential;
- encryption in transit according to the production database policy;
- secret value stored in the hosting secret manager;
- access restricted to the Dyract deployment identity/operators who require it;
- rotation supported without source-code changes;
- connection strings/passwords excluded from ordinary logs and exception messages;
- old credentials revoked after successful rotation.

Migration permissions should be reviewed separately if the provider can split runtime and schema-migration identities safely.

## Redis credential requirements

Redis still has its independent Production startup policy in `RedisConnectionPolicy`:

- TLS required;
- authentication required;
- administrative commands disabled;
- deployment must attest private/network-isolated access.

The Redis authentication secret must additionally arrive through:

```text
ConnectionStrings__Redis
```

The secret-source policy does not replace the TLS/network policy; both must pass.

## Rotation procedure

A safe baseline rotation is:

1. create/activate the new credential in the provider;
2. update the protected deployment secret setting;
3. restart/roll the Dyract directory instances so new connections use the new value;
4. verify health and shared-state/database behavior without logging the credential;
5. revoke the old credential;
6. verify no old instances remain connected with the previous credential.

Provider-specific zero-downtime dual-credential techniques may improve this sequence, but should be documented with the selected production platform rather than embedded as a generic application assumption.

## Future secrets

Before the corresponding features ship, the same principles must be applied to:

- TURN credentials or TURN REST/shared secrets;
- APNs credentials/keys;
- FCM service credentials;
- backup/PITR access credentials;
- observability exporter credentials if required;
- any future encrypted recovery storage credential.

Identity private keys, local-data keys and user recovery secrets are **not server deployment secrets** and must not be moved into this server-side mechanism.

## Logging and diagnostics

Do not log:

- connection strings;
- passwords/tokens;
- secret-manager references containing sensitive query material;
- provider authentication responses that embed credentials.

Startup validation errors should use bounded policy text. Runtime Production request handling already sanitizes unhandled application exceptions, but startup/deployment logs must be configured with the same no-secret expectation.

## What remains before production

Repository-side source enforcement is complete for currently implemented PostgreSQL/Redis credentials.

Production readiness still requires selecting and validating the actual hosting secret manager, including:

- workload/operator access controls;
- encryption/audit policy;
- secret rotation drill;
- revocation procedure;
- production deployment verification;
- extension of this policy for future TURN/push/backup/observability credentials as those features are introduced.
