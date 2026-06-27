#!/usr/bin/env pwsh
# Regenerate every derivative icon artefact under images/ from the
# two committed source SVGs (bowire_logo.svg + bowire_logo_small.svg).
#
# Usage:
#   scripts/generate-icons.ps1
#
# Does NOT touch wwwroot/docs/site — use scripts/distribute-icons.ps1
# afterwards to push the regenerated files to consumer locations.
#
# Dependencies (sharp, png-to-ico) live in tmp/icon-gen/, which is
# gitignored via the repo-wide `tmp/` rule and fully transient.
# First run writes the inline package.json, runs `npm install`
# there, and caches the deps; any subsequent run skips the install.

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$DepsDir = Join-Path $ScriptDir '..' 'tmp' 'icon-gen'

if (-not (Test-Path (Join-Path $DepsDir 'node_modules'))) {
    New-Item -ItemType Directory -Force -Path $DepsDir | Out-Null
    @'
{
  "name": "bowire-icon-tool-deps",
  "version": "1.0.0",
  "private": true,
  "description": "Transient dependency pocket for scripts/generate-icons.js and scripts/distribute-icons.js. Regenerated on demand from the wrappers — do not edit by hand.",
  "dependencies": {
    "png-to-ico": "^2.1.8",
    "sharp": "^0.34.0"
  }
}
'@ | Set-Content -Path (Join-Path $DepsDir 'package.json') -Encoding UTF8
    Write-Host 'Installing icon-generator dependencies (one-time) ...'
    Push-Location $DepsDir
    try { npm install --silent } finally { Pop-Location }
}

$env:NODE_PATH = Join-Path $DepsDir 'node_modules'
node (Join-Path $ScriptDir 'generate-icons.js')
