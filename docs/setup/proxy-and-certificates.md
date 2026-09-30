---
title: Proxy and certificates
summary: 'Run Bowire behind a corporate proxy: system, manual or no proxy, a bypass list, proxy credentials from the keyring, and extra trusted CAs for a TLS-inspecting proxy — globally, per workspace, or from the command line.'
---

# Proxy and certificates

In a network with a mandatory proxy, and often a TLS-inspecting one with its own certificate authority, Bowire needs to know two things: **how to get out**, and **which extra CA to trust**. Both are set in one place and apply to every protocol that can use a proxy.

## Where to set it

**Settings → Network** in the workbench. The page edits one layer at a time:

| Layer | Stored in | Wins over |
|---|---|---|
| All workspaces | `network-config.json` in your Bowire directory | the built-in default (`system`) |
| This workspace only | `network-config.<workspaceId>.json` | the global layer, field by field |
| Host configuration | `Bowire:Network:*` in appsettings, `BOWIRE_Bowire__Network__*` variables, or the CLI flags below | both files |

The host configuration wins because it is the operator's: a CI job that passes `--proxy-url` means it. The page shows which layer set each value and names anything that does not work as configured.

## Modes

- **System** (default) — follows `HTTPS_PROXY` / `HTTP_PROXY` / `NO_PROXY`, and otherwise the operating system's proxy settings.
- **Manual** — one proxy URL, e.g. `http://proxy.corp:3128` (`https://` and `socks5://` work too).
- **Direct** — never use a proxy.

Loopback targets (`localhost`, `127.0.0.1`, `::1`) always connect directly.

## Bypass list

Hosts that connect directly, separated by commas, semicolons or spaces:

| Entry | Matches |
|---|---|
| `example.com` | `example.com` and every subdomain |
| `.corp.local` / `*.corp.local` | `corp.local` and every subdomain |
| `api-*.test` | a glob; `*` matches any run of characters |
| `host.test:8443` | only that port |
| `10.0.0.0/8`, `192.168.1.7` | an IP range or address |
| `<local>` | any host name without a dot |
| `*` | everything — the proxy is never used |

## Proxy credentials

Set **Proxy user** (`DOMAIN\user` for an NTLM proxy) and **Proxy password** as a *reference*, never the password itself:

- `keyring:service/account` — read from the OS keyring (Windows Credential Manager, macOS Keychain, libsecret) when a connection needs it.
- `env:VARIABLE` — read from an environment variable; the form to use in CI.

A plain password is refused, and so is a proxy URL with credentials in it, so no settings file, export or log ever holds one.

## Trusted CAs

A TLS-inspecting proxy re-signs every certificate with its own CA. Without that CA, every HTTPS call fails. Point **Trusted CAs** at a PEM file (or paste the PEM) with the proxy's root certificate.

The bundle only rescues a *chain* error: a wrong host name or a missing certificate stays a failure whatever the bundle says. It is separate from the per-request mTLS option *allow self-signed*, which accepts any server certificate and stays clearly marked as unsafe.

## Checking a connection

**Settings → Network → Check a connection** requests a URL with the settings in force and says whether it got out, through which proxy, and why not — including the hint to add a CA bundle when the chain is untrusted, and to add credentials when the proxy answers 407.

## Command line

```bash
bowire --proxy-url http://proxy.corp:3128 --no-proxy ".corp.local,10.0.0.0/8" --ca-bundle ./corp-root.pem
bowire test flows.json --proxy-url system
bowire call --proxy-url none …
```

The flags work on every command. `--proxy-url` takes a URL or `system` / `none`. The same keys as environment variables:

```bash
BOWIRE_Bowire__Network__ProxyUrl=http://proxy.corp:3128
BOWIRE_Bowire__Network__NoProxy=.corp.local
BOWIRE_Bowire__Network__ProxyUser=CORP\\alice
BOWIRE_Bowire__Network__ProxyPasswordRef=env:PROXY_PASSWORD
BOWIRE_Bowire__Network__CaBundle=/etc/ssl/corp-root.pem
```

The MCP tool `bowire.network.get` shows the settings in force, their sources and problems, and which protocols follow the proxy.

## Which protocols follow the proxy

| Protocol | Proxy |
|---|---|
| REST, GraphQL, SOAP, OData, JSON-RPC, SSE, SignalR, WebSocket, MCP | yes |
| gRPC | `https://` channels tunnel through the proxy; a plaintext `http://` (h2c) channel cannot — put the host on the bypass list |
| Socket.IO | in the standalone tool, yes; embedded in another app, the client library connects directly |
| Pulsar | the admin REST API, yes; the broker's binary protocol connects directly |
| MQTT, NATS | no — they connect over plain TCP |
| OTLP | listens only; no outbound connections |

A call that goes direct although a proxy is configured for its host is logged as a warning, so a transport that cannot use the proxy never appears to work around it silently. A plugin reports its support through `IBowireProxySupport`; one that does not is listed as *not declared*.

## Embedded mode

Embedded in your ASP.NET app (`MapBowire()`), the settings apply to the connections Bowire's plugins make. Bowire does not change `HttpClient.DefaultProxy` there, so your application's own HttpClients are untouched. The standalone tool does set it, so plugin downloads, the update check and third-party client libraries follow the same settings.
