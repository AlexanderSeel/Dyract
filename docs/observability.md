# Privacy-aware directory observability

Dyract needs enough operational telemetry to detect failures and abuse without turning the directory into a metadata collection system.

This document defines the repository-level observability boundary. Deployment backend selection, access control and retention remain production-operations work.

## Principle

Normal directory telemetry should answer questions such as:

```text
Is registration failing more often?
Are resolve requests slow?
Is signaling returning more 5xx responses?
Is request volume changing?
```

without answering:

```text
Which PeerId contacted which other PeerId?
What IP/candidate did a peer publish?
Which capability/nonces/signaling IDs were used?
What was in the request body?
```

## Current application telemetry

`DirectoryTelemetryMiddleware` wraps the HTTP directory pipeline and emits only bounded operation/status/duration data.

Meter:

```text
Dyract.Directory
```

Current instruments:

```text
dyract.directory.requests
dyract.directory.request.duration
dyract.directory.failures
```

Metric dimensions are limited to:

```text
operation
status_class
```

The application log event contains:

```text
bounded operation name
HTTP status code
elapsed milliseconds
```

It does not interpolate the request path or any request model field.

## Bounded operation names

The classifier maps known routes to a fixed vocabulary:

```text
health
identity.challenge
identity.register
peer.lookup
presence.publish
presence.remove
capability.revoke
peer.resolve
signal.send
signal.fetch
signal.ack
api.unknown
http.other
```

Using a bounded vocabulary prevents dynamic PeerIds, signal IDs, query values or arbitrary attacker-controlled paths from becoming metric labels/high-cardinality metadata.

## Values forbidden from normal application telemetry

Do not add these to ordinary log templates, metric tags or tracing baggage:

- identity private keys or exported PKCS#8 values;
- local-data encryption keys;
- raw PeerIds/fingerprints unless a separately reviewed security incident workflow explicitly requires them;
- contact display names;
- message/attachment content;
- raw presence candidate addresses or ports;
- ICE candidate/SDP bodies;
- capability IDs or encoded capabilities;
- replay nonces;
- challenge bytes/IDs;
- signaling payloads/IDs/session IDs;
- recovery phrases/codes/packages;
- push tokens;
- request bodies;
- arbitrary user-supplied error/details text.

The network/server necessarily sees a client source address while handling a request. Dyract's own telemetry middleware deliberately does not copy that address into its application telemetry.

## Error handling

Security-sensitive failures should use stable bounded API/log error codes rather than logging attacker-controlled exception/request text.

Unexpected exceptions may still be captured by the hosting platform/framework depending on deployment configuration. Production logging configuration must therefore be reviewed separately; repository telemetry discipline alone cannot guarantee that a reverse proxy, cloud runtime or crash reporter does not collect additional metadata.

## Metrics cardinality

Metric tag values must remain from bounded enumerations such as operation and status class.

Do not tag metrics by:

```text
PeerId
remote IP
candidate address
contact
session ID
capability ID
signal ID
nonce
User-Agent
raw path/query
```

High-cardinality tags are both expensive and a privacy risk.

## Test coverage

`DirectoryTelemetryTests` proves:

- every known API route maps to a fixed operation name;
- unknown API routes collapse to `api.unknown`;
- non-API routes collapse to `http.other`;
- a request body containing a unique sentinel value is not copied into the Dyract telemetry log event;
- the literal request path is not copied into the event;
- operation and status remain observable.

The ordinary core CI executes these tests.

## Deployment retention policy still required

Repository code cannot decide a production log/metric retention period or operator access model in isolation.

Before public production deployment, define and document at least:

1. telemetry backend/provider;
2. who can read operational telemetry;
3. production vs development log levels;
4. retention period per data class;
5. deletion/rotation process;
6. whether hosting/CDN/load-balancer access logs are enabled and what they contain;
7. crash/error-reporting collection and scrubbing rules;
8. incident-only elevated logging procedure and expiry;
9. export/backup policy for telemetry;
10. regional/legal deployment requirements.

Default production policy should prefer short retention and aggregate metrics over request-level metadata.

## Current PLAN acceptance

Repository-side **privacy-aware application telemetry schema and regression tests** are implemented.

The broader PLAN item remains partially open until production backend/access/retention policy and infrastructure are selected and validated. This distinction prevents repository-safe logging from being misrepresented as a complete operational privacy program.
