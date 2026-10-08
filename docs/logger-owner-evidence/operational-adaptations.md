# Operational Logger adapter dispositions

`operational-source-witnesses.json` binds all 19 operational adapter path
assignments to their committed source bodies. It records only public code-owned
method names and structured field names; source configuration values are excluded.

| Source behavior | Defaults producer and consumer evidence | Bounded disposition |
| --- | --- | --- |
| `ILoggerManager` / `LoggerManager` level methods and structured event overload | `ILogger`, `MalievCloudJsonConsoleFormatter`, retained actual console severity case and existing-provider delivery case | Replace the adapter with native logging; Trace/Debug map to DEBUG, Information to INFO, Warning to WARNING, Error to ERROR and Fatal/Critical to CRITICAL. Arbitrary plaintext message equivalence remains excluded. |
| `RequestFailureLogEvent.Create` eight metadata properties, query removal, exception object | `ExceptionHandlingMiddleware`, actual Production HTTP failure cases and private formatter/provenance cases | Preserve event, service, method, status, exception type, incident and UTC fields; use route templates and exclude exception objects/messages. Field casing follows the retained native contract rather than the obsolete NLog object. |
| `UseMalievProductionExceptionHandler` generic 500 / optional text response | Standard middleware HTTP fixture, mapped JSON response and started-response cases | Preserve target JSON/status contracts and already-started responses; generic optional text is explicitly superseded rather than claimed equivalent. |
| `nlog.config` provider configuration | Preserved providers/filters/OpenTelemetry test, scoped UTC JSON/activity test | Replace obsolete adapter configuration without importing SQL sink configuration or clearing existing providers. This is configuration supersession, not cloud ingestion proof. |
| Adapter project and generated XML documentation | Source9e51 project removal, source5ac intermediate assembly removal, retained no-retired-assembly/source API test | Superseded artifacts; operational producer behavior remains required in Defaults. |

The dependency-terminal evidence belongs to the NativeLogging successor chain.
It supports selected-client safe operation/status observation and once-only
failure recording; it does not fabricate a dependency API in older Logger source
blobs that contain none.

These are explicit owner-portion dispositions awaiting source-specific owner
acceptance. They never close mixed commits or their non-Defaults paths. Native
test observations are from the accepted main receipt, not a new execution.
