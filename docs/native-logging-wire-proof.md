# Native logging wire proof

This test-only slice supports source `5ac7d045c51194edd9e64d8564f1b726b001be34`
against ServiceDefaults base `81830f4f0fdf25b655b09918f51c969a11eaed01`.
It does not complete the whole cross-repository source commit or test Web's
separate ordered incident/error pipeline.

`NativeLoggingWireTests` captures the real console output produced by
`AddServiceDefaults`, not just formatter options. It asserts built-in JSON
Critical level, category/message, UTC round-trip timestamp, actual W3C trace/span
and application scope fields. An existing provider must receive the same event,
and its configured category filter must still suppress Information messages.

The HTTP theory starts an in-memory TestServer in Production, registers the
actual standard middleware, and throws controlled exceptions for the 400 and
500 paths. It checks exactly one intended Critical event, all eight structured
failure fields, correlation scope/header, incident-to-response trace binding,
expected JSON error response and absence of synthetic query/header/cookie/
exception secrets in all captured output. No authentication or external-service
integration coverage is implied. The fixture clears the OTLP endpoint in its
in-memory configuration, so inherited exporter configuration cannot send data.

Console redirection is process-wide: the collection disables parallelization and
host disposal drains the logger queue before restoring Console.Out in finally.
The fixture never modifies production logging or middleware.

## Intentional architecture differences

- Configuration is shared through ServiceDefaults but executes inside each app;
  neither the retired remote LoggerService nor source NativeLogging is restored.
- Existing providers, filters and OpenTelemetry are preserved, unlike the source
  `ClearProviders` setup.
- Structured failure logs deliberately omit exception objects and messages to
  avoid secret disclosure. The source passed the exception object.
- Existing mapped JSON responses remain unchanged; source generic APIs used
  empty 500 responses and Email used generic text. This is not byte-for-byte
  response equivalence or a reason to replace target response contracts.

## Validation and limitation

Use the isolated dependency root containing CompatibilityContracts at
`13eefdb44cad42b46216bb0378af8c76e3672c2c`; do not compile canonical outputs:

```powershell
$env:MalievWorkspaceRoot='B:/maliev-legacy/worktrees/logging-proof-dependencies'
dotnet restore Legacy.Maliev.ServiceDefaults.slnx
dotnet build Legacy.Maliev.ServiceDefaults.slnx -c Release --no-restore
dotnet test Legacy.Maliev.ServiceDefaults.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~NativeLoggingWireTests
dotnet test Legacy.Maliev.ServiceDefaults.slnx -c Release --no-build --no-restore --collect:'XPlat Code Coverage'
dotnet format Legacy.Maliev.ServiceDefaults.slnx --verify-no-changes --no-restore
```

Observed 2026-09-07 after expanding real registration, authentication, cache,
database, messaging, endpoint and HTTP behavior coverage: Release builds had
zero warnings/errors and the full suite passed **242/242** with no skipped
tests. Coverage collection reports **2402/3000 owned ServiceDefaults lines
(80.07%)**, excluding generated `obj` sources and the separately checked-out
CompatibilityContracts dependency.

The expanded behavioral suite demonstrated and fixes two production defects:
the service-authentication base URL now enforces its documented origin-only
boundary, and snake-case conversion no longer produces a double underscore
when a conventional identifier already contains an underscore. Existing
logging-option and exception-unit tests remain intact.
