# Completed response failure ownership

The selected private response observer can surround a consumer's normal
`UseExceptionHandler` pipeline. After a throw is handled or re-executed, the
framework leaves `IExceptionHandlerFeature.Error` on the current request. The
observer skips its completed-response classification in that case, preserving
the exception pipeline's failure ownership instead of emitting another
`HandledOperationFailure` or `HealthProbeFailure` for the resulting response.

This uses the framework request feature, not a response header or the consumer's
private incident marker. A returned 500/503 without that feature still emits the
existing response classification. The observer does not change exception
logging, diagnostics suppression, response status, content, or headers.

Production TestServer regressions exercise a downstream path-based exception
handler, a catch/log/rethrow owner, the actual original-path feature during
re-execution, and normal/readiness-suffix throws. A header-only returned 500 is
a positive control. Existing ordinary returned 500/503, outer exception owner,
and health fingerprint tests remain in the focused hosted validation suite.

Non-framework handlers that catch exceptions without setting this feature are
outside this guard's contract. Consumers adopt the resulting commit separately;
this change does not update their dependency pins.
