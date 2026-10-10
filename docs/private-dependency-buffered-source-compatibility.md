# Selected source native buffering ownership (source review only)

Source witnesses: original `3d6506285a58671651d046e97a35fbb8885cea4f` and
`9e51e6c5da29de8e617b65b59d46882cde6d3b64`, specifically the committed
`Maliev.Service.WebApi/WebApiService.SendAsync` helpers. Both await native
`HttpClient.SendAsync(request)` with its default `ResponseContentRead` before
logging a non-success response, and catch the actual exception from that send.
Native content buffering can fail after delegating handlers return a response.
The handler predicate and metadata adaptations accepted in PRs #84 and #85
do not cover that outer phase. Issues #62 and #71 remain mixed open obligations.

## Narrow behavior and compatibility

This proposal extends only the combination of an explicitly selected
`AddPrivateFailureSourceObservation` factory client and the existing
`SendWithPrivateFailureObservationAsync` overload explicitly passed
`HttpCompletionOption.ResponseContentRead`. Completion options are preserved;
the default overload still uses `ResponseHeadersRead`.

The outer selected handler binds a request-owned callback and marks that it
returned an actual response. It defers a non-success status event in this mode
until the real native buffering task completes. On completed native success,
the pending status is emitted once and the original response is returned. On a
non-cancellation exception after this proven response boundary, the actual
outer exception owns one event instead of the pending response status.
The failed send has no returned response status, including when an exception
contains its own status property; this matches the original helper's null
response argument on its exception path. Native
HttpContent wrapping of an IOException into HttpRequestException is preserved;
the recorded bounded type is the actual caller-visible outer type. A generic
buffer failure does not replay a request through resilience; native buffering
still follows the final response returned by the unchanged inner policy.

The old outer OperationCanceledException branch and handler-deadline identity
proof stay unchanged. Post-handler caller cancellation, CancelPendingRequests,
client disposal, body deadline and forged timeout shapes remain quiet; none
has the original handler cancellation identity required by that proof. Pending
status is discarded when such a send fails with cancellation. Buffering
cancellation parity is explicitly OPEN, rather than inferred from a timeout
shape or a guessed native CTS. The generic buffering callback also rejects
OperationCanceledException defensively.

Generic and operation-only modes keep their existing event and ownership
behavior. Unselected clients stay unobserved. Direct HttpClient.SendAsync and
HeadersRead sends retain their previous boundaries; later content reads and
deserialization do not acquire this observation scope. All such adoption and
remaining body boundaries are independent obligations, not silently enrolled
by this producer change.

The once gate marks the surviving caller-owned observation before the provider
runs. Sink failure or late cancellation cannot replace a non-cancellation
exception or completed response. Per-request callbacks and provenance are
cleared in the existing finally path; no state lives on a pooled handler and
concurrent sends cannot consume one another's pending status. Markers stay in
request Options, never wire headers.

Method/type fields use the reviewed PR #85 bounded taxonomy. No caught
exception object, message, stack, arbitrary custom type name, URI/host/path,
query, content or authorization value reaches the log. The actual normal-host
Cloud formatter and opt-in private formatter schemas remain unchanged.

## Validation and exact acceptance requirements

80 named/typed factory and actual normal-host formatter cases are authored,
NOT RUN. They exercise actual native HttpContent buffering, IOException native
wrapping, generic/custom exceptions, 200/400/503 responses, status deferral,
successful buffering, caller cancellation/cancel-pending/disposal/body timeout,
forged timeout shapes, old/unselected modes, direct/HeadersRead boundaries,
sink failure/late cancellation, genuine default resilience recovery and
concurrent send cleanup. Only external transport and response content are
controlled; HttpClient, factory DI, resilience, completion/cancellation and
formatter are real. Tests do not start network hosts, containers or browsers.

Local Release, focused/full tests, format, audit, pack/isolated consumer and raw
coverage are NOT RUN: no qualified finite SDK allocation and fresh admission
4,177,044 KiB below the unchanged 4,194,304 KiB floor. No local SDK is launched.
Static/source checks cannot prove C# compilation or runtime behavior.

The proposed hosted gate requires all 80 cases after the strict build, alongside
the retained 45 predicate and 59 metadata cases. Full-suite retention of the
accepted 865-case baseline, full format/static/security/package validation,
unexcluded raw owned coverage at the unchanged 80% requirement, exact tested
merge-tree/proposed-head proof and exact protected-main checks are mandatory.
945 is a suite forecast, not an executed result. Registry package publication
is not inferred from an isolated produced-package test. Original source full
closure, custom type parity and independent consumer registrations/source or
package pins remain OPEN. No financial, security grant, data or deployment
authority is exercised.
