#!/usr/bin/env pwsh
# Pretty-print uncovered-line counts per source file from a merged
# Cobertura report. Sorted descending by absolute uncovered lines —
# the worst offenders surface first so the top-of-list files give
# the biggest coverage uplift per test.
param(
    [string]$Report = "artifacts/cov-combined/Cobertura.xml",
    [int]$Top = 60
)

[xml]$xml = Get-Content $Report
$rows = foreach ($pkg in $xml.coverage.packages.package) {
    foreach ($cls in $pkg.classes.class) {
        $lines = $cls.lines.line
        if (-not $lines) { continue }
        $total    = ($lines | Measure-Object).Count
        $covered  = ($lines | Where-Object { [int]$_.hits -gt 0 } | Measure-Object).Count
        $uncov    = $total - $covered
        if ($uncov -le 0) { continue }
        [PSCustomObject]@{
            Assembly  = $pkg.name
            File      = $cls.filename
            Total     = $total
            Covered   = $covered
            Uncovered = $uncov
            Pct       = if ($total -eq 0) { 0 } else { [math]::Round(100.0 * $covered / $total, 1) }
        }
    }
}

# Collapse rows for the same file (partial-class / nested-class entries):
$grouped = $rows | Group-Object File | ForEach-Object {
    [PSCustomObject]@{
        Assembly  = ($_.Group | Select-Object -First 1).Assembly
        File      = $_.Name
        Total     = ($_.Group | Measure-Object -Property Total -Sum).Sum
        Covered   = ($_.Group | Measure-Object -Property Covered -Sum).Sum
        Uncovered = ($_.Group | Measure-Object -Property Uncovered -Sum).Sum
    }
} | ForEach-Object {
    $_ | Add-Member -NotePropertyName Pct -NotePropertyValue ($(if ($_.Total -eq 0) { 0 } else { [math]::Round(100.0 * $_.Covered / $_.Total, 1) })) -PassThru
}

$grouped | Sort-Object Uncovered -Descending | Select-Object -First $Top | ForEach-Object {
    # Strip repo prefix so the file column is readable in 200-col output.
    $rel = $_.File -replace [regex]::Escape("$PWD\"), ''
    $rel = $rel -replace [regex]::Escape((Resolve-Path '.').Path + "\"), ''
    "{0,5}  {1,5:N1}%  {2,5}  {3,-44}  {4}" -f $_.Uncovered, $_.Pct, $_.Total, $_.Assembly, $rel
}
