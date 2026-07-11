---
title: Interceptor
summary: 'Observing + manipulating real client / server traffic. The Interceptor rail surfaces the in-process `UseBowireInterceptor()` middleware and the standalone MITM proxy CLI behind one workbench rail; captured flows convert to recordings, mocks, or templates. Ships as `Kuestenlogik.Bowire.Interceptor`.'
---

# Interceptor

The **Interceptor** is Bowire's in-process traffic capture surface. Drop one line of middleware into an ASP.NET host and every inbound request — from any client, over HTTP/1.1, HTTP/2, or HTTP/3 — flows into a workbench rail you can read, replay, save as a recording, or wrap as a mock.

v2.1 consolidated three v2.0 rails (**Proxy**, **Intercepted**, **Traffic**) into a single **Interceptor** rail and packaged the whole surface — middleware, stores, reverse-proxy host, admin endpoints — as one NuGet: `Kuestenlogik.Bowire.Interceptor` ([#325](https://github.com/Kuestenlogik/Bowire/issues/325)). The old packages (`Rail.Proxy`, `Rail.Intercepted`, `Rail.Traffic`) are retired; the middleware that used to live in Core moved into the new package too.

## When to use which

| Scenario | Use |
|---|---|
| The host you want to observe runs ASP.NET and you can add a NuGet | `app.UseBowireInterceptor()` — zero client setup, captures every inbound request to the host |
| The host is a black-box client (a browser, a phone app, a curl one-liner) and you need to MITM its outbound HTTPS | `bowire proxy` — standalone MITM proxy, the v1.x workflow, still supported |
| You want to forward an inbound request to a downstream service with rewriting | Interceptor's reverse-proxy host — `app.UseBowireInterceptor(opts => opts.ReverseProxy = ...)`  |

The middleware path is the v2.1 recommendation: no certificate trust, no separate process, no port juggling, and the captures are already routed into the workbench live over SSE.

## Quick start — middleware

Add the package:

```bash
dotnet add package Kuestenlogik.Bowire.Interceptor
```

Register the middleware in `Program.cs`:

```csharp
using Kuestenlogik.Bowire.Interceptor;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBowire();
var app = builder.Build();

app.UseBowireInterceptor();   // <- before your endpoints
app.MapBowire("/bowire");
app.MapControllers();
app.Run();
```

Any client hitting the host now produces an entry in the Interceptor rail. Open `/bowire`, switch to the **Interceptor** rail in the strip, and you'll see flows landing live as they arrive.

## What gets captured

Each intercepted flow carries:

| Field | Notes |
|---|---|
| Method, path, query string | Request line |
| Request headers, request body | Body capped at 1 MB by default; configurable via `InterceptorOptions.MaxBodyBytes` |
| Response status, response headers, response body | Capped the same way |
| Latency | Wall-clock from middleware entry to response complete |
| Timestamp | UTC, ISO 8601 |
| Client IP, scheme, host | Populated when behind a reverse proxy via `UseForwardedHeaders` |

Nothing is hashed, encrypted, or redacted by default — the operator runs the host, the operator sees the bodies. Redaction can be wired via the `InterceptorOptions.RedactRequestBody` / `RedactResponseBody` delegates if you need to scrub bearer tokens / PII before flows land in the rail.

## The Interceptor rail

The rail is mounted by `Kuestenlogik.Bowire.Interceptor` and surfaces three sub-tabs:

| Sub-tab | What it shows |
|---|---|
| **Flows** | Live list of captured flows. Click to inspect headers + body. Right-click for `Save to recording`, `Open in Compose`, `Copy as cURL`. |
| **Mock Rules** | Rules that short-circuit matching inbound requests with a canned response. Useful for simulating a partner service that's down. |
| **Settings** | Buffer size, body cap, redaction rules, reverse-proxy targets if configured |

Flows land via SSE on `/api/intercepted/stream`. The legacy `/api/intercepted/*` endpoints stay mounted; v2.1 adds `/api/interceptor/*` as the new canonical aliases.

### Append intercepted flows to a recording

Start a recording from the action bar, hit the host with any client, click stop. Every intercepted flow becomes a recording step. The combination of intercepted middleware + recording is the v2.1 fast path for "I want a `.bwr` of what my client did" — no proxy setup, no cert trust.

## Quick start — `bowire proxy` (standalone MITM)

For black-box clients you can't add a NuGet to, the standalone MITM proxy is still the right tool. Same `.bwr` output as the intercepted middleware — both write through the same store.

Start the proxy listening on `:8888`:

```bash
bowire proxy --port 8888 --out captured.bwr
```

Install the auto-generated CA into the client's trust store (one-time per machine / browser):

- **Linux / macOS — system trust store**: copy `~/.bowire/proxy-ca.crt` into `/usr/local/share/ca-certificates/` and run `update-ca-certificates`.
- **Windows**: double-click `~/.bowire/proxy-ca.pfx` and import into "Trusted Root Certification Authorities".
- **Browser-specific**: Firefox uses its own store — import `~/.bowire/proxy-ca.crt` under Settings → Privacy & Security → Certificates → Import.
- **Mobile app under test**: drop the `.crt` into the app's debug certificate folder, or for iOS push it via a configuration profile.

Then point the client at the proxy (curl, browser HTTPS proxy setting, mobile-VPN profile, etc.):

```bash
HTTPS_PROXY=http://localhost:8888 curl https://api.example.com/users
```

Every call routes through Bowire; `Ctrl+C` stops the proxy and finalises the recording.

### Flags

| Flag | Default | Notes |
|---|---|---|
| `--port, -p <port>` | `8888` | Proxy listen port. |
| `--out, -o <path>` | `proxy.bwr` | Recording output file. |
| `--ca-dir <dir>` | `~/.bowire/` | Where the auto-generated CA + key live. |
| `--filter <host>` | none | Repeatable. Only record calls matching the host substring. |
| `--passthrough <host>` | none | Repeatable. Skip MITM for these hosts (e.g. captive-portal endpoints). |
| `--workbench` | off | Stream captured frames live to the workbench at `/bowire/interceptor` for inline inspection. |

### CA storage

The auto-generated CA lives at `~/.bowire/proxy-ca.{pfx,crt}`. Install it once into the client trust store; every subsequent `bowire proxy` run reuses the same CA. Delete the files to force regeneration (e.g. when sharing the laptop or rotating dev creds). The private key never leaves the local machine. If you commit the `.bwr` recording to a repo, the CA is **not** included — only the captured plaintext.

## Reverse proxy

The Interceptor package also ships a reverse-proxy host (`BowireReverseProxyHost`) that lets you mount one or more downstream targets behind the workbench and capture every request that flows through. Useful for "I want to inspect what my React app sends to its API without changing either side" — point the React app at `localhost:5180/api` and configure the workbench to forward to the real API.

```csharp
app.UseBowireInterceptor(opts =>
{
    opts.ReverseProxy = new()
    {
        Targets =
        {
            ["/api"] = "https://api.example.com",
            ["/auth"] = "https://auth.example.com",
        },
        PreserveHostHeader = false,
    };
});
```

The reverse-proxy registry is exposed at `/api/tools/reverse-proxy/*` for runtime management; flows land in the same Interceptor rail as the middleware-captured ones.

## From capture to template

Right-click any flow in the Interceptor rail for one-click conversions:

- **Save to recording** — adds the flow to a named recording file. Subsequent runs against the same target append to the same file.
- **Open in Compose** — drops the flow into the [Compose rail](compose.md) as an editable request you can mutate + resend.
- **Convert to mock fixture** — adds the response shape to the Mock Rules sub-tab; the next matching inbound request gets short-circuited.
- **Convert to vulnerability template** — wraps the flow with `attack: true` and a `vulnerability:` block (CWE, severity); writes a JSON file `bowire scan` consumes.
- **Convert to fuzz baseline** — saves the request as the unmutated baseline `bowire fuzz --template` reads.
- **Copy as cURL** — shell-ready, with headers and body preserved.

Every conversion is a regular file write — version-control the result alongside your other test fixtures.

## Scope

- **HTTP/1.1 and HTTP/2** are intercepted natively (both middleware + `bowire proxy`).
- **HTTP/3 / QUIC** is currently passed through unmodified by `bowire proxy`; middleware captures it natively when Kestrel terminates QUIC.
- **WebSocket upgrade traffic** is captured frame-by-frame.
- **gRPC over HTTP/2** capture works for both binary protobuf and JSON-transcoded surfaces.

## Embedded vs. standalone

The Interceptor package is referenced transitively by `Bundle.Workbench`, so the standalone Tool always has it. Embedded hosts that drop the bundle and pick per-package references need to add `Kuestenlogik.Bowire.Interceptor` explicitly if they want the rail + middleware + reverse-proxy host + the `/api/intercepted/*` + `/api/tools/reverse-proxy/*` endpoints.

Embedded hosts that don't reference the package see no rail, no middleware, no admin endpoints — by design. The `app.UseBowireInterceptor()` call sites compile unchanged across the v2.0 → v2.1 jump because the namespace stayed at `Kuestenlogik.Bowire.Interceptor`; only the assembly moved.

## Settings IA

In v2.1's [extension-point-organised Settings tree](../release-notes/v2.1.0.md#settings-tree-organized-by-extension-point), the Interceptor surfaces under:

- **Plugins → Interceptor** — buffer size, body cap, redaction rules, enable/disable
- **Plugins → Reverse Proxy** — target mounts, host-header preservation
- **Settings → Rails → Interceptor** — show/hide the rail in the strip

## Screenshot

<!-- TODO: capture Interceptor rail — Flows sub-tab with intercepted requests list. Neither Combined (5101) nor standalone Tool (5180) ships the Kuestenlogik.Bowire.Interceptor package in the sample set on this branch; the rail isn't in the rail strip. Re-capture after the package lands in Bundle.Workbench for the demo Tool. -->


## Related

- [`bowire scan`](scan.md) — replay captured templates as security probes
- [`bowire fuzz`](fuzz.md) — use the captured baseline for field-level mutation
- [`bowire mock`](mock-server.md) — replay the captured response shape as a mock endpoint
- [Recording format](recording.md) — `.bwr` schema reference
- [Compose](compose.md) — drop intercepted flows into the request builder
- [Security-testing ADR](../architecture/security-testing.md)
