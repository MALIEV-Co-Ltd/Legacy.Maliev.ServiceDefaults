# Private request observation producer: test-first design

Status: locally tested producer candidate for Defaults issues 57 and 63;
coverage, remaining static gates and protected-main acceptance are incomplete.
The protected producer base is `086760fa0aae976a799dbcda1960d5c0981248cb`.
Root's unchanged canonical baseline session 53414 completed Release 0 warnings /
0 errors, 334 PASS / 0 skip; raw owned coverage was 74.32%, below the final 80%
requirement. Root then built the pass-through scaffold with 0 warnings / 0 errors
and executed 66 focused cases: 46 genuine assertion failures, 20 controls passed,
0 skipped, no setup errors. This agent independently read every TRX result and
failure. Preserved RED: `temp/root-scaffold-red-20261003/scaffold-red.trx`, SHA256
`77D74AA074CEFA66DD86B9AFB2BF1554D40D8018EDE6750DEB7436C0B8E8B800`.
Root authorized minimal implementation after that RED. The resulting candidate
was built in Release with 0 warnings / 0 errors, then passed all 73 focused
contracts and the full 407-test suite, zero skips. Focus TRX SHA256 is
`16AF747D154B201CC6C5FC6824BBF349EA323279F1F7816786A3E5EAA967DC54`;
full TRX SHA256 is
`2703A7DEAFB0C0A4DBE9DDE3D078825C8C5EF817CA618E0D3F32F4F19D8D9CD7`.
Independent production review found no Critical or Important findings in this
bounded producer slice. Raw coverage remains below 80%: the collector reports
75.47%; deduplicated owned physical file/line coverage is 2,838 / 3,760 (75.4787%).
Generated lines are retained; no exclusions or denominator changes are allowed.
Meaningful IAM failure/private-diagnostic boundary coverage is being expanded.
No commit, PR, consumer activation or final acceptance is claimed.

## Authoritative behavior and boundaries

Committed source `a59193ae2ac030d0e1d373ce4ec50c1b35437391` establishes direct-peer
diagnostic admission and completed-response observation.
`2f60d6b077860132db0aa97a5e0508c1d0407bdd` adds per-host identity and response-start
header protection. These committed objects were inspected only in the isolated
source mirror. Current ordinary MALIEV exception handling returns sanitized JSON;
the source Intranet synthetic failure returns an **empty** 500 response with
`application/problem+json`. Those distinct response contracts must coexist.

The planned public API is
`builder.AddPrivateRequestObservation("code-owned-prefix")`. It is explicitly
default-off; no environment flag, provider selection, global formatter replacement,
consumer registration, endpoint redirect, or obsolete service-prefix list is
introduced. A later implementation must validate and snapshot a nonempty bounded
single path-segment prefix (at most 64 characters, no slash/query/fragment), reject
conflicting registrations, and make identical repeated selection idempotent.
The candidate implements selection validation and host-local state; the preceding
pass-through scaffold intentionally implemented none of those during RED.

Current `UseStandardMiddleware` uses MALIEV's custom exception handler, not SDK
exception middleware. Implementation must insert a narrowly guarded diagnostic
branch **before forwarded headers**, with its own real SDK exception handler.
That branch logs the source Warning and throws a controlled synthetic exception;
SDK middleware produces the real Error; its delegate writes the empty problem
response and the producer subsequently logs the safe Critical. Tests forbid a forged SDK category or manually logged
Error. The unselected pipeline and ordinary selected request pipeline keep their
existing order, logging providers, JSON handler, and incident owner.

Implementation detail verified against official .NET 10.0.12 SDK source: its
handler delegate runs before the SDK reports Error. Therefore the candidate's
delegate writes only the empty 500/problem response, and the safe Critical is
logged immediately after awaiting SDK Invoke, still inside the validated scope.
The public SDK ExceptionHandlerMiddleware constructor isolates the branch from
consumer-registered IExceptionHandler services; no provider or global handler is
registered/replaced. Source logs Warning before the controlled throw; neither
custom Warning nor Critical receives an exception object.

The synthetic branch must bypass business/authentication/request-log middleware.
Admission uses the original connection peer, GET, the exact diagnostic path, and
exactly one `X-Maliev-Diagnostic-Id` header accepted by `Guid.TryParseExact(...,"N")`.
Guid.Empty is valid; uppercase input is echoed canonical lowercase. Denial is
quiet direct empty 404; a valid repeat before one minute is quiet direct empty
429. Both carry no-store/noindex, neither carries a nonce or instance marker.
The successful admission produces exactly Warning, framework Error, Critical;
returns direct empty 500/problem JSON; and carries its validated nonce plus a
stable opaque 32-lowercase-hex instance marker. Throttle state is atomic and host
local. The exact one-minute boundary permits another probe.

Custom diagnostic and response-observer ILogger calls must not receive raw
exception objects or arbitrary request data. The SDK Error retains its actual
controlled synthetic exception. A bounded scope supplies validated synthetic
metadata to the existing private formatter; no formatter provenance expansion
from issue 66 is included. Future formatter integration must accept only the
validated Synthetic/DiagnosticId pair, not arbitrary strings or exception text.
Modern native JSON logging remains independently present and unchanged.

