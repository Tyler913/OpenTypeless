# Runs the performance budgets (tests\TypelessCore.Tests\PerformanceTests.cs) in a Release build, like the app's, and
# prints each timing next to its budget. On GitHub Actions the table also goes to the run's summary page, and a check
# over its budget is flagged on the run page.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$report = [System.IO.Path]::GetTempFileName()
$status = 1
try {
    $env:OPENTYPELESS_PERF = '1'
    $env:OPENTYPELESS_PERF_REPORT = $report
    dotnet test tests\TypelessCore.Tests -c Release --nologo --filter 'FullyQualifiedName~PerformanceTests' @args
    $status = $LASTEXITCODE

    $rows = @(Get-Content $report | Where-Object { $_ } | ForEach-Object {
        $name, $ms, $budget = $_ -split "`t"
        [pscustomobject]@{ Name = $name; Ms = [double]::Parse($ms, $invariant); Budget = [double]::Parse($budget, $invariant) }
    })
    $table = @('| Check | Time | Budget | |', '|---|--:|--:|---|') + @($rows | ForEach-Object {
        '| {0} | {1} ms | {2} ms | {3} |' -f $_.Name, $_.Ms.ToString('0.0', $invariant), $_.Budget.ToString('0', $invariant),
            $(if ($_.Ms -le $_.Budget) { 'ok' } else { 'OVER BUDGET' })
    })
    Write-Host ''
    $table | ForEach-Object { Write-Host $_ }
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Value (@('### Windows performance budgets', '') + $table)
    }
    if ($env:GITHUB_ACTIONS) {
        $rows | Where-Object { $_.Ms -gt $_.Budget } | ForEach-Object {
            Write-Output ('::error title=Over its performance budget::{0}: {1} ms (budget {2} ms)' -f $_.Name,
                $_.Ms.ToString('0.0', $invariant), $_.Budget.ToString('0', $invariant))
        }
    }
}
finally {
    Remove-Item $report -ErrorAction SilentlyContinue
    Remove-Item Env:OPENTYPELESS_PERF, Env:OPENTYPELESS_PERF_REPORT -ErrorAction SilentlyContinue
}
exit $status
