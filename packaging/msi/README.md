# Windows Installer (MSI)

WiX v5 source for the Bowire MSI. The MSI lands users at
`Program Files\Bowire\bowire.exe`, registers the install folder
on the system `PATH`, drops a Start-menu shortcut, and writes
"Apps & Features" metadata so uninstall + future Microsoft Store
listing both look polished.

## Layout

```
packaging/msi/
├── Bowire.wxs    # WiX v5 product source
└── README.md      # this file
```

## Local build

```bash
# One-time: install the WiX .NET tool
dotnet tool install --global wix

# Publish the standalone Windows binaries
dotnet publish src/Kuestenlogik.Bowire.Tool -c Release -r win-x64 \
    --self-contained -o publish/bowire-win-x64

# Build the MSI from the publish output
wix build packaging/msi/Bowire.wxs \
    -arch x64 \
    -d Version=0.9.4 \
    -d PublishDir=publish/bowire-win-x64 \
    -o publish/Bowire-0.9.4-x64.msi
```

The same command with `-arch arm64` and the matching arm64 publish
output produces the arm64 MSI. WiX v5 runs cross-platform, so the
release pipeline builds both architectures on the Linux runner.

## Identity

| Field | Value |
|-------|-------|
| Product name | Bowire |
| Manufacturer | Küstenlogik |
| Install scope | per-machine (Program Files\Bowire) |
| `UpgradeCode` | `C0B72D33-31F5-4349-9748-9A0DBB0C51AF` |
| `ProductCode` | regenerated per build (WiX default) |

The `UpgradeCode` is the **fixed product identity** — never change it
once a release ships, otherwise Windows Installer treats the new MSI
as an unrelated product and major-upgrades break.

The `ProductCode` is regenerated for every build so each version
counts as a distinct package; `MajorUpgrade` reads the `UpgradeCode`
and silently uninstalls older versions before the new MSI lands.
The release pipeline extracts the per-build ProductCode from each
MSI and feeds it into the winget manifest (see
[`packaging/winget/`](../winget/)).

## Anatomy

| Section in `Bowire.wxs`        | What it does |
|---------------------------------|--------------|
| `<Package>`                     | Product identity, version, scope |
| `<MajorUpgrade>`                | Silent uninstall of older versions on install |
| `<MediaTemplate EmbedCab="yes">` | Single-file MSI (no loose CAB) |
| `<Property Id="ARP*">`          | Apps & Features fields (publisher, help URL, support URL) |
| `<Icon>`                        | App icon shown in ARP + Start menu |
| `<ComponentGroup Id="ProductFiles">` | Auto-harvested files from the publish folder |
| `<StartMenuShortcut>`           | Start-menu entry pointing at `bowire.exe` |
| `<PathEntry>`                   | Adds `INSTALLFOLDER` to system `PATH` (append, not prepend, so we don't shadow other tools) |
| `<Feature Id="MainFeature">`    | Single feature — Bowire is all-or-nothing |

## Manual install / uninstall

```pwsh
# Interactive install
msiexec /i Bowire-0.9.4-x64.msi

# Silent install (CI / automation)
msiexec /i Bowire-0.9.4-x64.msi /qn

# Repair (re-extract files)
msiexec /fa Bowire-0.9.4-x64.msi

# Uninstall
msiexec /x Bowire-0.9.4-x64.msi /qn
```

## Code signing (optional)

Unsigned MSIs work but trigger SmartScreen on first run. To avoid that:

```pwsh
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 \
    /n "Küstenlogik" Bowire-0.9.4-x64.msi
```

Free options for the cert itself:
- [SignPath.io](https://about.signpath.io/product/open-source) for
  open-source projects (requires manual review).
- Microsoft Trusted Signing — paid, ~$10/month.

Without a cert, users see the SmartScreen warning the first ~few
hundred installs until the file builds reputation; after that the
warning goes away on its own.
