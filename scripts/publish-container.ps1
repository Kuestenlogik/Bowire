#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Bowire — Build a self-contained OCI container image as a tarball.

.DESCRIPTION
    Uses the .NET 10 SDK's built-in container support
    (`dotnet publish /t:PublishContainer`) — no Dockerfile, no docker
    daemon required when ContainerArchiveOutputPath is set. Output goes to
    artifacts/containers/bowire-<version>-<arch>.tar.gz.

    Load the resulting image with:
      docker load < artifacts/containers/bowire-<version>-<arch>.tar.gz
      podman load < artifacts/containers/bowire-<version>-<arch>.tar.gz

.PARAMETER Version
    Semver string for the image tag (default: 0.9.4-dev)

.PARAMETER Arch
    Target architecture: linux-x64 (default) or linux-arm64
#>

[CmdletBinding()]
param(
    [string]$Version = "0.9.4-dev",
    [string]$Arch    = "linux-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$outputDir   = Join-Path $root "artifacts" "containers"
$archivePath = Join-Path $outputDir "bowire-$Version-$Arch.tar.gz"

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Host ""
Write-Host "  Bowire — Container build" -ForegroundColor Cyan
Write-Host "  Version: $Version" -ForegroundColor Gray
Write-Host "  Arch:    $Arch" -ForegroundColor Gray
Write-Host "  Output:  $archivePath" -ForegroundColor Gray
Write-Host ""

# PowerShell needs both ; and " escaped inside the property value.
$tags = "`"$Version`;latest`""

dotnet publish (Join-Path $root "src" "Kuestenlogik.Bowire.Tool") `
    -c Release `
    -r $Arch `
    -p:Version=$Version `
    -p:ContainerImageTags=$tags `
    -p:ContainerArchiveOutputPath=$archivePath `
    /t:PublishContainer `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED" -ForegroundColor Red
    exit $LASTEXITCODE
}

$size = (Get-Item $archivePath).Length / 1MB
$sizeStr = "{0:N1} MB" -f $size

Write-Host ""
Write-Host "  Done. ($sizeStr)" -ForegroundColor Green
Write-Host ""
Write-Host "  Load the image with:" -ForegroundColor Yellow
Write-Host "    docker load < $archivePath" -ForegroundColor Gray
Write-Host "    podman load < $archivePath" -ForegroundColor Gray
Write-Host ""
Write-Host "  Run with:" -ForegroundColor Yellow
Write-Host "    docker run --rm -p 5080:5080 kuestenlogik/bowire:$Version --url https://target:443 --no-browser" -ForegroundColor Gray
Write-Host ""
