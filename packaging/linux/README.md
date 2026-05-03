# Linux packages

Three formats live under here:

```
packaging/linux/
├── nfpm.yaml         # Single config → DEB + RPM (x86_64 + aarch64)
├── aur/PKGBUILD      # Arch User Repository build script
└── README.md         # this file
```

The release pipeline (`.github/workflows/release.yml`) attaches all
of these to each GitHub release:

- `bowire_<v>_amd64.deb`
- `bowire_<v>_arm64.deb`
- `bowire-<v>.x86_64.rpm`
- `bowire-<v>.aarch64.rpm`
- `bowire-linux-x64.tar.gz` (self-contained tarball, distro-agnostic)
- `bowire-linux-arm64.tar.gz`

The `PKGBUILD` is published separately to the [Arch User
Repository](https://aur.archlinux.org/) — see the AUR section below.

## End-user install

### Debian / Ubuntu / Mint

```bash
curl -L -o bowire.deb https://github.com/Kuestenlogik/Bowire/releases/latest/download/bowire_<v>_amd64.deb
sudo apt install ./bowire.deb
```

`apt` handles the dependency on `libicu*` automatically.

### Fedora / RHEL / openSUSE

```bash
sudo dnf install https://github.com/Kuestenlogik/Bowire/releases/download/v<v>/bowire-<v>.x86_64.rpm
```

### Arch / Manjaro

```bash
yay -S bowire               # via the AUR helper of choice
# or
git clone https://aur.archlinux.org/bowire.git
cd bowire && makepkg -si
```

### Anything else (Alpine, NixOS, generic)

Use the portable tarball:

```bash
curl -LO https://github.com/Kuestenlogik/Bowire/releases/latest/download/bowire-linux-x64.tar.gz
tar xzf bowire-linux-x64.tar.gz
sudo mv bowire-linux-x64 /opt/bowire
sudo ln -s /opt/bowire/bowire /usr/local/bin/bowire
```

## Local build (development)

The same nfpm config used by CI works locally:

```bash
# 1. Install nfpm (Go binary, no SDK required)
NFPM=2.43.0
curl -sSL "https://github.com/goreleaser/nfpm/releases/download/v${NFPM}/nfpm_${NFPM}_Linux_x86_64.tar.gz" \
    | sudo tar -xz -C /usr/local/bin nfpm

# 2. Self-contained .NET publish
dotnet publish src/Kuestenlogik.Bowire.Tool -c Release -r linux-x64 \
    --self-contained -o publish/bowire-linux-x64

# 3. Build the packages
VERSION=0.9.4 ARCH=amd64 PUBLISH_DIR=publish/bowire-linux-x64 \
    nfpm pkg --packager deb --target dist/bowire_${VERSION}_amd64.deb \
        --config packaging/linux/nfpm.yaml
VERSION=0.9.4 ARCH=amd64 PUBLISH_DIR=publish/bowire-linux-x64 \
    nfpm pkg --packager rpm --target dist/bowire-${VERSION}.x86_64.rpm \
        --config packaging/linux/nfpm.yaml
```

`nfpm.yaml` reads `${VERSION}`, `${ARCH}`, and `${PUBLISH_DIR}` from
the environment, so the same config drives both formats and both
architectures.

## Layout on disk

| Path | Owner | Purpose |
|------|-------|---------|
| `/opt/bowire/bowire` | package | Self-contained .NET 10 entrypoint exe |
| `/opt/bowire/*.dll`   | package | Bundled framework + plugins |
| `/usr/bin/bowire`     | package | Symlink → `/opt/bowire/bowire` |
| `~/.bowire/`          | user    | Plugins, recordings, environments (created on first run) |

The `/opt/bowire/` layout reflects the .NET self-contained publish
shape — the entry exe expects all its sibling DLLs in the same
directory. Splitting them across `/usr/lib/bowire/` would break the
runtime's assembly probe path. `/usr/bin/bowire` is a symlink rather
than a wrapper script so `which bowire` resolves to the real binary.

## AUR submission

The AUR is hosted on a separate Arch-side git server, not on GitHub.
First-time submission is manual:

```bash
# 1. SSH key registered at https://aur.archlinux.org/account/<user>/
ssh-copy-id aur@aur.archlinux.org

# 2. Clone the (initially empty) AUR repo
git clone ssh://aur@aur.archlinux.org/bowire.git
cd bowire

# 3. Drop in our PKGBUILD + generate the .SRCINFO metadata
cp /path/to/Bowire/packaging/linux/aur/PKGBUILD .
makepkg --printsrcinfo > .SRCINFO

# 4. Commit + push
git add PKGBUILD .SRCINFO
git commit -m "bowire 0.9.4 — initial AUR submission"
git push
```

For each subsequent release: bump `pkgver` in PKGBUILD, refresh
checksums via `updpkgsums`, regenerate `.SRCINFO`, push. A small
GitHub Action could automate that loop (see e.g.
[KSXGitHub/github-actions-deploy-aur](https://github.com/KSXGitHub/github-actions-deploy-aur)),
but the AUR convention is to keep PKGBUILD edits under direct review,
so we stay manual for now.

## Code signing (optional)

DEB and RPM both support detached signatures, which `apt`/`dnf`
verify when configured with the corresponding GPG key. For OSS we
skip signing — users are expected to verify SHA256 against the
release page. If a signed channel becomes worthwhile later, generate
a maintainer key, hand it to `nfpm` via `signing.key_file`, and host
the public key on the release page or a dedicated apt/dnf repo.
