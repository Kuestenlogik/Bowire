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
| `BWR-LINT-PII-IN-ERROR` | Medium | a declared **error** response carries a field that looks like personal data — a validation error that echoes the `email` it rejected, a 404 that repeats the `phone` it looked up |
| `BWR-LINT-MISSING-PAGINATION` | Medium | a method **returns a list** and takes no `page` / `limit` / `offset` / `cursor` parameter, and its response carries no continuation token |
| `BWR-LINT-STRING-TIMESTAMP` | Low | a timestamp ships as a bare string rather than a typed instant |
| `BWR-LINT-MISSING-VERSIONING` | Low | the service declares no version **and** no route carries a version marker such as `/v1/` |
| `BWR-LINT-MIXED-METHOD-NAMING` | Info | a method's name follows a different convention than the rest of its service |
| `BWR-LINT-MIXED-FIELD-NAMING` | Info | a field's name follows a different convention than the other fields of its service, request and response together |

### The naming rules judge consistency, not a style

Which convention is right depends on the protocol: gRPC methods are PascalCase,
REST operation ids camelCase, protobuf fields snake_case. A rule that demanded
one of them would be wrong for the others. What is wrong everywhere is a
surface that **mixes** them — a payload with `created_at` next to `updatedAt`,
a service with `GetOrder`, `ListOrders` and `cancelOrder`. So the naming rules
take the service's own majority as the standard and name the outliers against
it:

```
[INFO] BWR-LINT-MIXED-METHOD-NAMING  orders.v1.OrderService.cancelOrder
       Method 'cancelOrder' is camelCase; the rest of this service is PascalCase (2 PascalCase, 1 camelCase).
```

A few names cannot tell and are left out of the count entirely: a single
lower-case word (`status` is valid camelCase, snake_case and kebab-case at
once), an acronym (`ID`), and a name that is not an identifier at all (the
`GET_/pets/{id}` Bowire synthesises for a REST operation without an
`operationId`). HTTP headers and cookies do not count towards field naming —
kebab-case is the transport's convention there, not the API's. With no
majority, the finding is raised once for the service rather than pinned on
either half.

## What lint can see, and what it cannot

A rule can only inspect what discovery produced. This is the single most
important thing to know before reading a lint result:

| Protocol | Request fields | Response fields | Error fields | Rules that can fire |
|----------|----------------|-----------------|--------------|---------------------|
| gRPC (reflection or descriptor set) | yes | yes | no | all but `PII-IN-ERROR` |
| REST (OpenAPI document) | yes | yes, where the operation declares a 2xx JSON response schema | yes, every 4xx / 5xx / `default` response with a JSON schema | all |
| REST (embedded, ApiExplorer) | yes | yes, where the endpoint declares its response type | no | all but `PII-IN-ERROR` |

Errors are only visible where a schema declares them. gRPC reports an error as
a status plus details typed at runtime, GraphQL as an untyped `errors` array,
and neither says in the schema what an error will carry — so against those
`BWR-LINT-PII-IN-ERROR` has nothing to read. A clean result there means
"nothing declared", not "nothing leaks".

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

### Against a baseline

`--baseline` adds compatibility to the same run. Every breaking change since
the baseline becomes a finding of its own rule, `BWR-LINT-BREAKING-CHANGE`
(High):

```bash
bowire lint https://api.example.com --baseline main-surface.json --fail-on high
```

What counts as breaking is exactly what `bowire diff --fail-on breaking` counts
— a removed service, a removed method, a changed signature — because it is the
same comparison. Added methods, deprecations and description edits are not
breaking and produce nothing. `.bowire/rules.json` can switch the rule off or
re-grade it like any other.

A baseline that is not there, or that holds no services, is an error rather
than an empty comparison: against nothing, nothing can have broken, and a
clean report would be the one answer the run cannot give.

### Through the test runner

A job that already collects JUnit and SARIF from `bowire test` can take the
lint findings into the same reports:

```bash
bowire test --suite=lint surface.json --fail-on high --junit lint.xml --sarif lint.sarif
```

| Output | Shape |
|--------|-------|
| console | the same text report `bowire lint` prints |
| `--junit` | one test case **per rule**, failing when that rule found something at or above the gate; findings below it are listed in the case's output. Per rule rather than per finding, so the number of test cases stays the same from run to run |
| `--sarif` | every finding, at the level its severity maps to (High → error, Medium → warning, Low and Info → note), with service / method / field as a logical location |
| `--annotations` | GitHub Actions annotations — findings at the gate as errors, the rest as warnings |
| `--baseline` | as above — compatibility becomes one more JUnit case and SARIF rule, next to the design rules |

`--fail-on` takes `bowire test`'s words (`any` fails on any finding, `never`
only reports) and `bowire lint`'s severities. Without it the suite uses lint's
default — report, and pass — for the same reason as above. Unlike `bowire lint`,
a value it does not know is refused rather than read as "never": under
`bowire test` the step is a gate, and a typo that quietly turns it green is
the failure mode to avoid.

## In the workbench

The rules run in the background after every discovery, so findings show where
the method is, not only in a separate list:

- **In the sidebar**, a method with a finding of Low or worse carries a `!`
  pill in the colour of its worst finding. Info findings stay out of the
  sidebar — a naming nit on half the rows would teach people to ignore the
  pill.
- **Under the method's header**, a strip names how many findings the method
  has, folded to one line until opened. Opened, it lists each finding with its
  field, message and rule, and every severity, Info included.
- **The Lint rail** lists everything, service-level findings too. A finding on
  a method opens that method with its findings unfolded. With nothing
  discovered yet the rail shows an empty state rather than an empty list.

An unchanged rediscovery does not lint again; a changed surface does.

## Related

- [Contract testing](contract-testing.md) — pin what a consumer relies on
- [Report rollup](report-rollup.md) — read lint output alongside every other report
- [Service compare](service-compare.md) — diff two versions of a surface
