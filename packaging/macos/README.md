# macOS / Homebrew

Bowire ships to macOS (and to Linux-Brew users) through a custom
**Homebrew tap**: `kuestenlogik/homebrew-bowire`. One formula, four
platform branches:

```
              x86_64        arm64
  macOS    osx-x64        osx-arm64
  Linux    linux-x64      linux-arm64
```

Homebrew picks the right archive at install time based on `OS.mac?`
and `Hardware::CPU.arm?`.

## End-user install

```bash
brew tap kuestenlogik/bowire
brew install bowire
```

Or in one shot:

```bash
brew install kuestenlogik/bowire/bowire
```

`brew upgrade bowire` picks up new releases automatically once the
tap-update workflow has pushed the refreshed formula.

## Layout

```
packaging/macos/
├── Formula/
│   └── bowire.rb     # Formula template (placeholders are rendered
│                      # at release time by .github/workflows/homebrew.yml)
└── README.md          # this file
```

The rendered formula lands at
`https://github.com/kuestenlogik/homebrew-bowire/blob/main/Formula/bowire.rb`
on every release.

## One-time tap-repo setup

Before the first release the tap repo must exist. Naming convention
is fixed by Homebrew: `homebrew-<tap>` resolves `brew tap
<owner>/<tap>` to `github.com/<owner>/homebrew-<tap>.git`.

```bash
# 1. Create the (initially empty) repo
gh repo create kuestenlogik/homebrew-bowire --public --description "Homebrew tap for Bowire"

# 2. Seed it with a stub README + an empty Formula/ directory so the
#    auto-update workflow has something to push into
git clone https://github.com/kuestenlogik/homebrew-bowire.git
cd homebrew-bowire
mkdir Formula
cat > README.md <<'EOF'
# homebrew-bowire

Homebrew tap for [Bowire](https://github.com/Kuestenlogik/Bowire).

```
brew tap kuestenlogik/bowire
brew install bowire
```

The formula in this repository is auto-generated — see the
[main repo's release workflow](https://github.com/Kuestenlogik/Bowire/blob/main/.github/workflows/homebrew.yml)
for source of truth.
EOF
git add . && git commit -m "Initial tap seed"
git push
```

Then create a fine-grained PAT with `Contents: Read & Write` on
`kuestenlogik/homebrew-bowire` only, and store it as repo secret
`HOMEBREW_TAP_TOKEN` in `Kuestenlogik/Bowire`.

Without the secret the homebrew workflow exits gracefully — releases
still go out, just without the tap update.

## Per-release flow (automatic)

1. Tag → `release.yml` builds + uploads `bowire-{osx,linux}-{x64,arm64}.tar.gz`.
2. Release publishes → `homebrew.yml` fires.
3. Workflow downloads the four tarballs, computes SHA256 for each,
   renders `bowire.rb` from the template, and pushes the rendered
   formula directly to `kuestenlogik/homebrew-bowire:main`.
4. Users see the new version on `brew update && brew upgrade`.

The push is direct (no PR review) — the tap is owned by the same
maintainer as the source repo, and the formula is fully derived from
the release artefacts.

## Local smoke test

Before relying on the workflow, render a formula by hand and install
from a local tap:

```bash
# 1. Render the template against a real local build
VERSION=0.9.4
ARM=$(shasum -a 256 publish/bowire-osx-arm64.tar.gz | cut -d' ' -f1)
X64=$(shasum -a 256 publish/bowire-osx-x64.tar.gz   | cut -d' ' -f1)
sed \
    -e "s|__VERSION__|$VERSION|g" \
    -e "s|__SHA256_MACOS_ARM64__|$ARM|g" \
    -e "s|__SHA256_MACOS_X64__|$X64|g" \
    -e "s|__SHA256_LINUX_ARM64__|0|g" \
    -e "s|__SHA256_LINUX_X64__|0|g" \
    packaging/macos/Formula/bowire.rb > /tmp/bowire.rb

# 2. Install from the rendered file
brew install --formula /tmp/bowire.rb
bowire --version

# 3. Uninstall + clean up
brew uninstall bowire
```

`brew audit --strict` and `brew test bowire` are the two checks the
CI workflow could add later if the formula starts to grow custom
options.

## Submission to homebrew-core (later, optional)

The tap covers everything we need for distribution. Submitting to
`homebrew/core` (the official repo) would surface Bowire in
`brew search` without `brew tap` first. Trade-offs:

- **Discoverability** — better, but homebrew-core has a soft 75-star
  / 1k-installs heuristic before they accept new formulae for
  developer tooling. Worth waiting for traction.
- **Strict review** — the formula has to pass `brew audit --strict`
  in clean state, no failed tests. Our current formula meets that
  bar; the on-going maintenance burden is the same as for the tap.
- **Bottle build pipeline** — homebrew-core builds prebuilt bottles
  on its own infrastructure. Faster install for end users than
  pulling our tarball + extracting.

The path is: open a PR against `Homebrew/homebrew-core` with the
formula moved into `Formula/b/bowire.rb`, drop the `version` line
(homebrew-core derives it from the URL), and let the homebrew
maintainers + their CI take it from there.

## Code signing / notarization (not done)

The published tarballs are unsigned. Apple Gatekeeper greets
downloaded `.tar.gz` archives with a security warning the first
time `bowire` runs (right-click → Open → Allow once). For a CLI
distributed via `brew`, that's tolerable — `brew install` extracts
into `/opt/homebrew/Cellar/`, which is outside Gatekeeper's quarantine
scope, so the warning never fires for brew users.

If we ever ship a **`.pkg` installer** alongside the tarball,
notarization (via `notarytool`) becomes a hard requirement:
Apple-signed `.pkg`s installed without notarization are blocked on
modern macOS by default. Add at the same time we add an
`Apple Developer Program` membership ($99/year).
