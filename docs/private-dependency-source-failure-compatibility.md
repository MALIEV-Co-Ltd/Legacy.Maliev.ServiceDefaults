# Outbound failure predicate compatibility

The original helper at source `3d6506285a58671651d046e97a35fbb8885cea4f`
records every non-success HTTP response and otherwise unexpected transport
exception. The existing selected ServiceDefaults observer records 5xx responses
and selected exception classes. For example, a terminal 409 or an
`InvalidOperationException` from a transport currently produces no dependency
failure event.

`AddPrivateFailureSourceObservation` explicitly selects the broader failure
predicate for one named or typed client. It uses the existing bounded operation
taxonomy and configured dependency identity. The existing generic and
operation-only APIs retain their predicates. Repeated identical selection is
idempotent; changing a selected client's dependency or mode fails registration.

The observer remains outside the client handler/retry chain. It returns the same
response, rethrows the same exception, and uses the existing request-scoped
exactly-once observation state. Original caller cancellation remains quiet.
Logging failures do not replace transport outcomes. The existing safe event
fields and formatter remain unchanged; caught exception objects, host/path,
query, headers, body and exception data are not exported.

This is partial source compatibility. Historical method/type metadata and
independently validated consumer adoption remain separate obligations. No
source-commit disposition or consumer activation is changed by this producer PR.

## Validation status

The 45 authored focused cases cover named/typed 3xx/4xx/5xx responses, success,
unexpected exception identity, existing-mode and unselected-client controls,
caller cancellation, terminal-only retry observation, throwing logger providers,
idempotency, registration conflicts, and actual private formatter JSON.
Their compiled/executed count is unknown until hosted validation runs.

Local Release build, focused/full C# suites, formatting, package audit/pack,
security scan and raw coverage are **not run**. The fresh memory admission
observation was 4,187,668 KiB, below the unchanged 4,194,304 KiB floor; no qualified
finite local SDK allocation was granted. No local SDK worker was started.
The owner-approved migration exception permits a reviewed draft before these
checks, and waives none of them.

Before protected merge, the exact proposed head must pass Release with zero
warnings/errors, the focused cases, the full affected suite, formatting,
static/security/package checks and the existing unexcluded owned coverage gate
at its unchanged threshold. After merge, exact protected-main checks are also
required. Draft publication is not accepted migration completion.

## Remaining consumer adoption

| Consumer | Integration owner | Required next evidence |
| --- | --- | --- |
| Web `CountryClient` and its HTTP registration | Web owner coordinated by Root | Reviewed immutable producer pin; actual selected-client event/formatter tests; existing auth, retries and cancellation preserved |
| Intranet BFF `OrdersProxy` and its HTTP registration | Intranet/Orders consumer owner coordinated by Root | Actual terminal response/exception and retry recovery; original 10-second deadline and service authentication preserved |
| Intranet `LegacyCatalogClient` and its registration | Intranet/Catalog consumer owner coordinated by Root | Actual adapter outcomes and safe events with existing dependency ownership, auth and cancellation preserved |

Each consumer needs a separate reviewed change and required exact-head/main
validation. Related ServiceDefaults issues #62 and #71 remain open; producer
acceptance alone does not close their mixed producer/consumer obligations.
