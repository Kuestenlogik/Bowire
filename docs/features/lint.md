---
title: Design-time lint
summary: "`bowire lint` reads a discovered API surface for design smells — personal data in responses, unbounded collections, missing versioning — from a live URL or a snapshot file."
---

# Design-time lint

Most checks in Bowire run against a *call*: you invoke a method and assert on
what came back. Lint runs against the *surface*: it reads the discovered
schema and reports design problems without sending a single request to the
API under review.

That makes it the one check you can run on a schema in a pull request, before
the service it describes exists.

```bash
# a live service
bowire lint https://api.example.com --protocol rest

# a snapshot captured earlier — the shape a pipeline uses
bowire lint api-snapshot.json --format markdown --output lint.md
```

`<source>` is either a `.json` snapshot from `bowire diff snapshot` or a live
URL to discover. `--protocol` names the plugin for a live URL and is guessed
from the URL scheme when unset.

## The rules

| Rule id | Severity | Fires when |
|---------|----------|-----------|
| `BWR-LINT-SENSITIVE-RESPONSE` | High | a **response** field looks like a secret |
| `BWR-LINT-PII-RESPONSE` | Medium | a **response** field looks like personal data — email, phone, SSN, date of birth, address, passport or tax id |
| `BWR-LINT-MISSING-PAGINATION` | Medium | a method **returns a list** and takes no `page` / `limit` / `offset` / `cursor` parameter, and its response carries no continuation token |
| `BWR-LINT-STRING-TIMESTAMP` | Low | a timestamp ships as a bare string rather than a typed instant |
| `BWR-LINT-MISSING-VERSIONING` | Low | the service declares no version **and** no route carries a version marker such as `/v1/` |

## What lint can see, and what it cannot

A rule can only inspect what discovery produced. This is the single most
important thing to know before reading a lint result:

| Protocol | Request fields | Response fields | Rules that can fire |
|----------|----------------|-----------------|---------------------|
| gRPC (reflection or descriptor set) | yes | yes | all five |
| REST (OpenAPI document) | yes | yes, where the operation declares a 2xx JSON response schema | all five |
| REST (embedded, ApiExplorer) | yes | yes, where the endpoint declares its response type | all five |

Against a gRPC target the descriptors carry full message types, so every rule
evaluates. Against a REST target, the response shape comes from the OpenAPI
operation's 2xx `application/json` schema — a top-level array is read as one
repeated `items` field of the element type, so a list endpoint reads as a
list to the pagination rule — or, when Bowire runs inside the host, from the
endpoint's declared response type. An endpoint that declares neither (a
Minimal API handler returning a bare `Results.Ok(...)` with no `Produces<T>()`,
an OpenAPI response with a description and no schema) has no response shape
for the four response-shaped rules to read.

**Lint says so.** When any method has no response shape, the report carries a
note under the summary rather than passing it silently:

```console
no findings
note: 3 of 16 methods declare no response schema; the response-shaped rules
      (sensitive and PII fields, pagination, string timestamps) could not
      evaluate those. For REST, annotate the endpoint's response type
      (Produces<T>, or a response schema in the OpenAPI document).
```

The remedy is on the API side: declare the response type, and the rules
evaluate it on the next run.

A worked example against the gRPC sample:

```console
$ bowire lint http://localhost:5183 --protocol grpc
[MEDIUM] BWR-LINT-MISSING-PAGINATION  …Greeter.SayHelloBatch  Method 'SayHelloBatch'
         returns a list but takes no pagination parameter (page / limit / offset /
         cursor). Unbounded list responses are a scaling and denial-of-service risk.
[LOW]    BWR-LINT-MISSING-VERSIONING   …Greeter  Service declares no version and no
         route carries a version marker (e.g. /v1/).
[LOW]    BWR-LINT-MISSING-VERSIONING   grpc.reflection.v1alpha.ServerReflection  (same)

3 findings (1 medium, 2 low)
```

## Configuration

Severities and on/off switches live in `.bowire/rules.json`, discovered by
walking **up** from the working directory the way a linter should — so a
repository configures its rules once and every checkout, and the pipeline,
read the same file.

```bash
bowire lint https://api.example.com --rules .bowire/rules.json
```

Pass `--rules` to point at a specific file; omit it to auto-discover.

## Gating a pipeline

`--fail-on` turns the report into a gate:

```bash
bowire lint https://api.example.com --protocol rest --fail-on medium
```

| Value | Exit non-zero when |
|-------|--------------------|
| `none` (default) | never — the command always exits 0 |
| `info` / `low` / `medium` / `high` | a finding reaches that severity |

The default is `none` deliberately: adopting lint never breaks a pipeline on
the first run. Start at `high`, and lower the bar as findings get fixed
rather than the other way round.

`--format json` or `markdown` with `--output <file>` writes a report a later
step can pick up — including `bowire report rollup`, which reads lint output
alongside contract, benchmark, scan and test reports.

## In the workbench

The **Lint** rail runs the same rules over the active workspace's discovered
surface and lists the findings, each one clickable through to the method it
fired on. With nothing discovered yet the rail shows an empty state rather
than an empty list.

## Related

- [Contract testing](contract-testing.md) — pin what a consumer relies on
- [Report rollup](report-rollup.md) — read lint output alongside every other report
- [Service compare](service-compare.md) — diff two versions of a surface
