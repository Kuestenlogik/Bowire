# Bowire Release Notes

Hand-curated highlights per release. The full commit list is auto-generated
by GitHub on the release page; this file is the editorial layer above it.

Format per version: `## <version> — <date>` heading, free-form prose +
bullets summarising what shipped, why it matters, and any breaking changes.
The release workflow extracts the most-recent matching block at tag time
and uses it as the GitHub Release body.

---

## 1.1.0 — 2026-05-11

### ⚠ Breaking change for the standalone CLI

**The `bowire` tool now mounts the workbench at `/` instead of `/bowire`.**

If you double-click `bowire.exe` or run `bowire --url …` from a terminal,
your browser opens at `http://localhost:5080/` (was `…/5080/bowire`).
The optional MCP adapter moves from `/bowire/mcp` to `/mcp` for the same
reason. Update any bookmarks; AI-agent configs that pointed at the
`/bowire/mcp` adapter endpoint need to be re-pointed at `/mcp`.

**Embedded callers are not affected.** `app.MapBowire()` (no pattern arg)
keeps defaulting to `/bowire`; `app.MapBowire("/your/prefix")` keeps
mounting wherever you told it to. The route-pattern arg is still
authoritative. The standalone tool now passes `"/"` explicitly because
it has no host app sharing the route table.

### Highlights

- **Standalone workbench URL is now the site root.** No more `/bowire`
  hop; the auto-open browser URL and the startup banner both point at
  `http://localhost:5080/`. The MCP adapter (opt-in via
  `--enable-mcp-adapter`) moves alongside it to `/mcp`.
- **"gRPC failed to map discovery endpoints" warning gone** on standalone
  startup. `MapDiscoveryEndpoints(...)` now only runs in `BowireMode.Embedded`
  where Bowire is mounted inside a real gRPC / SignalR / Socket.IO host;
  in standalone the CLI is a client, there's nothing to reflect.
  `BrowserUiHost` also now calls `builder.Services.AddBowire()` so every
  plugin's DI prerequisites land in the container.
- **Coverage uplift to ~90% globally.** Five packages at 100% (Mcp,
  Protocol.Mcp, Protocol.OData, Protocol.SocketIo, Protocol.GraphQL); the
  rest of the protocol plugins all sit at 85% or higher. CLI tool jumped
  from 46% → 97% across two rounds.
- **MCP docs cover all four roles in one place.** Bowire as MCP client,
  Bowire's adapter wrapping discovered APIs, Bowire-as-MCP-server over
  HTTP (`AddBowireMcp` + `MapBowireMcp`), Bowire-as-MCP-server over stdio
  (`bowire mcp serve`). Claude Desktop config examples for both standalone
  and embedded mounts.
- **Storm → Surgewave brand migration (Phase 3)** swept across the main
  repo: docs, namespaces (`Protocol.Storm` → `…Surgewave`), JS detector,
  CSS classes, URL scheme (`storm://` → `surgewave://`), plugin slug,
  package id. Phase 1+2 (sibling plugin + SDK) had landed earlier;
  this completes the rename inside the main workbench.
- **OAuth proxy uses `IHttpClientFactory`** instead of `new HttpClient()`
  per request — handler pooling, clean test seam, and `Bowire:Trust
  LocalhostCert` opt-in now also applies to OAuth proxy calls against a
  local IdP with a self-signed cert.

### Dependency updates

- `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 1.2.0 → 1.3.0
- `coverlet.collector` 6.0.4 → 10.0.0 *(test-only)*
- Bundled `morphdom` (the workbench's only third-party JS lib, served
  locally because the workbench has a no-network guarantee) refreshed
  to **2.7.8** with a `/*! morphdom 2.7.8 — ... */` version header so
  the version is greppable in source and in the minified bundle.

### Migration

- **Bookmarks pointing at `http://localhost:5080/bowire`** → drop the
  `/bowire` suffix.
- **AI agent configs pointing at `http://localhost:5080/bowire/mcp`** →
  switch to `/mcp`.
- **Embedded callers using `app.MapBowire()`** → no change. Your prefix
  defaults to `/bowire` still, exactly as before. The tool's behaviour
  is the only thing that shifted.

---

## 1.0.12 — 2026-05-06

### Highlights

- **Custom domain `bowire.io`.** The marketing site, docs, and downloads now
  live at <https://bowire.io>. HTTPS is enforced, the certificate auto-renews
  via Let's Encrypt. The old `kuestenlogik.github.io/Bowire/` URLs continue
  to work via 301 redirect, but every reference inside Bowire (NuGet
  package URLs, MSI's Apps & Features URL, in-app About / landing-footer
  links) now points at the apex domain.
- **Full release pipeline back online.** Tag-driven publish from a single
  workflow now builds:
  - 14 NuGet packages (core, mock, mcp, every protocol plugin, the CLI tool)
  - 6 self-contained standalone bundles (Linux / Windows / macOS × x64 / arm64)
  - MSI installers (x64 + arm64), DEB + RPM packages (x64 + arm64)
  - Multi-arch OCI container to GHCR
  - DocFX HTML zip + a custom-rendered PDF docs snapshot
  - Auto-PR to `microsoft/winget-pkgs` for stable tags
- **Samples page.** New <https://bowire.io/samples.html> surfaces the eleven
  reference apps from `Kuestenlogik.Bowire.Samples` — one per protocol plus
  the Combined showcase that runs five protocols against the same
  `HarborStore`.
- **Marketing-site polish.** Real Surgewave / Apache Kafka marks on the
  downloads page, Akka.NET card added, native-installer links wired up to
  `releases/latest/download/`, copy-button layout fixed for long package
  names without widening the cards.

### Pipeline plumbing

The pipeline split into three jobs (Linux artefacts + container, Windows
MSI, GitHub Release) is the right shape for the WiX v5 Windows-only
constraint and lets the Linux + Windows builds run in parallel. A handful
of edge cases the old single-job pipeline never reproduced got ironed out
along the way:

- nfpm `contents.src` doesn't expand env vars on its own (only top-level
  fields), so the workflow now pre-runs `envsubst` against `nfpm.yaml`.
- `dotnet publish -t:PublishContainer -p:ContainerRuntimeIdentifiers="x;y"`
  needs both RIDs in the assets file before it iterates them — fix is an
  explicit multi-RID `dotnet restore` ahead of the container publish.
- DocFX's `pdf` command relies on a Playwright browser auto-install that
  races on CI. Replaced with a custom `scripts/build-docs-pdf.js` that
  walks the rendered HTML tree and merges per-page PDFs via `pdf-lib`.

### Test stability

- `OpenApiUploadStore` is a static singleton mutated by two test classes;
  xunit.v3 ran them in parallel and `Assert.Single` would race. Both
  classes are now in the same `[Collection]` so they serialise.

---

## 1.0.11 — 2026-05-05

Socket.IO namespace selection (`X-Bowire-SocketIo-Namespace` header), plus
the rolling site/screenshot refresh from the 1.0.10 method-detail header
layout fix.

---

## 1.0.10 — 2026-05-05

Method-detail header layout fix in the workbench UI.

---

## Older releases

For 1.0.9 and earlier, see the auto-generated entries on
<https://github.com/Kuestenlogik/Bowire/releases>.
