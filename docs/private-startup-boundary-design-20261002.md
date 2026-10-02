# Explicit private startup boundary — implementation and validation

## Independent root acceptance before PR

Root read the complete implementation and nine unchanged regression cases, independently rebuilt the direct test project in Release with `--no-restore -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false`: zero warnings/errors and all linked outputs Release. Root unfiltered `dotnet test` with `--no-build --no-restore` passed334/334 with zero skips; `temp/startup-validation-20261002/root/root-full.trx` SHA256 `9FEC4E12635C08BDEE8CCC9091CF2CE0E18C18B84A5463255442B6A525647E14`. Scoped full-content source/test/doc gitleaks and whitespace checks passed. Required exact-head CI and post-main acceptance remain pending. Parent issue57 remains OPEN; consumer activation, child-process exit acceptance and the other source observability paths are excluded.

ServiceDefaults issue57; source `a59193ae2ac030d0e1d373ce4ec50c1b35437391`, parent `8175e8f3383d31030de2321c480bf6f5bc0fbb2b`. Committed producer `tools/diagnostics/ProductionStartupBoundary.cs` supplies sync/async wrappers, critical5102 StartupFailure/HostInitialization and failing process ExitCode1 without rethrow into runtime plaintext output. `tools/diagnostics/tests/ProductionDiagnosticsTests.cs` independently asserts quiet success, sync/async failure and safe output; README describes pre-host startup ownership. Shared formatter lives in `ProductionDiagnostics.cs`; health/probe behavior lives separately in `ProductionObservabilityMiddleware.cs`. Original objects read-only.

Owned workspace is now on `codex/private-startup-boundary-20261002`, based on accepted main `13e6a64ebc51f358f9406af7069a2ea7ec558cbc`. Before the branch switch, independently verified both original PR60 HEAD `ec26ba460ee1b6ecf468e478be584a788c28e2d6` and main resolved to tree `6cd3a54d7e6c0de2a5345280c62244d117d448d4`, with no tracked or staged changes and exactly the three owned NEW files preserved. No merge or edit of committed PR60 files. Ledger source/Defaults remains partial; no source completion or consumer activation is inferred.

Exactly three NEW files are currently owned: this document, `tests/Legacy.Maliev.ServiceDefaults.Tests/PrivateStartupBoundaryContractTests.cs`, and `src/Legacy.Maliev.ServiceDefaults/Diagnostics/PrivateStartupBoundary.cs`. Initially the explicitly authorized compile-only scaffold called the delegate unchanged, did not catch, and left ReportFailure empty. After root reviewed the observed initial RED, minimal behavior implementation was authorized within these three files only. The implementation catches startup failure, isolates reporting, and sets ExitCode1 in finally; it never registers providers or activates a consumer.

## Proposed signatures and semantics

Public static class `Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateStartupBoundary`:

```csharp
void Run(Action startHost, ILogger? failureLogger = null, TextWriter? privateOutput = null);
Task RunAsync(Func<Task> startHost, ILogger? failureLogger = null, TextWriter? privateOutput = null);
void ReportFailure(Exception exception, ILogger? failureLogger = null, TextWriter? privateOutput = null);
```

Calls are explicit, never AddServiceDefaults registration. Execute delegate exactly once, no retries. Successful completion emits nothing and preserves ExitCode. Failure sets Environment.ExitCode1 even if diagnostic output fails; no Environment.Exit, replacement exception or plaintext fallback. Caller owns normal host logs: use once around the entry-point boundary, not in addition to an existing consumer catch/report wrapper. No target consumer changed here.

When a logger is supplied it takes precedence: one critical event5102/StartupFailure, only fixed EventName=StartupFailure and Operation=HostInitialization, Exception=null. Never original exception/message/stack/inner/Data/state/scopes to that logger or its arbitrary providers. No exception-type string expansion of the formatter allowlist. When no logger is supplied, privateOutput (or Console.Error if absent) is the only isolated output: render through the accepted PrivateFailureConsoleFormatter directly, with original exception available solely to that formatter for its safe runtime type. Do not create/register a global logger/provider/OTLP pipeline. Writer/logger failures are contained only within the diagnostic emit; startup delegate failure is neither retried nor replaced. ExitCode remains1.

## Independent initial oracles

Nine cases: sync/async quiet success and once-only delegate; sync/async supplied-provider fixed metadata with no original exception; sync/async direct private JSON output with native CRITICAL/5102/type and no private sentinels; throwing logger and throwing writer preserve exit1 and do not trigger Console.Error plaintext fallback; explicit ReportFailure emits safe output. Existing nonparallel Native console output collection serializes process-wide Console.Error/ExitCode, both restored in finally. No process termination, network, containers or real host invocation in this initial slice.