## Separate health predicates

The completed-response failure predicate remains source-compatible:
case-insensitive path suffix `/readiness` or `/liveness`, for **any HTTP method**,
including unregistered suffix lookalikes. Returned 5xx produces HealthProbeFailure
with Readiness/Liveness and safe status only. Each operation tracks its own
fingerprint: same status is suppressed for less than five minutes; status change
logs immediately; any completed status below 500 resets; exactly five minutes
logs again. Other completed 5xx produces HandledOperationFailure/HttpResponse.
Thrown ordinary failures skip this post-await observation and retain the custom
handler's single Critical; no second incident is created.

Marker eligibility is narrower and independent: exact configured-prefix,
actually registered **GET** readiness/liveness endpoints only. A configured but
unmapped endpoint, POST readiness, suffix lookalike, metrics, and Aspire-liveness
receive no new marker. Registered unhealthy readiness still returns its existing
sanitized JSON/status; POST may receive the same health response and failure
observation but no marker. Headers are set immediately and protected at response
start. Existing Aspire-liveness and metrics behavior is otherwise untouched.

## NEW executable acceptance matrix

The original reviewed contract matrix contained 66 cases: 46 registered TestServer HTTP
cases, 10 registration guard cases, 8 formatter allowlist cases, and 2 native
logging controls. The fixture uses the real
AddServiceDefaults/AddStandardMiddleware,
actual MapDefaultEndpoints, and an independent recording provider without clearing
native providers. Clock, connection peer, controlled health result, and harmless
business endpoints are test arrangements, not production failure hooks. The
recorder formats actual captured entries through both existing formatters and
retains exception type and actual EventId only. Fixture initialization binds the
controlled health closure, successfully starts the app, then creates the client;
failed initialization disposes the app and any client already created.
Assertions check response status/headers/body before
JSON parsing, so absent behavior reaches genuine assertions rather than parser
setup errors.

| Cases | Boundary and expected evidence |
| --- | --- |
| 10 | Null builder; null/empty/whitespace/path/query/fragment/over-64 prefix; conflicting second prefix rejected |
| 9 | Direct quiet denials: public/missing peer, POST/HEAD, missing/malformed/braced/multiple nonce, trusted proxy spoofing loopback through forwarding |
| 3 | IPv4/IPv6/zero GUID admission; canonical echo; real Warning/Error/Critical pipeline and private formatter synthetic fields |
| 3 | Quiet 59-second repeat/exact-minute admission, invalid nonce does not consume admission, concurrent eight requests yield one admission |
| 2 | Default-off ordinary behavior and selected ordinary thrown failure retain JSON/single existing incident owner |
| 5 | Completed 200/400/404 quiet; returned 500/503 safe response-failure observation |
| 2 | Registered healthy GET readiness/liveness retain actual bodies, marker and no-store, quiet success |
| 2 | Actual registered unhealthy readiness GET/POST preserves sanitized JSON, independently verifies marker predicate and suffix-failure observation |
| 7 | Other paths/methods and configured-but-unregistered endpoint receive no new marker |
| 2 | Per-host stable identity/independent throttle; downstream header overwrite restored at response start |
| 4 | Unregistered readiness/liveness suffixes including POST and mixed case: 20 repeats, status change, 299/300-second bounds |
| 3 | Completed 200/400/404 each resets a previous health failure |
| 1 | Readiness and liveness fingerprints independent |
| 1 | Repeated identical selection does not duplicate events/admission |
| 1 | Native information logging retained; private formatter remains quiet for information |
| 1 | Actual retained native console provider output captured and drained on host disposal; separate from dual-formatter characterization |
| 1 | A mapped business GET at the exact configured readiness path is not a registered health endpoint and receives no marker |
| 1 | Synthetic nonce/no-store/marker survive response-start overwrite arranged on the actual SDK Error event |
| 8 | Formatter scope allowlist: true plus valid N GUID canonicalized (including uppercase/zero); false/string-true/missing/malformed/D-format rejected; arbitrary credential field never emitted |

