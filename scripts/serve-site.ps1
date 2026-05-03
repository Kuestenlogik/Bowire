#requires -Version 7
# Local preview for the marketing site + docs. See scripts/serve-site.sh
# for the rationale — http-server matches GitHub Pages' URL semantics
# out of the box (bare directory URLs redirect to trailing-slash form,
# .html files serve directly), so relative asset paths resolve
# correctly on both.

$ErrorActionPreference = 'Stop'

$Port = if ($args.Count -gt 0) { $args[0] } else { '4000' }
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Site = Join-Path $Root 'site/_site'

if (-not (Test-Path $Site)) {
    throw "$Site does not exist. Run scripts/build-docs.ps1 first."
}

Write-Host "==> Serving $Site on http://localhost:$Port"
Write-Host "    Home:   http://localhost:$Port/"
Write-Host "    Docs:   http://localhost:$Port/docs/"
Write-Host ''
# -c-1 disables caching so CSS/JS edits reload without Ctrl+F5.
# --cors keeps Pagefind's fetch() happy on localhost.
& npx --yes http-server -p $Port -c-1 --cors $Site