Root corrected the initial missing-type design before execution: all tests call the public API directly, with no reflection adapter. The initial compile-only scaffold preserved unchanged delegate behavior so quiet controls could pass. Failing Run calls use Record.ExceptionAsync and Assert.Null to make the original escaped startup error a reached assertion RED, not an uncaught fixture error. Every failure branch independently sets ExitCode37 and restores it in finally; explicit reporting must change it to1. Root read all three files and released the exclusive .NET slot before the initial run below. Sink/provider doubles inspect the boundary's emitted wire; no global logging/privacy claim. The initial scaffold was not committed or treated as working startup protection.

## Observed initial RED — 2026-10-02

Sequential commands in the owned workspace:

```powershell
dotnet restore Legacy.Maliev.ServiceDefaults.slnx
dotnet build Legacy.Maliev.ServiceDefaults.slnx -c Release --no-restore
dotnet test tests/Legacy.Maliev.ServiceDefaults.Tests/Legacy.Maliev.ServiceDefaults.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~PrivateStartupBoundaryContractTests --logger 'console;verbosity=normal'
```

Restore exit0. Fresh Release build exit0, 0 warnings and 0 errors. The existing linked CompatibilityContracts project reported its Debug output; Defaults and its test assembly reported Release outputs. Focus exit1: total9, passed2, failed7. Both sync/async quiet-success controls passed. Six wrapper failure cases reached Assert.Null and failed because the original InvalidOperationException escaped unchanged (supplied logger sync/async, isolated output sync/async, throwing logger, throwing writer). Explicit reporting reached Assert.Equal and failed with expected ExitCode1 versus actual37. These are genuine reached behavioral assertion REDs against the compile-only scaffold, not missing-type, discovery, or build failures. Later metadata/output assertions are not yet established by this run. No full suite, implementation, commit, provider activation, consumer change, or external action; frozen for root review.

## Observed GREEN and validation — 2026-10-02

After root reviewed all three files and the 2PASS/7assertionRED evidence, minimal implementation was authorized. Tests were unchanged from RED. Sequential commands:

```powershell
dotnet build tests/Legacy.Maliev.ServiceDefaults.Tests/Legacy.Maliev.ServiceDefaults.Tests.csproj -c Release --no-restore
dotnet test tests/Legacy.Maliev.ServiceDefaults.Tests/Legacy.Maliev.ServiceDefaults.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~PrivateStartupBoundaryContractTests --logger 'trx;LogFileName=startup-green.trx' --results-directory temp/startup-validation-20261002
dotnet test tests/Legacy.Maliev.ServiceDefaults.Tests/Legacy.Maliev.ServiceDefaults.Tests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=defaults-full.trx' --results-directory temp/startup-validation-20261002
dotnet format Legacy.Maliev.ServiceDefaults.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.ServiceDefaults.slnx package --vulnerable --include-transitive --no-restore
dotnet pack src/Legacy.Maliev.ServiceDefaults/Legacy.Maliev.ServiceDefaults.csproj -c Release --no-build --no-restore -o temp/startup-validation-20261002/packages
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
gitleaks dir src/Legacy.Maliev.ServiceDefaults/Diagnostics/PrivateStartupBoundary.cs --redact=100 --exit-code 1 --no-banner --no-color
gitleaks dir tests/Legacy.Maliev.ServiceDefaults.Tests/PrivateStartupBoundaryContractTests.cs --redact=100 --exit-code 1 --no-banner --no-color
```

Direct-project build exit0, 0 warnings/errors; CompatibilityContracts, Defaults and tests all reported Release outputs. Focus exit0:9/9 passed,0 failed/skipped. Full Defaults exit0:334/334 passed,0 failed/skipped. XML TRX readback confirmed total/executed/passed9 and334 respectively, all failed/error/timeout/aborted/notExecuted counters0. Whole format exit0/no output. Audit exit0: neither Defaults nor tests had vulnerable packages in current nuget.org sources. Private pack exit0; ZIP metadata readback confirmed `lib/net10.0/Legacy.Maliev.ServiceDefaults.dll` and existing CompatibilityContracts1.0.0 dependency. This local package contains uncommitted reviewed code; its repository commit metadata is the base13e6a64, not a release claim. Gitleaks scanned54 commits with no leaks; each owned source/test scan found no leaks. Scoped search found no Environment.Exit, registration, Console plaintext write, OTLP or retry paths. Build output remains ignored; generated TRX/package under temp are untracked evidence retained for root review, never staged.

SHA256 evidence under `temp/startup-validation-20261002`:

- `startup-green.trx`:33084BF6D70B6B5837A5C91D7EB2167E2249D895F5274A8E7CFA00F472214D5A
- `defaults-full.trx`:09EF3DE5D6F737ECB69E3C004F3A04DCADA0911841548D6E48E3DD3B63BE9681
- `packages/Legacy.Maliev.ServiceDefaults.1.0.0.nupkg`:5264325D2A3606CCA5E26FADF4017BE0A0F37A570230D26CBD9BEE69C12A0BF5

Next step is root diff/evidence review; no commit or push authorized. Child-process exit and real consumer duplicate-boundary avoidance require separate consumer acceptance; health/request observer dedup, diagnostic route, service provenance/Activity, deployments/Cloud ingestion and whole source ownership remain excluded.
