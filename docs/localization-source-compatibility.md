# Shared localization compatibility

Tracks ServiceDefaults issue #70. This is an implementation candidate, not
protected-main acceptance or completion of a mixed-owner source commit.

Source checkpoint: `135e526d0dab85c415b3afdcefd7b70fe2c82e2f`.
Individually retained source history:

- `5fac706a7983a6d359b39acbd670e6800afe020e`: public cookie and route-culture middleware.
- `72eb9f1949176392141951d35e6e06f7c30af4c2`: documentation changes; not a runtime fix.
- `03dc9a1271c16e6535934445e9dd6e3f30e8fffe`: generated XML belongs in build/package output.

The original public namespaces and constructor/method shapes are retained.
The cookie middleware uses the first configured cookie provider, writes the
selected culture and UI culture before the next middleware, and passes through
without writing a cookie when the provider or culture feature is absent. Its
extension uses real DI middleware activation; callers must register its service
and run request localization before it.

The route middleware retains case-insensitive matching, the legacy-router
fallback, generated-path redirect, and 404 when no path can be generated.
It does not silently substitute endpoint-route generation for the original
IRouter contract. Modern endpoint consumer acceptance is a separate obligation;
the no-router pass-through is not proof of an endpoint redirect.
The route middleware requires a configured `RouteDataRequestCultureProvider`;
the original failure when that prerequisite is missing is retained rather than
silently selecting a different route key or changing pipeline configuration.

## Executed evidence

Strict Release build of ServiceDefaults and tests: zero warnings and errors.
The accepted private Contracts dependency is pinned at
`78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`; the existing solution mapping builds
that external dependency in Debug, not Release. No whole-graph Release claim.

The initial obsolete test-host API build failure was corrected in the new test
fixture. It is not a feature RED. Thereafter all sixteen actual regression cases
failed with missing public middleware types, with zero skipped/error/timeout/
aborted tests. RED TRX SHA-256:
`CF4A5DF5677180E61082AD14D905817A8EBFA09D81211F018B47C17B1827692C`.

Only after that RED, the two public middleware files were implemented. A fresh
strict build and sixteen focused cases passed, including real DI/HTTP cookie
requests and real Route redirect/404 generation. GREEN TRX SHA-256:
`CEAB30AF7E1A0A2E5BB7C8214B878E80B1236D4378B40FCE1ECA7254D87C180A`.

A seventeenth packaged-XML test reproduced the missing documentation file:
one failure, zero passes/skips. RED TRX SHA-256:
`C1BA238BB5B41B15E3AAEE78A9172FDBA1B0135083628C426052FD43A1BCD09D`.
Only then was `GenerateDocumentationFile` enabled. A fresh strict Release build
passed with zero warnings/errors and all seventeen localization cases passed.
GREEN TRX SHA-256:
`C3B2735599B449BAD8841DF55FB732943AB260869B32F30C186A7A0F4102A9D1`.

The full affected suite passed 463 tests, zero failures/skips. Full TRX SHA-256:
`A4BAADF98E48CB898371C5139168F46FC159BC41C027ED450F5516F28507F4D3`.
The no-exclusion production coverage report is 3067/4135 lines (74.17%), below
the required 80% gate. XML generation exposes previously absent generated
OpenAPI documentation code; its cache, identifier helper and transformers need
meaningful runtime consumer coverage, not exclusions or synthetic line hits.
Coverage XML SHA-256:
`E489B32B843A08218BB3E38843DFD3B1BDEF4D619233DDEB62E15F21A6E34AF4`.

Independent source review found no blocking middleware compatibility issues.
Six further real HTTP cases verify shared XML schema/property descriptions in
v1/v2 documents for Development and Staging, and documentation exclusion in
Production. An initial expectation that `info.version` includes a `v` prefix
was a fixture error, not a runtime regression; after correcting only that
expectation, all six cases passed without any OpenAPI runtime change.
Their TRX SHA-256:
`F327296FA714EAAC3DFB5CE2BDC3D8D87E02D9AEB973B8AEB5D55208A5877796`.

The expanded full suite passed 469 tests, zero failures/skips; coverage remained
3067/4135 (74.17%). Full TRX SHA-256:
`1AC630C4193926F1F9890F5B99E561B72286F40EA9A57FD6F30DD340B27F7A08`.
Coverage XML SHA-256:
`A2F57528E8626C06A8F6D7254824B2050FE1042EB77C68BC59388E65D05185F8`.
Whole-solution formatting verification passed. Package audit reported no known
vulnerable packages. Release package creation passed; direct ZIP/XML readback
verified nine localization members with nonempty descriptions at
`lib/net10.0/Legacy.Maliev.ServiceDefaults.xml`. Package SHA-256:
`B080E10F887F63968259591FB9ED02103C4EBE5AE31449CA4109E6799B6245D9`.

The raw coverage gate, secret scans, required PR CI and post-merge main
verification remain pending. No new commit or deployment; this is not a
completed slice while the coverage gate fails.

### Integrated validation superseding the prior coverage failure, 2026-10-04

Strict Release build passed with zero warnings/errors. The full affected suite
passed 646 tests, no failures/skips; physical reported Defaults coverage is now
3378/4211 (80.22%) without reader exclusions. Full TRX SHA256:
`05FCAE5D38C4941F7CFB5D458B00E3D9E94D182CFCA5245E0EFBA3CA57F5693A`;
coverage XML SHA256:
`CF3670417B9D61375CE6A6CB18B5A2AC690910784FBA39E30E40408715BD72EA`.
Whole-solution formatting, two-project vulnerability audit, Release package
creation and history secret scan passed. These are current integrated local
results, not exact PR/main CI or consumer migration acceptance. The historical
74.17% result above is retained as provenance, not the current coverage result.
