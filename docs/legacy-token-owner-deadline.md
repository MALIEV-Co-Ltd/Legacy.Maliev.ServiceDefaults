The original WebApiService RequestJwtTokenAsync uses buffered SendAsync under a
finite HttpClient timeout. The migrated shared legacy workload provider retains
finite issuance and cache refresh, but ResponseHeadersRead ends HttpClient's
timeout coverage at the headers. Previously its body reads passed no owner
cancellation, so a stalled body could leave the shared refresh occupied.

The provider now owns one deadline from the already configured named client's
Timeout. It spans sending and bounded body reading. Caller cancellation still
cancels only that caller's wait. An expired owner returns the existing unavailable
result, releases the shared refresh for a retry, and cannot cache a late result.
The inner exchange retains ownership of its request, response and stream until
completion, including late completion of cancellation-ignoring transports. Late
faults are observed; no foreign task or transport is terminated. A transport that
never cooperates or completes cannot be claimed fully cleaned up.

Normal registered Quotation and Procurement clients consume this provider.
Procurement's additional local guards remain independently owned and unchanged.
Tests use normal host/DI/provider/outbound authentication handlers and controlled
primary transports; they do not establish a fresh full application join. Existing
wire casing, credentials, token lifetime bounds, cache skew, response size bounds,
grants, validators and endpoint routing are unchanged. No Basic, HS256,
non-expiring token, source private origin or original thirty-minute timeout is
restored. Consumer pins require their own reviewed migration.

The regressions were authored uncommitted before the runtime change. Expected
baseline failures were not executed: no local SDK is admitted and no knowingly
failing tests-only commit was published. Actual hosted coherent-candidate evidence
must establish build, focused/full suites, raw generated-inclusive coverage,
static checks and package consumption before protected acceptance.
