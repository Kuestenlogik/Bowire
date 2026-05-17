#!/usr/bin/env pwsh
# Print the uncovered line numbers for a specific source file out of a
# merged Cobertura report. Lets me see exactly which branches a test
# pass missed before writing the next test.
param(
    [Parameter(Mandatory=$true)][string]$File,
    [string]$Report = "artifacts/cov-combined/Cobertura.xml"
)

[xml]$xml = Get-Content $Report
$norm = ($File -replace '/', '\').ToLowerInvariant()
foreach ($pkg in $xml.coverage.packages.package) {
    foreach ($cls in $pkg.classes.class) {
        $name = $cls.filename
        if (-not $name) { continue }
        if (-not $name.ToLowerInvariant().EndsWith($norm)) { continue }
        $cls.lines.line | Where-Object { [int]$_.hits -eq 0 } | ForEach-Object {
            "{0,5}  branch={1,5}  {2}" -f $_.number, $_.branch, $cls.filename
        }
    }
}
