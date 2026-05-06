# Bowire Release Notes

Hand-curated highlights per release. The full commit list is auto-generated
by GitHub on the release page; this file is the editorial layer above it.

Format per version: `## <version> — <date>` heading, free-form prose +
bullets summarising what shipped, why it matters, and any breaking changes.
The release workflow extracts the most-recent matching block at tag time
and uses it as the GitHub Release body.

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
- **Marketing-site polish.** Real Storm / Apache Kafka marks on the
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
