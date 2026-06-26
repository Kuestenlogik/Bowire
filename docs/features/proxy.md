---
summary: 'Observing + manipulating real client / server traffic. Unified Traffic rail surfaces the standalone MITM proxy CLI and the in-process middleware behind one workbench rail that adapts to the active deployment.'
---

# Traffic — observe + manipulate live requests

> **#315 — Unified rail.** The Proxy + Intercepted rails were folded into one **Traffic** rail. A given Bowire process is never both Standalone and Embedded at the same time, so the rail auto-detects from `BowireOptions.Mode` and adapts:
>
> * **Standalone** (the `bowire` CLI tool, `bowire proxy`, `bowire interceptor`) — header reads "Standalone proxy mode"; the Settings sub-tab exposes the sidecar URL.
> * **Embedded** (`MapBowire()` inside an ASP.NET host) — header reads "Embedded middleware mode"; the Settings sub-tab surfaces the in-process middleware status (was `UseBowireInterceptor()` called?).
>
> The Flows + Mock Rules sub-tabs render identically across deployments — both back-ends write into the same in-process `InterceptedFlowStore` + `InterceptorMockStore`. The legacy `/api/intercepted/*` endpoints stay mounted; `/api/traffic/*` is the new canonical alias. The legacy rail descriptors (`BowireProxyRailContribution`, `BowireInterceptedRailContribution`) ship `[Obsolete]` with `HideFromRail = true` for one release window so embedded hosts that explicitly reference them in DI keep compiling. Existing installs with `localStorage.bowire_rail_mode='proxy'` or `'intercepted'` rewrite to `'traffic'` on first paint. The `bowire proxy` and `bowire interceptor` CLI subcommands keep working unchanged.

# Intercepting Proxy — `bowire proxy`

`bowire proxy` is an MITM (Man-In-The-Middle) HTTPS proxy that captures every request and response between a client and a server, then writes the flow into a Bowire recording. The captured `.bwr` is exactly the format [`bowire scan`](scan.md), [`bowire fuzz`](fuzz.md), and [`bowire mock`](mock-server.md) read — capturing once turns into a vulnerability template, a fuzz baseline, or a mock fixture.

## Quick start

Start the proxy listening on `:8888` and let it generate its CA:

```bash
bowire proxy --port 8888 --out captured.bwr
```

Install the generated CA into the client's trust store (one-time per machine / browser):

* **Linux / macOS — system trust store**: copy `~/.bowire/proxy-ca.crt` into `/usr/local/share/ca-certificates/` and run `update-ca-certificates`.
* **Windows**: double-click `~/.bowire/proxy-ca.pfx` and import into "Trusted Root Certification Authorities".
* **Browser-specific**: Firefox uses its own store — import `~/.bowire/proxy-ca.crt` under Settings → Privacy & Security → Certificates → Import.
* **Mobile app under test**: drop the `.crt` into the app's debug certificate folder, or for iOS push it via a configuration profile.

Then point the client at the proxy (curl, browser HTTPS proxy setting, mobile-VPN profile, etc.):

```bash
HTTPS_PROXY=http://localhost:8888 curl https://api.example.com/users
```

Every call routes through Bowire; `Ctrl+C` stops the proxy and finalises the recording.

## Flags

| Flag | Default | Notes |
|---|---|---|
| `--port, -p <port>` | `8888` | Proxy listen port. |
| `--out, -o <path>` | `proxy.bwr` | Recording output file. |
| `--ca-dir <dir>` | `~/.bowire/` | Where the auto-generated CA + key live. |
| `--filter <host>` | none | Repeatable. Only record calls matching the host substring. |
| `--passthrough <host>` | none | Repeatable. Skip MITM for these hosts (e.g. captive-portal endpoints). |
| `--workbench` | off | Stream captured frames live to the workbench at `/bowire/proxy` for inline inspection. |

## From capture to template

The workbench's **Proxy view** lists every captured flow with one-click actions:

* **Send to recording** — adds the flow to a named recording file. Subsequent runs against the same target append to the same file.
* **Convert to vulnerability template** — wraps the flow with `attack: true`, prompts for a `vulnerability:` block (CWE, severity, description), and writes a JSON file the scanner consumes as a template.
* **Convert to fuzz baseline** — saves the request as the unmutated baseline `bowire fuzz --template` reads.
* **Convert to mock fixture** — adds the response shape to a recording the mock server replays.

Every conversion is a regular file write — version-control the result alongside your other test fixtures.

## CA storage

The auto-generated CA lives at `~/.bowire/proxy-ca.{pfx,crt}`. Install it once into the client trust store; every subsequent `bowire proxy` run reuses the same CA so existing trust persists. Delete the files to force regeneration (e.g. when sharing the laptop or rotating dev creds).

The private key never leaves the local machine. If you commit the `.bwr` recording to a repo, the CA is **not** included — only the captured plaintext.

## Embedded mode

When Bowire is mounted inside an ASP.NET host via `MapBowire()` instead of the standalone CLI, **the proxy listener is not in-process** — there is no `bowire proxy` running alongside the workbench, and the embedded host has no opinion about how to spawn one.

The workbench reflects this:

* The Proxy rail's empty state reads "Proxy runs outside this host" and points at this page rather than at a `bowire proxy` shell command the operator can't invoke from inside their app.
* Auto-connect to the loopback default (`http://127.0.0.1:8889`) is skipped — failing connections against a port nobody owns are noise.
* The "start the proxy with `bowire proxy` in a terminal" hint never appears in embedded mode (issue [#299](https://github.com/Kuestenlogik/Bowire/issues/299)).

If you want the rail in an embedded host, run `bowire proxy` on another machine and point the workbench at it:

* Open **Workspace Settings → General → Proxy**.
* Set **External proxy endpoint** to the URL of the remote proxy (e.g. `http://proxy.dev.internal:8889`).
* The setting travels with the workspace (`.bww`), so every team member opening the workspace inherits the same target.

Standalone CLI users are unaffected — leaving the field empty falls back to the loopback default that has worked since v1.x.

## Scope

* **HTTP/1.1 and HTTP/2** are intercepted natively.
* **HTTP/3 / QUIC** is currently passed through unmodified (UDP MITM with the same CA model is on the roadmap).
* **WebSocket upgrade traffic** is captured frame-by-frame the same way `bowire --url ws://…` would.
* **gRPC over HTTP/2** capture works for both binary protobuf and JSON transcoded surfaces.

## Related

* [`bowire scan`](scan.md) — replay captured templates as security probes
* [`bowire fuzz`](fuzz.md) — use the captured baseline for field-level mutation
* [`bowire mock`](mock-server.md) — replay the captured response shape as a mock endpoint
* [Recording format](recording.md) — `.bwr` schema reference
* [Security-testing ADR](../architecture/security-testing.md)
