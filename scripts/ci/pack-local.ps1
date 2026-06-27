$Version = if ($args[0]) { $args[0] } else { "0.9.4-local" }
# PackageOutputPath in Directory.Build.props points at artifacts/packages,
# so a plain `dotnet pack` writes there directly. No -o override needed.
$Packages = "$PSScriptRoot\..\artifacts\packages"

Write-Host "Packing Kuestenlogik.Bowire v$Version..." -ForegroundColor Cyan

dotnet pack Kuestenlogik.Bowire.slnx -c Release -p:Version=$Version

Write-Host ""
Write-Host "Package published to: $Packages" -ForegroundColor Green
Write-Host ""
Write-Host "To use in other projects, add this to nuget.config:" -ForegroundColor Yellow
Write-Host "  <add key=`"local`" value=`"$((Resolve-Path $Packages).Path)`" />"
