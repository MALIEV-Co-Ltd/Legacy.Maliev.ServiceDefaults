# Legacy.Maliev.ServiceDefaults

This public repository is the compatibility-preserving .NET 10 extraction of the
legacy services' shared defaults from `Maliev.Aspire`.

## Non-negotiable boundaries

- Never add credentials, private keys, connection strings, production evidence, or secret-manager payloads.
- Preserve the public `Maliev.Aspire.ServiceDefaults.*` namespaces until every legacy consumer has migrated to this package.
- Keep repository, assembly, and package identity `Legacy.Maliev.ServiceDefaults` so it cannot be confused with the new-platform implementation.
- Changes to authentication, permissions, health endpoints, retries, caching, logging/redaction, messaging, database registration, or OpenAPI/Scalar require focused compatibility tests.
- Do not add Swagger/Swashbuckle, AutoMapper, FluentValidation, or FluentAssertions.
- This library must not create Kubernetes, database, secret, node-pool, Cloud SQL, or other billable resources.

## Required validation

```powershell
dotnet restore Legacy.Maliev.ServiceDefaults.slnx
dotnet build Legacy.Maliev.ServiceDefaults.slnx -c Release --no-restore
dotnet test Legacy.Maliev.ServiceDefaults.slnx -c Release --no-build --no-restore
dotnet format Legacy.Maliev.ServiceDefaults.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.ServiceDefaults.slnx package --vulnerable --include-transitive --no-restore
dotnet pack src/Legacy.Maliev.ServiceDefaults/Legacy.Maliev.ServiceDefaults.csproj -c Release --no-build
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```

Keep commits small and migrate consumers in separate repository-specific pull requests.
