# Outbound source metadata adaptation (source review only)

Source witnesses are the complete original commits
`3d6506285a58671651d046e97a35fbb8885cea4f` and
`9e51e6c5da29de8e617b65b59d46882cde6d3b64`. The former's
`Maliev.Service.WebApi/DependencyFailureLogEvent.cs` emits `method` and
`exceptionType`; the latter replaces it with `DependencyFailureDetails.cs` and
the native `WebApiService.LogDependencyFailure` structured fields `Method` and
`ExceptionType`. Both read `request.Method.Method` and `exception.GetType().Name`.
They do not derive the standalone method from an operation label.

Accepted producer PR #84 at `4ffba0639620e7e923d331317f3f28501526b573` restores
the selected outbound handler's non-success/generic exception predicate. It
does not restore these metadata fields or close the whole historical source.
This proposal remains part of open mixed obligations #62 and #71.

## Explicit privacy taxonomy proposed for review

Only `AddPrivateFailureSourceObservation` adds these fields. Generic and
operation-only registrations retain their existing state. The normal host
formatter is `MalievCloudJsonConsoleFormatter`, selected by `AddServiceDefaults`;
it emits the producer's `State.Method` and `State.ExceptionType` with a null
`Exception` object. The private formatter emits separate `Method` and
`ExceptionType` fields; its existing lower-case `exceptionType` runtime
provenance remains unchanged. HTTP outcomes carry a null structured exception
type, omitted by the private formatter, and retain a standalone method.

| Original value | Proposed bounded value |
| --- | --- |
| Exact standard method GET, HEAD, POST, PUT, DELETE, CONNECT, OPTIONS, TRACE, PATCH | Same uppercase method |
| Other method token | `UnknownMethod` |
| Exact runtime HttpRequestException, TimeoutException, OperationCanceledException, TaskCanceledException, Polly.Timeout.TimeoutRejectedException, IOException, InvalidOperationException, ArgumentException, NotSupportedException | Same simple type name |
| Any custom or derived exception type | `UnknownException` |

Custom type names can encode application or customer information; identifier
syntax alone is not permission to export them. Exact runtime type equality,
including rejection of an IOException subclass, prevents that leak. This is
an explicit adaptation requiring source review, not a claim of arbitrary
exception-type parity. Operation remains an independent finite resource/verb
taxonomy, including `UnknownOperation` while a standard standalone method is
still retained.

Caught exceptions never reach ILogger; messages, inner exceptions, stack,
Data, arbitrary type names, URI/host/path/query, request/response content and
authorization fields do not enter this event. The provider may fail without
replacing a response or thrown exception. Caller cancellation remains quiet.
The outer observer still surrounds authentication and resilience. A recovered
retry is quiet and only the terminal outcome contributes one event. The
existing rendezvous binds the known `TaskCanceledException` metadata only for
the outer native deadline wrapper it already proves; cancellation callbacks
do not log independently.

## Validation and remaining obligations

59 named/typed factory and normal-host formatter cases are authored, NOT RUN.
They cover standalone methods on unknown resources, exact known and custom
exception types, both formatter schemas, unchanged old/unselected modes,
caller cancellation, proven native timeout, terminal retry ownership, and
provider failure preserving exception identity. The hosted workflow proposal
requires all 59 results/counters to pass after the strict build, alongside the
existing 45 predicate cases and unchanged full/static/security/package/raw
coverage gates. No exclusions or thresholds change.

Local Release build, focused/full tests, format, audit, pack and coverage are
NOT RUN. Fresh memory observed 8,146,228 KiB, but the qualified finite SDK slot
is exclusively allocated to the Document owner; this producer has no SDK
allocation. No SDK, native test host, container or browser is launched here.
Static diff, workflow syntax and source checks cannot establish C# compilation
or runtime correctness. Publication and protected acceptance remain pending
review and the exact proposed-head/protected-main required checks.

Web CountryClient and Intranet Catalog/Orders adoption are independent owner
slices and are not edited by this proposal. Arbitrary custom source types and
post-handler generic buffering failures remain OPEN; the send wrapper does not
close all buffering behavior. No financial, authentication grant, database,
deployment or production data work is included.
