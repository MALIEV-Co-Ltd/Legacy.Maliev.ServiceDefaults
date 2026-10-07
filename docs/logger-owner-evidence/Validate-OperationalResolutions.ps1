param(
    [Parameter(Mandatory)][string]$CanonicalDefaultsRoot,
    [Parameter(Mandatory)][string]$HistoricalSourceRoot
)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Validate-LoggerObligations.ps1" -CanonicalDefaultsRoot $CanonicalDefaultsRoot -HistoricalSourceRoot $HistoricalSourceRoot | Out-Null
$packet = Get-Content "$PSScriptRoot/operational-owner-resolutions.json" -Raw | ConvertFrom-Json
$paths = Get-Content "$PSScriptRoot/path-obligations.json" -Raw | ConvertFrom-Json
$targets = Get-Content "$PSScriptRoot/target-bodies.json" -Raw | ConvertFrom-Json
$witnesses = Get-Content "$PSScriptRoot/operational-source-witnesses.json" -Raw | ConvertFrom-Json
$expected = @($paths.assignments | Where-Object path -Match 'Maliev.LoggerService.NLog/' |
    ForEach-Object { "$($_.source)|$($_.path)|$($_.sourceBlob)" } | Sort-Object)
$actual = @($packet.records | ForEach-Object { "$($_.source)|$($_.path)|$($_.sourceBlob)" } | Sort-Object)
if ($actual.Count -ne 19 -or @(Compare-Object $expected $actual).Count) { throw 'Operational resolution membership differs.' }
foreach ($record in $packet.records) {
    if ($record.wholeSourceClosed -ne $false -or -not $record.nativeCases.Count) { throw 'Invalid bounded resolution.' }
    $assignment = @($paths.assignments | Where-Object { $_.source -eq $record.source -and $_.path -eq $record.path })
    $witness = @($witnesses.witnesses | Where-Object { $_.source -eq $record.source -and $_.path -eq $record.path })
    if ($assignment.Count -ne 1 -or $witness.Count -ne 1) { throw 'Ambiguous resolution binding.' }
    if ($record.identity -ne $assignment[0].identity -or $record.sourceBodyRevision -ne $assignment[0].sourceBodyRevision) {
        throw 'Resolution identity or body revision differs.'
    }
    foreach ($binding in @(@('behaviorIds', $assignment[0].behaviorIds), @('sourceMethods', $witness[0].methods), @('sourceFields', $witness[0].structuredFailureFields))) {
        $field = $binding[0]
        if (($record.$field | ConvertTo-Json -Depth 6 -Compress) -ne ($binding[1] | ConvertTo-Json -Depth 6 -Compress)) { throw "Resolution $field differs." }
    }
    if (($record.targetBodies | ConvertTo-Json -Depth 6 -Compress) -ne ($targets | ConvertTo-Json -Depth 6 -Compress)) {
        throw 'Resolution target bodies differ.'
    }
    $expectedCases = @($assignment[0].retainedEvidence | ForEach-Object { "$($_.testId)|$($_.testName)|$($_.outcome)" } | Sort-Object)
    $actualCases = @($record.nativeCases | ForEach-Object { "$($_.testId)|$($_.testName)|$($_.outcome)" } | Sort-Object)
    if (@(Compare-Object $expectedCases $actualCases).Count) { throw 'Resolution evidence differs.' }
    if ($record.acceptedMain -ne '7b3099bf67d0f17e56cfdb3dcf36541304abaac2' -or $record.nativeRun -ne '37569609354') {
        throw 'Resolution native authority differs.'
    }
}
if ($packet.ledgerChanged -ne $false -or $packet.wholeSourceClosure -ne $false) { throw 'Unexpected ledger or source closure.' }
[pscustomobject]@{ OperationalPortions = 19; ExactEvidence = 'Passed'; WholeSourceClosure = $false; LedgerChanged = $false }