The SDK Error must carry event 1/UnhandledException and the controlled producer
exception type `Maliev.Aspire.ServiceDefaults.Diagnostics.ProductionObservabilityDiagnosticException`,
not merely a matching category. Its event identity was checked against official
[ASP.NET Core 10.0.12 logging source](https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.12/src/Middleware/Diagnostics/src/DiagnosticsLoggerExtensions.cs).
The synthetic overwrite arrangement is test-only: after capturing the real SDK
Error, the recorder registers an OnStarting callback that overwrites the three
headers. It emits no event or exception itself and invokes no business endpoint.
This specifically exercises protection installed before the controlled throw;
it does not promise protection against arbitrary outer middleware callbacks
registered before the producer boundary. Existing formatter rejection tests stay
untouched. Registration-only builder configurations are disposed. The native
control's INFO severity and Timestamp property were verified directly against the
current native formatter source, not guessed from another formatter's shape.
Root subsequently added seven cases, bringing the current class to 73 (17 Facts
and 56 InlineData): actual native synthetic severity/scope wire and six individual
state/scope-pair precedence arrangements. Root also added an assertion that the
synthetic response-start overwrite callback actually executes once. These checks
were authored after this source candidate; the original 66-case RED does not prove
their prior failure. Root owns their reverse-test/fresh execution sequence and all
test/helper changes. Production files are frozen for root's build and review.
Root reviewed the NEW files before scaffold build, corrected one analyzer-only
collection-size assertion, and independently inspected all 46 reached assertion
failures before authorizing runtime implementation. Compile/setup/resource
failure is not behavior RED. The seven later controls are regression/acceptance
expansion, not evidence of a separately observed pre-implementation failure.

## Ownership and validation sequence

Root now owns the NEW tests/helper and additional acceptance cases. This agent
owns the producer candidate and this document. The production delta is the NEW
extension selection, NEW private state/pipeline/controlled exception, minimal
MiddlewareExtensions insertion, internal endpoint metadata on actual registered
readiness/liveness, and private formatter's strict same-container synthetic pair
allowlist. First fully valid pair wins, state precedes individual scopes; no pair
is spliced across containers or overwritten by later scopes. Existing custom
exception middleware, native formatter, tests/assertions and pre-existing `temp/`
are preserved; index remains empty.

After source review and an explicitly released finite slot: fresh serial Release
build first with warnings as errors and shared compilation disabled, focused NEW
GREEN, then full unfiltered suite/coverage. All generated lines remain in
the denominator. Repository format, vulnerable transitive audit, private pack,
diff, YAML/JWT/secret gates follow; actual commands/counts/hashes will be recorded
only after execution. TestServer needs no PostgreSQL/Redis/container/service/data
or deployment operations.

Consumer activation and exact source-compatible empty-body handling in Intranet,
Auth, and each API remain separately owned obligations. They require their own
registered HTTP/direct-peer controls, dependency pin review, and runtime checks;
producer tests cannot establish adoption. Startup boundary behavior, issue 66
formatter provenance, retired Swagger/Prediction, global native logging, terminal
HTTP observer, AppHost, schema, infrastructure and deployments are excluded here.

Historical preflight before root's later completed canonical baseline observed the exact accepted HEAD
and a clean tracked/untracked tree, but only 1,984,460 KiB free against the required
3,145,728 KiB floor. No native process was launched by this agent at that time.
This transient capacity observation is not a test failure or behavior RED. No
foreign process, memory setting, deadline, configuration, or source was changed.

## Root final local acceptance (2026-10-03)

The original 66 scaffold cases produced 46 assertion failures and 20 passes before
producer implementation. Seven later observation controls, 22 IAM transport/private
logging cases and six real disposed-MemoryCache fallback/private logging cases are
acceptance expansion and existing-behavior characterization, not earlier RED claims.
The final source was restored, built in Release with warnings as errors (zero
warnings/errors), then tested sequentially: 101 focused cases and 435 unfiltered
cases passed, with zero failures or skips. `dotnet format --verify-no-changes
--no-restore` passed after scoped whitespace-only fixes to owned new files.

Final unfiltered TRX SHA-256:
`5E5DF2FC64A6A870AEF3D5AA281DED10EBCA62BBFC36ED54083B2BEA0ACA151C`.
Final focused TRX SHA-256:
`BC3B6BBB6B70F09C007CE97AD372AE5342912F0DAE3CA5829A305E507A50CBB9`.
Raw Cobertura SHA-256:
`BB465023DC08DF963120315D58B76E3C329D8202F39B9457D582E665A1BAF564`.
Owned assembly collector line rate is 0.8027. Deduplicating physical filename and
line number (maximum actual hit count) gives 3,019/3,761 = 80.2712%; source-generated
logging and modern OpenAPI lines remain included. No exclusions or coverage waiver
were introduced. Whitespace formatting changed one physical sequence-point line
relative to the earlier 3,018/3,760 result; only this final result is acceptance.

The six cache cases verify the actual disposed store's documented fallback values,
hashed structured-state identifiers and private formatter redaction. They do not
prove Redis failover, native-provider privacy or registration cleanup. IAM tests
replace external HTTP transport only; actual cancellation is drained and no static
cache reset is used. Independent source review confirmed the cancellation cleanup
and measured zero-IBus-resolution fixture corrections.

The transitive NuGet vulnerability audit reported no vulnerable packages in either
project using the configured current source. The Release private package was built
successfully. Gitleaks history and current source/tests/docs scans reported no leaks;
diff whitespace checks passed. Generated bin/obj paths are ignored. Review-requested
raw evidence under the pre-existing untracked temp directory is preserved and never
staged. Protected PR/exact-head/post-main CI remain separate gates at this update.
Issues 57/63 retain broader consumer and startup obligations; producer acceptance
does not close them. Swagger and Prediction retirement stays intact.
