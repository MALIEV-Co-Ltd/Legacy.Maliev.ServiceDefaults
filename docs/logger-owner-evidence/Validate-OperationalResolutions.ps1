param([Parameter(Mandatory)][string]$CanonicalDefaultsRoot)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Validate-LoggerObligations.ps1" -CanonicalDefaultsRoot $CanonicalDefaultsRoot | Out-Null
$packet = Get-Content "$PSScriptRoot/operational-owner-resolutions.json" -Raw | ConvertFrom-Json
$paths = Get-Content "$PSScriptRoot/path-obligations.json" -Raw | ConvertFrom-Json
$expected = @($paths.assignments | Where-Object path -Match 'Maliev.LoggerService.NLog/' |
    ForEach-Object { "$($_.source)|$($_.path)|$($_.sourceBlob)" } | Sort-Object)
$actual = @($packet.records | ForEach-Object { "$($_.source)|$($_.path)|$($_.sourceBlob)" } | Sort-Object)
if ($actual.Count -ne 19 -or @(Compare-Object $expected $actual).Count) { throw 'Operational resolution membership differs.' }
foreach ($record in $packet.records) {
    if ($record.wholeSourceClosed -ne $false -or -not $record.nativeCases.Count) { throw 'Invalid bounded resolution.' }
    $assignment = @($paths.assignments | Where-Object { $_.source -eq $record.source -and $_.path -eq $record.path })
    $expectedCases = @($assignment[0].retainedEvidence | ForEach-Object { "$($_.testId)|$($_.testName)|$($_.outcome)" } | Sort-Object)
    $actualCases = @($record.nativeCases | ForEach-Object { "$($_.testId)|$($_.testName)|$($_.outcome)" } | Sort-Object)
    if (@(Compare-Object $expectedCases $actualCases).Count) { throw 'Resolution evidence differs.' }
    if ($record.acceptedMain -ne '7b3099bf67d0f17e56cfdb3dcf36541304abaac2' -or $record.nativeRun -ne '37569609354') {
        throw 'Resolution native authority differs.'
    }
}
if ($packet.ledgerChanged -ne $false -or $packet.wholeSourceClosure -ne $false) { throw 'Unexpected ledger or source closure.' }
[pscustomobject]@{ OperationalPortions = 19; ExactEvidence = 'Passed'; WholeSourceClosure = $false; LedgerChanged = $false }
