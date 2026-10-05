param([Parameter(Mandatory = $true)] [string] $CoveragePath)
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
$lines = @{}
foreach ($class in $packages[0].classes.class) {
    foreach ($line in $class.SelectNodes('lines/line')) {
        $key = "$($class.filename):$($line.number)"
        $lines[$key] = ([long] $line.hits -gt 0) -or ($lines[$key] -eq $true)
    }
}
if ($lines.Count -eq 0) { throw 'No measured owned production lines.' }
$covered = @($lines.Values | Where-Object { $_ }).Count
[pscustomobject]@{ Assembly = 'Legacy.Maliev.ServiceDefaults'; Covered = $covered; Total = $lines.Count;
    Percent = [decimal] 100 * $covered / $lines.Count } | ConvertTo-Json
