# Per-send failure ownership

An outer caller can create `PrivateDependencyFailureObservation` and pass it to
`SendWithPrivateFailureObservationAsync(request, callerToken, observation)`.
The existing three-argument overload remains unchanged. The fourth argument is
required, so existing calls with a third `default` argument remain unambiguous.

`WasObserved` becomes true when the explicitly selected dependency observer
claims the failure, before invoking the logging sink. It remains true after
the send unwinds, even if the sink throws or caller cancellation happens during
the sink callback. It indicates ownership, not successful log delivery. A
consumer can suppress its duplicate failure observation using this signal.
False does not identify the failure category or authorize a replacement event.

Create fresh state per send. Reuse, including after success or cancellation,
is rejected before another transport call. The state stores only two integers;
it retains no request, exception, URI, cancellation token, callback or identity.
Request-scoped rendezvous state still clears its callback, terminal exception
and request option during unwind. Pooled handlers retain no per-send state.
Response-body reads remain outside the helper boundary.

The normal native deadline tests cover selected named and typed clients with
real `HttpClient` deadline rewriting. Four deterministic cases cancel the
original caller token inside the sink after the ownership claim, including
throwing sinks. They require one safe event and retained ownership after
unwind. Six cases preserve quiet success, caller cancellation and unselected
native timeouts, reject state reuse and verify cleanup. Existing failure,
privacy, response and cancellation tests remain in the focused and full suites.

Hosted validation builds with warnings as errors and runs the observer focus
before the normal full validation, raw coverage and isolated package consumer.
This producer change does not establish any consumer's adoption or behavior.
