# Windows Package Manager (winget)

Source-of-truth manifests for `winget install KuestenLogik.Bowire`.

The actual published manifests live at
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
under `manifests/k/KuestenLogik/Bowire/<version>/`. The files in this
directory are **placeholder templates** — every release PR upstream
is rendered from them by
[`.github/workflows/winget.yml`](../../.github/workflows/winget.yml).

## Layout

```
packaging/winget/template/
├── KuestenLogik.Bowire.yaml                 # version pointer
├── KuestenLogik.Bowire.locale.en-US.yaml    # name, description, tags, URLs
└── KuestenLogik.Bowire.installer.yaml       # per-arch ZIP URLs + SHA256
```

The templates carry these placeholders, substituted by the workflow:

| Placeholder         | Source |
|---------------------|--------|
| `__VERSION__`       | Release tag with the leading `v` stripped (`v0.1.0` → `0.1.0`) |
| `__RELEASE_DATE__`  | Workflow run date in `yyyy-MM-dd` |
| `__SHA256_X64__`    | `Get-FileHash` of the released `bowire-win-x64.zip` |
| `__SHA256_ARM64__`  | `Get-FileHash` of the released `bowire-win-arm64.zip` |

## How a release flows through

1. Push a tag → `release.yml` builds `bowire-win-x64.zip` +
   `bowire-win-arm64.zip` and attaches them to the GitHub release.
2. Release publishes → `winget.yml` fires.
3. Workflow downloads the two ZIPs, computes their SHA256, renders
   the templates into `out/manifests/k/KuestenLogik/Bowire/<version>/`,
   runs `winget validate` against them, and finally calls
   `wingetcreate submit` to open a PR on `microsoft/winget-pkgs`.

The very first PR (when `KuestenLogik.Bowire` doesn't yet exist
upstream) is reviewed by the winget maintainers — typically 3-7 days.
Every subsequent version PR is auto-merged once the standard checks
pass.

## One-time setup before the first release

A repo secret named `WINGET_TOKEN` must hold a fine-grained GitHub
PAT with `Contents: Read & Write` on a fork of
`microsoft/winget-pkgs` owned by the maintainer running the release.
Without that secret, `winget.yml` exits early without failing the
release — the GitHub release still ships, users just won't see the
package on `winget` until the secret is filled in.

Token scope, in detail:
- Resource owner: the maintainer's GitHub account
- Repository access: only `<maintainer>/winget-pkgs`
- Repository permissions: `Contents: Read & Write` (push to a branch)

## Local smoke test

Render the templates by hand and run `winget install --manifest`
against them before relying on the workflow:

```pwsh
# Replace placeholders with values from a real local build
$dir = "$env:TEMP\bowire-winget"
robocopy template $dir *.yaml /MIR

(Get-Content "$dir\*.yaml" -Raw) `
    -replace '__VERSION__',     '0.9.4' `
    -replace '__RELEASE_DATE__', (Get-Date -Format yyyy-MM-dd) `
    -replace '__SHA256_X64__',   (Get-FileHash bowire-win-x64.zip).Hash `
    -replace '__SHA256_ARM64__', (Get-FileHash bowire-win-arm64.zip).Hash |
    Set-Content "$dir\$($_.Name)"

winget validate $dir
winget install --manifest $dir
```

## Manifest schema

The YAML files conform to the **1.6.0** schema:

- <https://aka.ms/winget-manifest.version.1.6.0.schema.json>
- <https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json>
- <https://aka.ms/winget-manifest.installer.1.6.0.schema.json>

Most editors that respect the `# yaml-language-server` comment at the
top of each file auto-validate against those schemas.
