#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Runs the unit test suite with code coverage and writes a Cobertura report.

.DESCRIPTION
    The single entry point for coverage, used both locally and in CI so that the two
    can never disagree.

    Only tests/SiteCheck.Core.Tests is measured. The integration suite is left out by
    construction rather than by a filter: it is never started here, so it cannot
    contribute to the number, and nobody has to remember an exclude flag.

.PARAMETER MinimumLineCoverage
    Fails with a non-zero exit code below this line coverage. 70 by default; see
    docs/testing.md for why it is not higher. Pass 0 to report without enforcing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/coverage.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/coverage.ps1 -MinimumLineCoverage 0
#>

[CmdletBinding()]
param(
    [double] $MinimumLineCoverage = 70,
    [string] $OutputDirectory = 'TestResults'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$reportName = 'coverage.cobertura.xml'
$reportPath = Join-Path (Join-Path $repoRoot $OutputDirectory) $reportName

Push-Location $repoRoot
try {
    dotnet test (Join-Path 'tests' 'SiteCheck.Core.Tests') `
        --coverage `
        --coverage-output-format cobertura `
        --coverage-output $reportName `
        --results-directory $OutputDirectory

    if ($LASTEXITCODE -ne 0) {
        Write-Error "The unit tests failed. Coverage is not meaningful until they pass."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $reportPath)) {
    Write-Error "No coverage report at $reportPath. Did the coverage extension run?"
}

[xml] $report = Get-Content -Path $reportPath -Raw

$rows = $report.coverage.packages.package.classes.class |
    ForEach-Object {
        [pscustomobject]@{
            Type     = $_.name
            Coverage = [double] $_.'line-rate'
        }
    } |
    Sort-Object Coverage

Write-Host ''
Write-Host 'Line coverage by type (least covered first)'
foreach ($row in $rows) {
    Write-Host ('  {0,7:P1}  {1}' -f $row.Coverage, $row.Type)
}

$total = [double] $report.coverage.'line-rate'
$branches = [double] $report.coverage.'branch-rate'

Write-Host ''
Write-Host ('Lines    {0:P2}' -f $total)
Write-Host ('Branches {0:P2}' -f $branches)
Write-Host ("Report   $reportPath")

if ($MinimumLineCoverage -le 0) {
    Write-Host ''
    Write-Host 'No minimum enforced.'
    exit 0
}

$minimum = $MinimumLineCoverage / 100.0

if ($total -lt $minimum) {
    Write-Host ''
    Write-Host ('FAILED: line coverage {0:P2} is below the {1:P0} floor.' -f $total, $minimum)
    exit 1
}

Write-Host ''
Write-Host ('OK: line coverage {0:P2} is at or above the {1:P0} floor.' -f $total, $minimum)
exit 0
