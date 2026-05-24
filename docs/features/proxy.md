---
summary: 'Intercepting MITM proxy that captures real client / server traffic as Bowire recordings. The auto-generated CA is installed once into the client''s trust store; captured flows become templates that scan / fuzz / mock can replay.'
---

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
