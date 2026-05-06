# chocolateyInstall.ps1 for Bowire
#
# Downloads the per-machine x64 MSI from the matching GitHub release,
# verifies it against the SHA256 baked in by the release pipeline,
# then runs `msiexec /qn` to install. The MSI itself adds the
# install folder to system PATH and registers an Apps & Features
# entry — so after install, `bowire` works in any new shell.
#
# Placeholders:
#   __VERSION__   — patched by chocolatey.yml workflow before pack
#   __SHA256__    — SHA256 of Bowire-<version>-x64.msi from the release
#
# Why x64-only: ARM64 Windows users are <1% of the install base and
# most still pull the win-arm64.zip directly. If demand picks up we
# can add an arch-detection branch here using `Get-CimInstance
# Win32_Processor`.

$ErrorActionPreference = 'Stop'

$packageName  = 'bowire'
$version      = '__VERSION__'
$url64        = "https://github.com/Kuestenlogik/Bowire/releases/download/v$version/Bowire-$version-x64.msi"
$checksum64   = '__SHA256__'

$packageArgs = @{
    packageName    = $packageName
    fileType       = 'msi'
    url64bit       = $url64
    checksum64     = $checksum64
    checksumType64 = 'sha256'
    softwareName   = 'Bowire*'
    silentArgs     = '/qn /norestart'
    validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
