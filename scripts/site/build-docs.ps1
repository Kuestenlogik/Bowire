#requires -Version 5.1
# Windows equivalent of scripts/build-docs.sh — see that file for the why.
# Usage:
#   .\scripts\build-docs.ps1
#   .\scripts\build-docs.ps1 -Serve        # build then start Jekyll serve
param(
    [switch]$Serve
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "==> Restoring .NET tools"
& dotnet tool restore | Out-Null

Write-Host "==> Building main solution (release) for API metadata"
& dotnet build Kuestenlogik.Bowire.slnx -c Release --nologo -v q

Write-Host "==> Running docfx build"
& docfx build docs/docfx.json

Write-Host "==> Mirroring docs into site/docs for Jekyll"
if (Test-Path site/docs) { Remove-Item -Recurse -Force site/docs }
Copy-Item -Recurse artifacts/docs site/docs

Write-Host "==> Building Jekyll site (for local preview)"
Push-Location site
& bundle exec jekyll build | Out-Null
Pop-Location

Write-Host "==> Generating unified Pagefind index over site + docs"
& npx -y pagefind --site site/_site --output-path site/_site/pagefind | Out-Null

Write-Host "==> Done. Pagefind index at site/_site/pagefind/."

if ($Serve) {
    Write-Host "==> Starting Jekyll serve"
    Set-Location site
    & bundle exec jekyll serve --watch
}
