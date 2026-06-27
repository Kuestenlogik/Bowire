#!/usr/bin/env pwsh
# Push the current icon artefacts from images/ into every consumer
# location (embedded Bowire UI, docfx docs, marketing site). Also
# renders one extra file that doesn't live in images/ — the iOS
# apple-touch-icon (180×180 PNG).
#
# Usage:
#   scripts/distribute-icons.ps1
#
# Does NOT regenerate images/ — run scripts/generate-icons.ps1 first
# if the sources need rebuilding. Safe to run any time: it's a pure
# copy/render step.
#
# Shares the transient tmp/icon-gen/ deps pocket with
# generate-icons.ps1 (sharp is the only runtime requirement). First
# run writes the inline package.json and npm-installs; subsequent
# runs skip the install.

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
    Write-Host 'Installing icon-tool dependencies (one-time) ...'
    Push-Location $DepsDir
    try { npm install --silent } finally { Pop-Location }
}

$env:NODE_PATH = Join-Path $DepsDir 'node_modules'
node (Join-Path $ScriptDir 'distribute-icons.js')
