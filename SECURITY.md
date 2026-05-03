# Security Policy

## Reporting a Vulnerability

If you've found a security issue in Bowire, please report it privately so we can fix it before it's discussed publicly.

**Email:** security@kuestenlogik.de

Please include:

- A description of the issue and the affected component (e.g. CLI, MCP server, plugin install path, recording/mock surface)
- Steps to reproduce (or a proof-of-concept)
- The Bowire version (`bowire --version` or the NuGet/container tag)
- Your assessment of impact (information disclosure, RCE, DoS, …)

We aim to acknowledge reports within **2 business days** and to ship a fix or coordinated disclosure plan within **30 days** of triage. Reporters who prefer to stay anonymous are welcome to do so; we are happy to credit you in the release notes if you wish.

Please **do not** open a public GitHub issue for security reports.

## Scope

In scope:

- The `Kuestenlogik.Bowire*` packages and the `bowire` CLI tool
- The MCP adapter (`Kuestenlogik.Bowire.Protocol.Mcp`) and the self-service MCP server (`Kuestenlogik.Bowire.Mcp`)
- The mock server (`Kuestenlogik.Bowire.Mock`) — replay, schema-only mode, control endpoints
- The plugin install / load surface (NuGet download, ALC isolation, host-provided prefix handling)
- The published OCI container image at `ghcr.io/kuestenlogik/bowire`
- The release artefacts (MSI / DEB / RPM / Homebrew / winget)

Out of scope:

- Third-party plugins published outside the `Kuestenlogik/` organisation
- Bugs in upstream dependencies (please report those to the upstream project; we will track and consume the fix)
- Findings that require an attacker to already have local code execution on the host running `bowire`
- Self-inflicted misconfiguration (e.g. running `bowire mcp serve --allow-arbitrary-urls` against an internal API and being surprised that an MCP client reaches it)

## Hardening notes

A few defaults you should know about when deploying Bowire:

- **`bowire mcp serve`** defaults to read-only + an env-seeded URL allowlist. Widening that surface (`--allow-arbitrary-urls`) exposes any reachable HTTP server to local MCP clients.
- **`bowire mock --recording`** does not require authentication on the replay endpoints. The mock is intended for local development and CI; do not expose the bound port to untrusted networks. Use `--control-token` to gate the runtime-scenario-switch endpoint.
- **`bowire plugin install`** downloads NuGet packages and loads the assemblies into the `bowire` process. Treat plugin sources the same way you would treat any other NuGet feed in your supply chain.

## Supported versions

Bowire is currently pre-1.0; we support the latest released minor version. Once 1.0 ships, we will publish a security-support matrix here.
