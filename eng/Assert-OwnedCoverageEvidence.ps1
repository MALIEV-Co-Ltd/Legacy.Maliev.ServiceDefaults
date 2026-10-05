param(
    [Parameter(Mandatory = $true)] [string] $CoveragePath,
    [Parameter(Mandatory = $true)] [string] $ResultsDirectory
)
$ErrorActionPreference = 'Stop'
[xml] $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'coverage.runsettings') -Raw
$configuration = $settings.RunSettings.DataCollectionRunSettings.DataCollectors.DataCollector.Configuration
foreach ($name in @('Exclude', 'ExcludeByAttribute', 'ExcludeByFile')) {
    if (-not [string]::IsNullOrWhiteSpace([string] $configuration.$name)) { throw 'Raw evidence cannot use exclusions.' }
}
if ([string] $configuration.Include -cne '[Legacy.Maliev.ServiceDefaults]*' -or
    [string] $configuration.SkipAutoProps -ne 'false' -or [string] $configuration.IncludeTestAssembly -ne 'false') {
    throw 'Expected unexcluded owned production assembly evidence.'
}
[xml] $coverage = Get-Content -LiteralPath $CoveragePath -Raw
$packages = @($coverage.coverage.packages.package)
if ($packages.Count -ne 1 -or $packages[0].name -cne 'Legacy.Maliev.ServiceDefaults') {
    throw 'Expected exactly one owned production assembly.'
}
$lines = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
foreach ($class in $packages[0].classes.class) {
    $path = ([string] $class.filename).Replace('\', '/')
    foreach ($line in $class.SelectNodes('lines/line')) {
        $number = [long]::Parse([string] $line.number, [Globalization.CultureInfo]::InvariantCulture)
        $hits = [long]::Parse([string] $line.hits, [Globalization.CultureInfo]::InvariantCulture)
        if ($number -le 0 -or $hits -lt 0) { throw 'Invalid physical-line coverage data.' }
        $key = $path + [char] 0 + $number.ToString([Globalization.CultureInfo]::InvariantCulture)
        $previous = $false
        $null = $lines.TryGetValue($key, [ref] $previous)
        $lines[$key] = $previous -or $hits -gt 0
    }
}
if ($lines.Count -eq 0) { throw 'No measured owned production lines.' }
$covered = @($lines.Values | Where-Object { $_ }).Count
$trxFiles = if (Test-Path -LiteralPath $ResultsDirectory) {
    @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter '*.trx')
} else { @() }
if ($trxFiles.Count -ne 1) { throw 'Expected exactly one complete full-suite TRX.' }
[xml] $trx = Get-Content -LiteralPath $trxFiles[0].FullName -Raw
$results = @($trx.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
if ($results.Count -eq 0) { throw 'Full-suite TRX contains no results.' }
if (@($results | Where-Object outcome -ne 'Passed').Count -gt 0) { throw 'Full-suite TRX contains unsuccessful outcomes.' }
$counters = $trx.TestRun.ResultSummary.Counters
if ($null -eq $counters) { throw 'Full-suite TRX counters do not match results.' }
foreach ($required in @('total', 'executed', 'passed')) {
    if (-not $counters.HasAttribute($required)) { throw 'Full-suite TRX counters do not match results.' }
}
foreach ($counter in $counters.Attributes) {
    $expected = if ($counter.Name -in @('total', 'executed', 'passed')) { $results.Count } else { 0 }
    $actual = [long]::Parse($counter.Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($actual -ne $expected) { throw 'Full-suite TRX counters do not match results.' }
}
[pscustomobject]@{ Assembly = 'Legacy.Maliev.ServiceDefaults'; Covered = $covered; Total = $lines.Count;
    Percent = [decimal] 100 * $covered / $lines.Count; PassedTests = $results.Count } | ConvertTo-Json
