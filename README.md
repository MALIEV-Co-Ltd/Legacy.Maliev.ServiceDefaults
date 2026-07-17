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

## Local validation

```powershell
dotnet restore Legacy.Maliev.ServiceDefaults.slnx
dotnet build Legacy.Maliev.ServiceDefaults.slnx -c Release --no-restore
dotnet test Legacy.Maliev.ServiceDefaults.slnx -c Release --no-build --no-restore
dotnet pack src/Legacy.Maliev.ServiceDefaults/Legacy.Maliev.ServiceDefaults.csproj -c Release --no-build
```

Local builds resolve `Maliev.MessagingContracts` from the sibling MALIEV workspace. CI resolves the same versioned contract from the organization package registry.

## Operational boundary

This library creates no infrastructure. Consuming services remain constrained to the existing GKE cluster and `maliev-legacy` namespace. Database migrations, secrets, deployments, node pools, Cloud SQL, and paid resources are outside this repository.
