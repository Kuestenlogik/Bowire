# Chocolatey distribution

Bowire ships to Chocolatey on every stable tag through the
[`chocolatey.yml`](../../.github/workflows/chocolatey.yml) workflow.

## End-user install

```powershell
choco install bowire
```

Upgrade or uninstall:

```powershell
choco upgrade bowire
choco uninstall bowire
```

The package wraps the per-machine x64 MSI from the matching GitHub
release — same artefact the Winget channel uses, so the two channels
stay in lockstep. The MSI adds the install folder to the system PATH,
so `bowire ...` works in any new shell after install.

## Layout

```
packaging/chocolatey/
├── bowire.nuspec               # Package metadata (id, title, urls, tags, description)
├── tools/
│   ├── chocolateyInstall.ps1   # Download + checksum-verify + msiexec /qn the MSI
│   └── chocolateyUninstall.ps1 # Resolve product code + msiexec /x
└── README.md                   # this file
```

`__VERSION__` and `__SHA256__` placeholders in `bowire.nuspec` and
`chocolateyInstall.ps1` get patched in by the release workflow before
`choco pack` runs.

## Workflow

`chocolatey.yml` triggers on `release: published` (and via
`workflow_dispatch` for re-runs):

1. Resolve version + release date from the tag.
2. Download `Bowire-<version>-x64.msi` from the GitHub release.
3. Compute SHA256 of the MSI.
4. Patch placeholders in `nuspec` + `chocolateyInstall.ps1`.
5. `choco pack` against the patched files → `bowire.<version>.nupkg`.
6. `choco push` to the Chocolatey community feed using the
   `CHOCOLATEY_API_KEY` secret.

## One-time setup before the first release

The push step needs the `CHOCOLATEY_API_KEY` repository secret:

1. Create a Chocolatey community account at
   <https://community.chocolatey.org/account/Register> if you don't
   already have one.
2. Profile → API Keys → copy the key.
3. Add it as a GitHub secret:

   ```powershell
   gh secret set CHOCOLATEY_API_KEY --repo Kuestenlogik/Bowire
   ```

Without the secret the workflow exits the push step gracefully — the
package is still built and visible as a workflow artefact for manual
publishing if needed.

## Moderation note

Chocolatey community-feed packages go through a **moderation queue**.
The first submission for a brand-new package can take 1-7 days while
a human reviews the install scripts; subsequent versions usually pass
within hours. Moderation runs async on the chocolatey side — the
release workflow doesn't block on it.

If the moderators flag something on the first submission (typically
checksum verification or wording in `<description>`), respond on the
package page; the workflow keeps building new versions in parallel
without re-triggering moderation on the already-pending one.
