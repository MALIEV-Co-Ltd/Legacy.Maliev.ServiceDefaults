param(
    [string]$PackageDirectory = 'artifacts',
    [string]$EvidenceDirectory = 'artifacts/coverage-evidence'
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This package-consumer gate runs only in hosted validation.' }
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'Legacy.Maliev.ServiceDefaults.*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected one produced Defaults package.' }
$archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($entry.Count -ne 1) { throw 'Expected one produced package manifest.' }
    $reader = [System.IO.StreamReader]::new($entry[0].Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = $manifest.package.metadata.version
} finally { $archive.Dispose() }
if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'Invalid produced package version.' }

# Prepare the frozen source dependency explicitly; the Defaults CI build does not build this checkout.
$contractsProject = Join-Path $env:GITHUB_WORKSPACE '.dependencies/Legacy.Maliev.CompatibilityContracts/src/Legacy.Maliev.CompatibilityContracts/Legacy.Maliev.CompatibilityContracts.csproj'
& dotnet restore $contractsProject --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the frozen Contracts dependency.' }
$dependencyBuild = @(& dotnet build $contractsProject --configuration Release --no-restore 2>&1)
$dependencyBuildExit = $LASTEXITCODE
$dependencyBuild | ForEach-Object { Write-Host $_ }
if ($dependencyBuildExit -ne 0 -or ($dependencyBuild -join "`n") -notmatch '(?m)^\s*0 Warning\(s\)\s*$' -or ($dependencyBuild -join "`n") -notmatch '(?m)^\s*0 Error\(s\)\s*$') {
    throw 'Frozen Contracts dependency requires a zero-warning, zero-error build.'
}
& dotnet pack $contractsProject --configuration Release --no-build --no-restore --output $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the frozen Contracts dependency package.' }

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $tempRoot ('maliev-typed-package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Legacy.Maliev.ServiceDefaults" Version="PACKAGE_VERSION" />
  </ItemGroup>
</Project>
'@
    $project.Replace('PACKAGE_VERSION', $version) | Set-Content -LiteralPath (Join-Path $scratch 'Consumer.csproj')
    $program = @'
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Maliev.Service.WebApi;

using var client = new HttpClient(new CurrencyHandler());
using var response = await client.PostAsJsonAsync("https://package.invalid/currencies/", new Currency { Code = "THB" }, CancellationToken.None);
var model = await WebApiResponseReader.ReadAsAsync<List<Currency>>(response, CancellationToken.None);
if (!ReferenceEquals(response, model.Response) || model.Item.Count != 1 || model.Item[0].Code != "THB" || model.Item[0].Name != "บาท")
    throw new InvalidOperationException("Packed formatter success behavior failed.");
if (!(await response.Content.ReadAsStringAsync()).Contains("THB", StringComparison.Ordinal))
    throw new InvalidOperationException("Packed response ownership failed.");
using var failureContent = new ObservedContent();
using var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = failureContent };
var failed = await WebApiResponseReader.ReadAsAsync<List<Currency>>(failure);
if (!ReferenceEquals(failure, failed.Response) || failed.Item is not null || failureContent.Reads != 0 || failureContent.Disposed)
    throw new InvalidOperationException("Packed failure response was read or disposed.");
if (await failure.Content.ReadAsStringAsync() != "unparsed error") throw new InvalidOperationException("Packed failure body changed.");
Console.WriteLine("PASS: exact packed package; SDK JSON call compiled; original formatter executed; response ownership retained.");

public sealed class Currency { public string? Code { get; set; } public string? Name { get; set; } }
internal sealed class CurrencyHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/currencies/"
            || await request.Content!.ReadAsStringAsync(cancellationToken) != "{\"code\":\"THB\",\"name\":null}")
            throw new InvalidOperationException("SDK JSON wire behavior changed.");
        return new(HttpStatusCode.Created) { Content = new StringContent("[{\"code\":\"THB\",\"name\":\"บาท\"}]", Encoding.UTF8, "application/json") };
    }
}
internal sealed class ObservedContent : HttpContent
{
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    { Reads++; await stream.WriteAsync(Encoding.UTF8.GetBytes("unparsed error")); }
    protected override bool TryComputeLength(out long length) { length = 14; return true; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
'@
    $program | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs')
    $localSource = [System.Security.SecurityElement]::Escape($packageRoot)
    "<configuration><packageSources><clear/><add key='produced' value='$localSource'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources></configuration>" |
        Set-Content -LiteralPath (Join-Path $scratch 'NuGet.Config')
    $consumerProject = Join-Path $scratch 'Consumer.csproj'
    & dotnet restore $consumerProject --configfile (Join-Path $scratch 'NuGet.Config') --packages (Join-Path $scratch 'packages')
    if ($LASTEXITCODE -ne 0) { throw 'Packed consumer restore failed.' }
    $build = @(& dotnet build $consumerProject --configuration Release --no-restore 2>&1)
    $buildExit = $LASTEXITCODE
    $build | ForEach-Object { Write-Host $_ }
    if ($buildExit -ne 0 -or ($build -join "`n") -notmatch '(?m)^\s*0 Warning\(s\)\s*$' -or ($build -join "`n") -notmatch '(?m)^\s*0 Error\(s\)\s*$') {
        throw 'Packed consumer requires a zero-warning, zero-error build.'
    }
    & dotnet run --project $consumerProject --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Packed consumer runtime proof failed.' }

    $assets = Get-Content -LiteralPath (Join-Path $scratch 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $target = @($assets.targets.Keys | Where-Object { $_ -notlike '*/*' })
    if ($target.Count -ne 1) { throw 'Expected one consumer target framework.' }
    $formatter = $assets.targets[$target[0]]['Microsoft.AspNet.WebApi.Client/6.0.0']
    if ($null -eq $formatter) { throw 'Packed formatter runtime dependency was not restored.' }
    $compile = @($formatter.compile.Keys | Where-Object { $_ -ne '_._' -and $_ -notlike '*/_._' })
    $runtime = @($formatter.runtime.Keys | Where-Object { $_ -ne '_._' -and $_ -notlike '*/_._' })
    if ($compile.Count -ne 0 -or $runtime.Count -eq 0) { throw 'Packed formatter asset selection violates compile/runtime isolation.' }
    $actualHash = [Convert]::ToBase64String([System.Security.Cryptography.SHA512]::HashData([System.IO.File]::ReadAllBytes($packages[0].FullName)))
    if ($assets.libraries["Legacy.Maliev.ServiceDefaults/$version"].sha512 -ne $actualHash) { throw 'Consumer did not restore the exact produced package.' }
    New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
    @{ package = $packages[0].Name; version = $version; packageSha512 = $actualHash; projectReference = $false; dependencyBuildWarnings = 0; dependencyBuildErrors = 0; buildWarnings = 0; buildErrors = 0; runtime = 'pass'; formatterCompileAssets = $compile; formatterRuntimeAssets = $runtime } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'packed-consumer.json')
} finally {
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ([System.IO.Path]::GetDirectoryName($resolvedScratch) -ne $tempRoot -or [System.IO.Path]::GetFileName($resolvedScratch) -notmatch '^maliev-typed-package-[a-f0-9]{32}$') {
        throw 'Refusing cleanup outside the owned temporary package consumer.'
    }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
exit 0
