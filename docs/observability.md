# Privacy-aware directory observability

Dyract needs enough operational telemetry to detect failures and abuse without turning the directory into a metadata collection system.

This document defines the repository-level observability boundary and the default production retention policy. Deployment backend configuration and retention validation remain production-operations work.

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

The normal application request log event contains:

```text
bounded operation name
HTTP status code
elapsed milliseconds
```

It does not interpolate the request path or any request model field.

In production, an ordinary unhandled request exception is terminated inside `DirectoryTelemetryMiddleware` with a generic `500 internal_error` response. The application logs only a bounded failure category; it does not pass the original exception message into the telemetry event or rethrow it into ordinary hosting diagnostics when the response has not started.

Development retains normal exception propagation so local debugging is still possible.

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

Failure classification is also bounded:

```text
timeout
canceled
cryptography
io
internal
```

Exception messages, endpoints and infrastructure details are not failure-category values.

## Values forbidden from normal application telemetry

Do not add these to ordinary log templates, metric tags, tracing baggage or exception-message copies:

- identity private keys or exported PKCS#8 values;
- local-data encryption keys;
- recovery phrases/codes/passphrases/packages;
- raw PeerIds/fingerprints unless a separately reviewed incident workflow explicitly requires them;
- contact display names/contact lists;
- message/attachment content;
- raw client IP addresses or ports;
- raw presence candidate addresses or ports;
- ICE candidate/SDP bodies;
- capability IDs or encoded capabilities;
- replay nonces;
- challenge bytes/IDs;
- signaling payloads/IDs/session IDs;
- push tokens;
- request/response bodies;
- arbitrary query strings, headers or cookies;
- Redis/PostgreSQL connection strings or credentials;
- arbitrary user-supplied error/details text.

The network/server necessarily sees a client source address while handling a request. Dyract's own telemetry middleware deliberately does not copy that address into its application telemetry.

## Error handling

Security-sensitive failures should use stable bounded API/log error codes rather than logging attacker-controlled exception/request text.

Production application logging may record:

```text
operation = peer.resolve
status = 500
failure category = internal
```

It must not automatically record:

```text
exception.Message
exception.ToString()
exception Data dictionaries
connection strings
SQL parameters
Redis command values
request body snapshots
```

If an incident requires deeper diagnostics, use a reviewed temporary diagnostic change with explicit scope, access, expiry and deletion instead of permanently enabling unrestricted exception capture.

A response that has already started, or a request that is already canceled, can still escape the ordinary production sanitizer because it may no longer be safe to replace the response. Infrastructure/hosting diagnostics must therefore still follow this document's data-minimization rules.

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
exception message
```

High-cardinality tags are both expensive and a privacy risk.

## Tracing

Application distributed tracing with payload capture is not enabled by the current repository.

If tracing is introduced later:

- use the same bounded operation names;
- do not attach PeerIds, IPs, candidates, capabilities, nonces, request/response bodies or message content;
- disable automatic body/header/query capture unless separately reviewed;
- do not enable database parameter capture;
- do not export Redis command values;
- treat exemplars as telemetry subject to the same retention rules.

A tracing vendor integration must not be enabled merely because an SDK can collect more data.

## Production retention baseline

Dyract favors short retention because directory telemetry is intended for current operations, not long-term behavioral analytics.

Unless a deployment has a documented stricter requirement, use this baseline:

```text
application request/security logs   7 days normal retention
application logs hard maximum      30 days unless explicit incident/legal hold applies
raw/high-cardinality traces         disabled by default
aggregate operational metrics      30 days
incident-specific extracts         explicit scope + deletion date required
```

Retention is measured from telemetry ingestion time and should be enforced by backend lifecycle/retention controls rather than manual cleanup.

Do not retain request-level telemetry indefinitely merely to build future analytics.

A legal or incident hold is an exception, not a reason to change the default retention of all future telemetry. The held dataset must have documented scope, access and eventual deletion.

## Edge and infrastructure telemetry

Repository-safe application logging does not automatically sanitize CDN, reverse-proxy, WAF, load-balancer, cloud runtime, PostgreSQL, Redis or crash-reporting telemetry.

Before production launch review each source for:

```text
client IP capture
full URL/query capture
request/response body sampling
headers/cookies
TLS/client metadata
WAF samples
PostgreSQL statement/parameter logging
Redis command/value logging
crash dump contents
cloud diagnostic exports
```

Where client-IP logging is required for edge abuse control, keep it in the restricted edge/security system, minimize retention and access, and do not copy it into ordinary Dyract application metrics/product analytics.

WAF/body sampling must not retain Dyract request bodies by default.

## Access control

Production telemetry must follow least privilege:

- service-health dashboards should not require access to secret stores or raw edge-security logs;
- edge logs containing client network metadata should have narrower access than ordinary application metrics;
- telemetry exporters use dedicated credentials;
- exporter credentials come from production secret management;
- sensitive infrastructure-log access is audited where the platform supports it;
- application logs/metrics are never publicly accessible.

## Alerts

Prefer alerts from aggregate bounded data:

- elevated `5xx` rate;
- sustained request latency;
- Redis/PostgreSQL availability failure;
- rate-limit/WAF rejection volume;
- unusual changes in fixed-operation request rates.

Alert payloads should reference deployment/service/operation, not raw peer or endpoint metadata.

## Test coverage

`DirectoryTelemetryTests` protects the repository-side boundary by proving:

- every known API route maps to a fixed operation name;
- unknown API routes collapse to `api.unknown`;
- non-API routes collapse to `http.other`;
- a request body containing a unique sentinel is not copied into the Dyract request log;
- the literal request path is not copied into the event;
- failure categories are bounded;
- a production exception containing credential/IP-like sentinel data returns a generic error;
- the sentinel is not copied into captured production logs.

The ordinary core CI executes these tests.

These tests do not validate a specific external logging/metrics vendor or its retention settings.

## Production deployment checklist

Before claiming production observability complete, record and validate:

```text
log/metrics backend
application-log retention = configured target
aggregate-metric retention = configured target
body/header/query capture disabled
edge/WAF logging policy + retention
PostgreSQL statement/parameter logging policy
Redis command/value logging policy
crash reporting/scrubbing policy
telemetry access roles
exporter credentials source/rotation
alert routing
retention deletion/expiry verification
```

A deployment should be able to demonstrate that expired telemetry is actually deleted by the configured backend.

## Current PLAN acceptance

Repository-side **privacy-aware structured application logs/metrics and retention policy** are complete when this document remains aligned with implementation.

Still deployment-specific before production:

- choose/configure the telemetry backend/exporter;
- apply the retention limits in that backend;
- review edge/cloud/database/cache diagnostic defaults;
- validate deletion/expiry and access controls.

Those concrete production observability deployment/retention checks remain unfinished infrastructure validation work rather than an application-code task.
