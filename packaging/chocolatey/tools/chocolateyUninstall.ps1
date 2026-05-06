# chocolateyUninstall.ps1 for Bowire
#
# Reads the per-machine MSI registration via Bowire's softwareName
# match and runs `msiexec /x` against it. WiX's MajorUpgrade rule on
# the install side already handles version-to-version transitions
# during `choco upgrade`; this script is only invoked on
# `choco uninstall bowire`.

$ErrorActionPreference = 'Stop'

$packageArgs = @{
    packageName    = 'bowire'
    fileType       = 'msi'
    softwareName   = 'Bowire*'
    silentArgs     = '/qn /norestart'
    validExitCodes = @(0, 3010, 1641, 1605, 1614, 1641)
}

# Resolve the installed product code from Apps & Features and
# pass it to msiexec /x. Get-UninstallRegistryKey is provided by
# the Chocolatey helpers module that's auto-imported in install/
# uninstall scripts.
$key = Get-UninstallRegistryKey -SoftwareName $packageArgs.softwareName

if ($key.Count -eq 1) {
    $key | ForEach-Object {
        $packageArgs.silentArgs = "$($_.PSChildName) $($packageArgs.silentArgs)"
        $packageArgs.file       = ''
        Uninstall-ChocolateyPackage @packageArgs
    }
} elseif ($key.Count -eq 0) {
    Write-Warning "$($packageArgs.packageName) was not found via the Apps & Features registry. It may have been uninstalled outside chocolatey already; nothing to do."
} else {
    Write-Warning "Multiple installs of $($packageArgs.packageName) found. Skipping uninstall — remove them manually from Apps & Features."
    $key | ForEach-Object { Write-Warning "  - $($_.DisplayName) $($_.DisplayVersion)" }
}
