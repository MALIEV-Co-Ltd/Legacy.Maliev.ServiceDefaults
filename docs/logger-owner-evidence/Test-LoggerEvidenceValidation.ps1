param(
    [Parameter(Mandatory)][string]$CanonicalDefaultsRoot,
    [Parameter(Mandatory)][string]$HistoricalSourceRoot,
    [Parameter(Mandatory)][string]$TestOutputRoot
)
$ErrorActionPreference = 'Stop'
$outputRoot = [System.IO.Path]::GetFullPath($TestOutputRoot)
[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$results = @()
$expectedReasons = @{
    sourceBlob = 'Historical source body binding differs.'
    identity = 'Resolution identity or body revision differs.'
    behaviorIds = 'Resolution behaviorIds differs.'
    sourceMethods = 'Resolution sourceMethods differs.'
    sourceFields = 'Resolution sourceFields differs.'
    targetBodies = 'Resolution target bodies differ.'
}
$positiveCopyPassed = $false
foreach ($control in @('sourceBlob', 'identity', 'behaviorIds', 'sourceMethods', 'sourceFields', 'targetBodies')) {
    $temporary = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($temporary) | Out-Null
    $copied = @()
    try {
        foreach ($file in Get-ChildItem $PSScriptRoot -File) {
            $target = Join-Path $temporary $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $copied += $target
        }
        if (-not $positiveCopyPassed) {
            & "$temporary/Validate-OperationalResolutions.ps1" -CanonicalDefaultsRoot $CanonicalDefaultsRoot -HistoricalSourceRoot $HistoricalSourceRoot | Out-Null
            $positiveCopyPassed = $true
        }
        if ($control -eq 'sourceBlob') {
            $path = Join-Path $temporary 'path-obligations.json'
            $packet = Get-Content $path -Raw | ConvertFrom-Json
            $packet.assignments[0].sourceBlob = '0000000000000000000000000000000000000000'
        }
        else {
            $path = Join-Path $temporary 'operational-owner-resolutions.json'
            $packet = Get-Content $path -Raw | ConvertFrom-Json
            switch ($control) {
                'identity' { $packet.records[0].identity = 'corrupted-identity' }
                'behaviorIds' { $packet.records[0].behaviorIds = @('corrupted-behavior') }
                'sourceMethods' { $packet.records[0].sourceMethods = @('CorruptedMethod') }
                'sourceFields' { $packet.records[0].sourceFields = @('CorruptedField') }
                'targetBodies' { $packet.records[0].targetBodies[0].sha256 = 'corrupted-body-hash' }
            }
        }
        $packet | ConvertTo-Json -Depth 14 | Set-Content $path -Encoding utf8
        $rejected = $false
        $actualReason = $null
        try {
            & "$temporary/Validate-OperationalResolutions.ps1" -CanonicalDefaultsRoot $CanonicalDefaultsRoot -HistoricalSourceRoot $HistoricalSourceRoot | Out-Null
        }
        catch {
            $actualReason = $_.Exception.Message
            if ($actualReason -ne $expectedReasons[$control]) { throw "Unexpected validation failure for control: $control" }
            $rejected = $true
        }
        if (-not $rejected) { throw "Corruption control accepted: $control" }
        $results += [pscustomobject]@{ Control = $control; Rejected = $true; Reason = $actualReason; PositiveCopyPassed = $positiveCopyPassed }
    }
    finally {
        foreach ($file in $copied) { Remove-Item -LiteralPath $file -Force }
        Remove-Item -LiteralPath $temporary
        if (Test-Path -LiteralPath $temporary) { throw 'Disposable control directory remains.' }
    }
}
$results | ConvertTo-Json | Set-Content (Join-Path $outputRoot 'corruption-controls.json') -Encoding utf8
$results
