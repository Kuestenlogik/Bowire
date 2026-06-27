#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Bowire: NuGet packages + self-contained standalone executables.

.PARAMETER SkipNuGet
    Skip NuGet package generation

.PARAMETER SkipExecutables
    Skip self-contained executables

.PARAMETER Runtime
    Target runtime (default: win-x64). Use "all" for all platforms.

.PARAMETER Configuration
    Build configuration (default: Release)

.EXAMPLE
    .\scripts\publish.ps1

.EXAMPLE
    .\scripts\publish.ps1 -Runtime all

.EXAMPLE
    .\scripts\publish.ps1 -Runtime linux-x64 -SkipNuGet
#>

[CmdletBinding()]
param(
    [switch]$SkipNuGet,
    [switch]$SkipExecutables,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$nugetDir = Join-Path $root "artifacts" "packages"
$publishDir = Join-Path $root "artifacts" "publish"
$slnx = Join-Path $root "Kuestenlogik.Bowire.slnx"

# Determine version
if (-not $Version) {
    $commitCount = git rev-list --count HEAD 2>$null
    $shortSha = git rev-parse --short HEAD 2>$null
    if ($commitCount -and $shortSha) {
        $Version = "0.9.4-dev.$commitCount.$shortSha"
    } else {
        $Version = "0.9.4-dev"
    }
}

$runtimes = if ($Runtime -eq "all") {
    @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
} else {
    @($Runtime)
}

Write-Host ""
Write-Host "  Bowire — Publish" -ForegroundColor Cyan
Write-Host "  Version:       $Version" -ForegroundColor Gray
Write-Host "  Configuration: $Configuration" -ForegroundColor Gray
Write-Host "  Runtimes:      $($runtimes -join ', ')" -ForegroundColor Gray
Write-Host ""

$failed = 0

# ── 1. NuGet Packages ────────────────────────────────────────────────────────

if (-not $SkipNuGet) {
    Write-Host "━━━ NuGet Packages ━━━" -ForegroundColor Yellow

    dotnet pack $slnx -c $Configuration -p:Version=$Version --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED: dotnet pack" -ForegroundColor Red
        $failed++
    } else {
        $packages = Get-ChildItem -Path $nugetDir -Filter "*.nupkg" -ErrorAction SilentlyContinue
        foreach ($pkg in $packages) {
            $size = [math]::Round($pkg.Length / 1KB, 1)
            Write-Host "  $($pkg.Name) (${size} KB)" -ForegroundColor Green
        }
        Write-Host "  Done → $nugetDir" -ForegroundColor Gray
    }
    Write-Host ""
}

# ── 2. Self-Contained Executables ────────────────────────────────────────────

if (-not $SkipExecutables) {
    Write-Host "━━━ Standalone Executables ━━━" -ForegroundColor Yellow

    $serverProject = Join-Path $root "src" "Kuestenlogik.Bowire.Tool"

    foreach ($rt in $runtimes) {
        $outDir = Join-Path $publishDir "bowire-$rt"

        Write-Host "  bowire-$rt..." -NoNewline
        dotnet publish $serverProject -c $Configuration -r $rt --self-contained `
            -p:Version=$Version `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=false `
            -p:ReadyToRun=true `
            -p:DebuggerSupport=false `
            -o $outDir --nologo -v quiet 2>&1 | Out-Null

        if ($LASTEXITCODE -ne 0) {
            Write-Host " FAILED" -ForegroundColor Red
            $failed++
        } else {
            $size = [math]::Round((Get-ChildItem -Path $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
            Write-Host " ${size} MB → $outDir" -ForegroundColor Green
        }
    }
    Write-Host ""
}

# ── Summary ──────────────────────────────────────────────────────────────────

if ($failed -gt 0) {
    Write-Host "  $failed step(s) failed!" -ForegroundColor Red
    exit 1
} else {
    Write-Host "  All done." -ForegroundColor Green
    Write-Host ""
    Write-Host "  Usage:" -ForegroundColor Gray
    Write-Host "    NuGet:      dotnet add package Kuestenlogik.Bowire --version $Version" -ForegroundColor Gray
    Write-Host "    Standalone: .\artifacts\publish\bowire-$($runtimes[0])\bowire --url https://my-server:443" -ForegroundColor Gray
}
Write-Host ""
