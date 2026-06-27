#requires -Version 7
# Smoke test: build Bowire + the two sibling plugin repos
# (Bowire.Protocol.Dis, Bowire.Protocol.Udp), install them into a
# throw-away plugin directory via `bowire plugin install`, and verify
# that `bowire plugin list` + `bowire plugin inspect` both see them.
#
# Run from the Bowire repo root:
#   ./scripts/smoke-dis-udp.ps1
#
# Assumes the DIS and UDP plugin repos are checked out as siblings.
# Override with env vars DIS_REPO / UDP_REPO if needed.

$ErrorActionPreference = 'Stop'

$Root    = Resolve-Path (Join-Path $PSScriptRoot '..')
$DisRepo = if ($env:DIS_REPO) { $env:DIS_REPO } else { Join-Path $Root '..\Bowire.Protocol.Dis' }
$UdpRepo = if ($env:UDP_REPO) { $env:UDP_REPO } else { Join-Path $Root '..\Bowire.Protocol.Udp' }
$Version = if ($env:VERSION)  { $env:VERSION  } else { '0.9.4-smoke' }

if (-not (Test-Path $DisRepo)) { throw "DIS repo not found at $DisRepo (set DIS_REPO to override)" }
if (-not (Test-Path $UdpRepo)) { throw "UDP repo not found at $UdpRepo (set UDP_REPO to override)" }

$SmokeDir  = Join-Path $Root 'artifacts\smoke-plugins'
$SmokeFeed = Join-Path $Root 'artifacts\smoke-feed'

Write-Host "==> Packing Bowire ($Version)"
dotnet pack (Join-Path $Root 'Kuestenlogik.Bowire.slnx') -c Release -p:Version=$Version --nologo -v quiet

Write-Host "==> Packing Bowire.Protocol.Dis ($Version)"
dotnet pack (Join-Path $DisRepo 'Bowire.Protocol.Dis.slnx') -c Release -p:Version=$Version `
    -p:KL_Bowire_Version=$Version --nologo -v quiet

Write-Host "==> Packing Bowire.Protocol.Udp ($Version)"
dotnet pack (Join-Path $UdpRepo 'Bowire.Protocol.Udp.slnx') -c Release -p:Version=$Version `
    -p:KL_Bowire_Version=$Version --nologo -v quiet

Write-Host "==> Resetting $SmokeDir"
Remove-Item -Recurse -Force $SmokeDir  -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $SmokeFeed -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $SmokeDir  -Force | Out-Null
New-Item -ItemType Directory -Path $SmokeFeed -Force | Out-Null

Copy-Item (Join-Path $Root    'artifacts\packages\*.nupkg') $SmokeFeed -ErrorAction SilentlyContinue
Copy-Item (Join-Path $DisRepo 'artifacts\packages\*.nupkg') $SmokeFeed -ErrorAction SilentlyContinue
Copy-Item (Join-Path $UdpRepo 'artifacts\packages\*.nupkg') $SmokeFeed -ErrorAction SilentlyContinue

$Cli = Join-Path $Root 'src\Kuestenlogik.Bowire.Tool\Kuestenlogik.Bowire.Tool.csproj'
function Invoke-Bowire([string[]]$Args) {
    & dotnet run --project $Cli --no-build -- @Args
    if ($LASTEXITCODE -ne 0) { throw "bowire $($Args -join ' ') exited with $LASTEXITCODE" }
}

Write-Host '==> Installing DIS plugin'
Invoke-Bowire @('plugin','install','Kuestenlogik.Bowire.Protocol.Dis','--version',$Version,'--source',$SmokeFeed,'--source','https://api.nuget.org/v3/index.json','--plugin-dir',$SmokeDir)

Write-Host '==> Installing UDP plugin'
Invoke-Bowire @('plugin','install','Kuestenlogik.Bowire.Protocol.Udp','--version',$Version,'--source',$SmokeFeed,'--source','https://api.nuget.org/v3/index.json','--plugin-dir',$SmokeDir)

Write-Host '==> plugin list (verbose)'
Invoke-Bowire @('plugin','list','--verbose','--plugin-dir',$SmokeDir)

Write-Host '==> plugin inspect Kuestenlogik.Bowire.Protocol.Dis'
$disOut = & dotnet run --project $Cli --no-build -- plugin inspect Kuestenlogik.Bowire.Protocol.Dis --plugin-dir $SmokeDir
$disOut | Write-Host
if (-not ($disOut -match 'BowireDisProtocol')) {
    throw 'DIS plugin: BowireDisProtocol not found in inspect output'
}

Write-Host '==> plugin inspect Kuestenlogik.Bowire.Protocol.Udp'
$udpOut = & dotnet run --project $Cli --no-build -- plugin inspect Kuestenlogik.Bowire.Protocol.Udp --plugin-dir $SmokeDir
$udpOut | Write-Host
if (-not ($udpOut -match 'BowireUdpProtocol')) {
    throw 'UDP plugin: BowireUdpProtocol not found in inspect output'
}

# IBowireMockEmitter is the extension point that `bowire mock` picks
# up via PluginManager.EnumeratePluginServices. A regression in the
# plugin ALC walker would silently unwire DIS proactive replay -
# assert the type is visible in the inspect output so CI catches it.
if (-not ($disOut -match 'DisMockEmitter')) {
    throw 'DIS plugin: DisMockEmitter not found in inspect output - mock-emitter extension point broken'
}

Write-Host ''
Write-Host 'OK - both plugins installed, listed, and inspect-reported their IBowireProtocol types.'
Write-Host 'DIS plugin also exposes IBowireMockEmitter (DisMockEmitter) for bowire mock proactive replay.'
Write-Host "Plugin dir: $SmokeDir"
Write-Host "Local feed: $SmokeFeed"
