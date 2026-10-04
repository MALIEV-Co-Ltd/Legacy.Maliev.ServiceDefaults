# Private failure source compatibility

This candidate addresses producer behavior from source commits
`38a6247161979d046354401ec32e1fb5d7546f53` (operation taxonomy) and
`a59193ae2ac030d0e1d373ce4ec50c1b35437391` (runtime failure provenance).
It does not activate consumer clients or retire their independent acceptance.

## Explicit client selection

The existing `AddPrivateFailureObservation(builder, dependency)` contract retains
the generic `HttpRequest` operation. The new explicit
`AddPrivateFailureOperationObservation(builder, dependency)` emits only literals
from a finite retained-resource/seven-method vocabulary, otherwise
`UnknownOperation`. Prediction is retired and not part of the vocabulary.
Only the first nonempty path segment is classified; later segments, host,
query strings, request/response bodies and caught exception messages are never
exported. Dependency identity remains configured code-owned metadata.

Repeated identical selection is idempotent. Conflicting dependency identity or
generic/operation-aware mode for the same client name throws during registration
rather than silently changing another consumer's observability contract.
The handler preserves terminal-only observation, caller cancellation and original
transport outcomes; logging-provider failures do not replace the caller outcome.

## Runtime provenance and privacy

Warning/error/critical records obtain UTC time, entry-assembly name/version,
active W3C trace/span, exception type, immediate inner-exception type and outer
throw-site `Type.Method` from actual runtime metadata. State and scopes cannot
override those fields. No formatter callback, arbitrary message, stack trace,
filesystem path, exception data or protected object is rendered.

Missing or unsafe bounded identifiers fail closed. In particular compiler-
generated async/lambda type or method punctuation is not retained: source
location becomes null. This is an explicit privacy difference from the original
source's unconstrained type/method text, not a claim of exact metadata parity.
Missing, hierarchical or default W3C identifiers do not produce trace/span fields.
The existing independent 64-field and 16-scope traversal budgets remain unchanged.
Normal console formatting and globally unselected clients are unchanged.

## Acceptance status

The original producer baseline executed 54 cases: 43 expected contract failures
and 11 passing safety/compatibility controls, with no abnormal test outcomes.
The candidate strict Release build completed with zero warnings and errors.
Operation cases (47), provenance cases (17) and existing producer controls (14)
all passed: 78 executed, no failures or skips. Focused TRX SHA256:
`92EDE3E729E72DDEC5D39A41197DBF8ACEAE8AC071537020AA8C8D8896448736`.
Full affected suite, physical raw coverage, static checks, consumer acceptance and
protected-main CI remain required before acceptance. Documentation or producer-
focused tests alone cannot close a consumer/source-owner obligation.

### Integrated validation, 2026-10-04

Fresh strict Release build: zero warnings/errors. Revised real factory/auth,
IAM and Redis boundaries: 96 passed, zero failures/skips. Full affected suite:
646 passed, zero failures/skips or abnormal outcomes. Full TRX SHA256:
`05FCAE5D38C4941F7CFB5D458B00E3D9E94D182CFCA5245E0EFBA3CA57F5693A`.
Unfiltered reported physical coverage for this assembly: 3378/4211 (80.22%);
coverage XML SHA256:
`CF3670417B9D61375CE6A6CB18B5A2AC690910784FBA39E30E40408715BD72EA`.
No reader exclusions were applied; private Contracts is separately reported
54/57 (94.74%). This does not prove uninstrumented source completeness.
Whole-solution formatting, package audit (two projects, zero known vulnerable
packages), package creation and history secret scan (57 commits, zero findings)
passed. Consumer activation, independent final review, exact PR CI and post-merge
main proof remain required. No source-owner or consumer obligation is closed
by this local integrated evidence.
