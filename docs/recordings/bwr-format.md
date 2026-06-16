# `.bwr` — Bowire Recording File Format

A `.bwr` file is a **standalone, workspace-agnostic JSON document** that
captures one Bowire recording — an ordered sequence of invocations made
against a live service — in a form that any Bowire build can replay
without any other state on disk. Producers and consumers depend on the
file alone; there is no sidecar directory, no content-addressed body
store, no cross-reference to a workspace.

Throughout the docs this file extension is what is meant by:

- the HAR import tool (`bowire har convert ... -o foo.bwr`)
- the `bowire mock --recording <foo.bwr>` CLI ([#211](https://github.com/Kuestenlogik/Bowire/issues/211))
- the workbench's "Export recording" download
- the on-disk format of a UI-started mock (`~/.bowire/mocks/<id>.bwr`)

The same JSON tree is also embedded — un-extension'd — inside a
`.bww` workspace as a recordings-array entry. A `.bwr` is **strictly
the single-recording extract** of that embedded shape, suitable for
sharing or replaying outside the workspace.

## Two accepted shapes

The loader accepts either of the two following JSON envelopes. Both are
written as `.bwr` and the same loader picks the right path automatically.

### Store-wrapped envelope

```json
{
  "recordings": [
    { "id": "...", "name": "...", "steps": [...] },
    ...
  ]
}
```

When the array contains a single recording the loader returns it
without further input. When it contains multiple, the loader requires
the consumer to disambiguate (e.g. `bowire mock --recording foo.bwr --name "happy path"`).

### Single-recording shape

```json
{ "id": "...", "name": "...", "steps": [...] }
```

This is what the workbench export emits and what most CLI consumers
prefer. The loader detects it by the presence of `steps` at the root.

## Schema

The on-the-wire shape mirrors the .NET types in
`src/Kuestenlogik.Bowire/Mocking/BowireRecording.cs` —
`BowireRecording` for the file root (or each array entry of the
store-wrapped shape) and `BowireRecordingStep` for each step. Property
names use `camelCase` exactly as `[JsonPropertyName]` declares them.

### Recording root

| Field | Type | Required | Meaning |
|---|---|---|---|
| `recordingFormatVersion` | `int` | yes | Schema version this file was written under. See [Versioning](#versioning) below. |
| `id` | `string` | yes | Stable identifier — used as a content-addressing key by some consumers; must survive a re-export round-trip. |
| `name` | `string` | yes | Human-readable label shown in the workbench's recording list. |
| `description` | `string` | no | Free-form prose. Empty string when omitted. |
| `createdAt` | `long` | no | Unix milliseconds. Stamped at first save; not updated on re-export. |
| `steps` | `array<step>` | yes | At least one entry; the loader rejects empty `steps`. |
| `schemaSnapshot` | `object` | no | Phase-5+ recordings carry the resolved frame-semantics annotations as a sidecar so a replay can mount the same widgets the original session did. Optional; loader treats omission as "ask the live store at replay". |
| `sourceSchema` | `object` | no | Original OpenAPI / AsyncAPI / GraphQL / WSDL document the target advertised at capture time. Used by mock-hosting extensions to serve the **full** declared surface, not just the realized slice. |
| `attack` | `bool` | no | When `true` this is a security probe (a "vulnerability template" — `docs/architecture/security-testing.md`), NOT a fixture; the mock-server replay path explicitly skips it. Default `false`. |
| `vulnerability`, `vulnerableWhen` | object | iff `attack=true` | Identifying metadata + match predicate for the targeted vulnerability. |

### Step entry

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | `string` | yes | Unique per-step identifier within the recording. |
| `protocol` | `string` | yes | Lowercase plugin id: `rest`, `grpc`, `signalr`, `websocket`, `mqtt`, `nats`, ... |
| `service` | `string` | yes | OpenAPI tag / gRPC service FQN / hub name / topic, depending on protocol. |
| `method` | `string` | yes | Operation id / gRPC method / hub method / event name. |
| `methodType` | `string` | yes | One of `Unary`, `ServerStreaming`, `ClientStreaming`, `Duplex`. |
| `serverUrl` | `string` | no | Origin the step was captured against. Optional — `bowire mock` doesn't use it; downstream consumers might. |
| `body` | `string` | no | Primary request body (unary: full request; streaming: first message). |
| `messages` | `array<string>` | no | All request messages in order. Single entry for unary; multiple for streaming. |
| `metadata` | `object<string, string>` | no | Request headers / gRPC metadata. |
| `status` | `string` | yes | `OK`, an HTTP code, or a gRPC status code. Default `OK`. |
| `durationMs` | `long` | no | Wall-clock duration the original call took. The replayer ignores this unless `--replay-speed` is configured. |
| `response` | `string` | no | **Inlined response body.** A standalone `.bwr` MUST inline this — the field is not allowed to be a `responseRef` content-address hash. |
| `httpPath` | `string` | REST only | Path template the call was made against. |
| `httpVerb` | `string` | REST only | HTTP verb. |
| `responseBinary` | `string (base64)` | gRPC only | Raw wire bytes of the response message. Replayed byte-for-byte by the mock without a runtime protobuf encoder. Requires `recordingFormatVersion: 2`. |
| Additional step fields | … | … | See `BowireRecordingStep` in source for the full surface — streaming variants, capture-side metadata, semantic discriminators, etc. |

## Self-containment requirement

Inside a workspace, the chunked recording store under
`~/.bowire/recordings/<id>/` (or `<workspace>/recordings/<id>/`) splits
large bodies into a `bodies/<sha256>` directory and references them
from the step manifest as `responseRef`. **This shape is forbidden in a
standalone `.bwr` file.** Producers MUST resolve every `responseRef` to
the inline `response` body before writing the file. The validator
rejects any `.bwr` that carries a `responseRef`.

This rule is what makes a `.bwr` "self-contained, workspace-agnostic":
moving the file alone is enough to replay the session anywhere.

## Versioning

`recordingFormatVersion` is monotonically increasing. The current values
live in `src/Kuestenlogik.Bowire.Mock/Loading/RecordingFormatVersion.cs`:

- `V1 = 1` — Phase-1a — REST unary only.
- `V2 = 2` — Phase-1b — adds `responseBinary` for gRPC unary replay.
- `Current = V2` is what producers write today.

The loader rejects any version it wasn't built for with a clear
diagnostic — `Re-record against a matching Bowire build`. v2 is a
strict superset of v1: a v2 build can replay v1 recordings as long
as the version field is set.

## Loader contract

Programmatic loading goes through
`Kuestenlogik.Bowire.Mock.Loading.RecordingLoader.Load(path, select?)`:

- accepts both envelope shapes,
- validates `recordingFormatVersion` against the build's `Current`,
- requires at least one step,
- throws `InvalidDataException` with a human-readable message on any
  shape / version mismatch.

The CLI surface is `bowire recording validate <path>` (alias on the
`recording` subcommand) — exit `0` for a valid file, `64` (EX_USAGE)
for bad args, `65` (EX_DATAERR) for a malformed file, with the inner
diagnostic on stderr.

## Out of scope

- **Compression**. `.bwr` is plain JSON; gzip-friendly under git
  attributes. A versioned `.bwr.gz` extension lands later if recordings
  get unwieldy.
- **Binary wire format**. Single-JSON keeps diffs readable — a
  reviewer should be able to look at a workspace PR and see what
  changed in the recorded fixture.
- **Encryption**. Use the underlying transport (https for download, ssh
  for git) and at-rest encryption on the filesystem. Per-file
  encryption is workspace policy, not recording-format scope.
