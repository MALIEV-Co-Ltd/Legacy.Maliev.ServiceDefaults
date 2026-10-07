param(
    [Parameter(Mandatory)][string]$CanonicalDefaultsRoot,
    [Parameter(Mandatory)][string]$HistoricalSourceRoot
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$membership = Get-Content "$root/historical-memberships.json" -Raw | ConvertFrom-Json
$packet = Get-Content "$root/path-obligations.json" -Raw | ConvertFrom-Json
$retained = Get-Content "$root/actual-retained-logging-tests.json" -Raw | ConvertFrom-Json
$behaviors = Get-Content "$root/behavior-evidence.json" -Raw | ConvertFrom-Json
$targets = Get-Content "$root/target-bodies.json" -Raw | ConvertFrom-Json
$repo = (Resolve-Path -LiteralPath $CanonicalDefaultsRoot).Path
$accepted = '7b3099bf67d0f17e56cfdb3dcf36541304abaac2'
$observedHead = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $observedHead -ne $accepted) { throw 'Defaults checkout is not the accepted commit.' }
foreach ($behavior in $behaviors) {
    if (-not $behavior.cases.Count) { throw 'Behavior lacks native supporting cases.' }
    foreach ($evidence in $behavior.cases) {
        $match = @($retained | Where-Object {
            $_.testId -eq $evidence.testId -and $_.testName -eq $evidence.testName -and $_.outcome -eq 'Passed'
        })
        if ($match.Count -ne 1 -or $evidence.outcome -ne 'Passed') { throw 'Behavior evidence is unverified.' }
    }
}
foreach ($target in $targets) {
    $observed = (Get-FileHash (Join-Path $repo $target.path) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($observed -ne $target.sha256) { throw "Target body drift: $($target.path)" }
}
if ($membership.Count -ne 19 -or $packet.assignments.Count -ne 119) { throw 'Source or assignment count changed.' }
$expected = @($membership | ForEach-Object {
    $source = $_.source
    $_.loggerPaths | ForEach-Object { "$source|$_" }
} | Sort-Object)
$actual = @($packet.assignments | ForEach-Object { "$($_.source)|$($_.path)" } | Sort-Object)
if (@(Compare-Object $expected $actual).Count) { throw 'Exact source/path memberships differ.' }
if (@($actual | Select-Object -Unique).Count -ne 119) { throw 'Duplicate source/path assignments.' }
foreach ($assignment in $packet.assignments) {
    if ($assignment.sourceBlob -notmatch '^[a-f0-9]{40}$') { throw 'Missing committed source blob.' }
    $resolvedBlob = git -C $HistoricalSourceRoot rev-parse --verify "$($assignment.sourceBodyRevision):$($assignment.path)" 2>$null
    if ($LASTEXITCODE -ne 0 -or $resolvedBlob -ne $assignment.sourceBlob) { throw 'Historical source body binding differs.' }
    $ancestry = git -C $HistoricalSourceRoot rev-list --parents -n 1 $assignment.source
    if ($LASTEXITCODE -ne 0 -or $assignment.sourceBodyRevision -notin ($ancestry -split ' ')) { throw 'Historical body revision is not the source or a direct parent.' }
    foreach ($id in $assignment.behaviorIds) {
        if (@($behaviors | Where-Object id -eq $id).Count -ne 1) { throw 'Unknown or ambiguous behavior assignment.' }
    }
    if ($assignment.wholeSourceClosed -ne $false) { throw 'This packet cannot close a source commit.' }
    foreach ($evidence in $assignment.retainedEvidence) {
        $match = @($retained | Where-Object {
            $_.testId -eq $evidence.testId -and $_.testName -eq $evidence.testName -and $_.outcome -eq 'Passed'
        })
        if ($match.Count -ne 1 -or $evidence.outcome -ne 'Passed') { throw 'Unverified or ambiguous retained evidence.' }
    }
}
if ($packet.wholeSourceClosure -ne $false -or $packet.ledgerChanged -ne $false) { throw 'Unexpected closure claim.' }
[pscustomobject]@{ Sources = 19; Assignments = 119; ExactMembership = 'Passed'; EvidenceReadback = 'Passed'; SourceClosure = $false }
