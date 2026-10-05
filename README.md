# Legacy.Maliev.ServiceDefaults

Compatibility-preserving .NET 10 hosting defaults for MALIEV's extracted legacy services.

The repository is public and independently versioned so legacy workloads do not depend on the new-platform `Maliev.Aspire` repository. The first compatibility release intentionally retains the existing `Maliev.Aspire.ServiceDefaults.*` C# namespaces while changing the repository, assembly, and package identity to `Legacy.Maliev.ServiceDefaults`. This avoids a single cross-organization breaking change across every legacy service.

The initial source snapshot was extracted from `MALIEV-Co-Ltd/Maliev.Aspire` commit
`01d506203763b914e237268a8746f1406423df86`. Subsequent changes belong in this
repository and must not be copied back implicitly.

## Included defaults

- health, readiness, liveness, Prometheus, OpenTelemetry, and URL-query redaction
- resilient HTTP clients and service discovery
- RS256 JWT authentication, permission authorization, IAM registration, and service-token exchange
- PostgreSQL, Redis caching, RabbitMQ/MassTransit, rate limits, CORS, and middleware
- ASP.NET Core OpenAPI with Scalar

### Native logging ownership after source `5ac7d045`

The original `maliev-web` commit `5ac7d045c51194edd9e64d8564f1b726b001be34`
removed its shared `Maliev.NativeLogging` project. Its applications then configured
the built-in JSON console logger and production exception handler locally. This
Legacy extraction deliberately uses one shared ServiceDefaults registration
instead: `AddServiceDefaults` configures the `maliev-cloud-json` console
formatter, and `UseStandardMiddleware` installs the exception handler. The old
NativeLogging and LoggerService assemblies are not package dependencies.

Source commit `cbac7d7155da2208c77d56103b6a2cb19196fc83` changed the
obsolete LoggerService API to obtain its symmetric JWT key from configuration.
Source commit `9e51e6c5da29de8e617b65b59d46882cde6d3b64` subsequently removed
that API, its deployment files, and its authentication startup entirely. There
is no LoggerService runtime to port or signing secret to provision. Legacy
services instead use the shared RS256-only production validator in this package;
the native logging and JWT contract tests guard both sides of that replacement.

The shared formatter retains UTC `O` timestamps, scopes, activity trace/span
correlation, and structured `LogLevel`/category/message fields. It additionally
emits Cloud Logging `severity` and records exception type without exception
message or stack text. Unlike the source's `ClearProviders` and minimum
Information setting, it preserves providers and filters already registered by
each host and permits a host to enable a more verbose level. These are
intentional compatibility and safety differences, not byte-for-byte JSON or
application-local configuration parity. The existing native JSON and wire tests
exercise the formatter and standard middleware; the source-`5ac7` contract test
checks that the retired package/API is absent. Each consuming application still
needs its own startup, package/container, and error-path validation before its
source-commit owner can be marked migrated (tracked separately by Workflows
[#143](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Workflows/issues/143)).

### Outbound write safety

The shared HTTP resilience policy retries transient failures for safe read
methods only. Automatic retries are disabled for `POST`, `PUT`, `PATCH`,
`DELETE`, and `CONNECT` so a lost response cannot repeat a legacy write. A
service that needs retryable writes must expose an idempotency contract and
own that retry at its application boundary; changing the shared policy is not
an acceptable substitute.

### Retained typed HTTP responses

`Maliev.Service.WebApi.WebApiResponseReader.ReadAsAsync<T>(response, cancellationToken)`
returns the retained `Maliev.Service.WebApi.Model.ApiResponse<T>` model. Successful
responses use the original `Microsoft.AspNet.WebApi.Client` 6.0 formatter; null,
empty and malformed content follow that formatter's behavior. Non-success
responses retain the type's default item and the original status, headers and
unread body. The caller owns response disposal on success, failure and exceptions.
The cancellation token is checked before reading and forwarded to the formatter.

This extracts the typed-response portion of the legacy `GetAs<T>`,
`GetExternalAs<T>` and `Post<T>` helpers. Transport, endpoint configuration,
credentials and token acquisition remain owned by the consuming application.
Each Intranet consumer requires its own integration validation before adoption.

## Local validation

```powershell
dotnet restore Legacy.Maliev.ServiceDefaults.slnx
dotnet build Legacy.Maliev.ServiceDefaults.slnx -c Release --no-restore
dotnet test Legacy.Maliev.ServiceDefaults.slnx -c Release --no-build --no-restore
dotnet pack src/Legacy.Maliev.ServiceDefaults/Legacy.Maliev.ServiceDefaults.csproj -c Release --no-build
```

Local builds resolve `Legacy.Maliev.CompatibilityContracts` from the sibling
MALIEV workspace. CI checks out the exact validated public commit. Packing this
project records `Legacy.Maliev.CompatibilityContracts` as a package dependency;
publishing remains a separate gated action.

## Operational boundary

This library creates no infrastructure. Consuming services remain constrained to the existing GKE cluster and `maliev-legacy` namespace. Database migrations, secrets, deployments, node pools, Cloud SQL, and paid resources are outside this repository.
