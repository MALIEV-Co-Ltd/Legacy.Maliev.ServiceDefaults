# Logger owner evidence

This packet preserves 19 Defaults owner portions (119 source/path assignments:
43 Defaults-only and 76 within mixed commits). It does not close any source
commit, retire operational logging, or change another owner's ledger.

`historical-memberships.json` preserves exact commit, parent and path membership.
`path-obligations.json` records committed source blobs and distinguishes the
operational adapter from SQL sink delivery/query surfaces. Retirement eligibility
is not retirement approval. Mixed commits retain their other owners' obligations.

`behavior-evidence.json` links explicit adaptations and obsolete adapter artifact
supersession to actual passed case IDs
from accepted Defaults main `7b3099bf67d0f17e56cfdb3dcf36541304abaac2`, native
run `37569609354`. `target-bodies.json` binds current producer and test bodies to
that implementation. These are supporting observations, not historical
byte equivalence or newly executed tests.

Provider preservation and correlation, private exception metadata, route-template
privacy, and mapped JSON response preservation have retained native evidence.
Dependency terminal-failure classification and once-only observation are linked
to the successor's `DependencyFailureDetails` source blob, selected-client
producer/registration/context bodies and actual native timeout, transport, 503,
caller-cancellation and throwing-provider case IDs. This successor evidence does
not automatically satisfy the older Logger paths. No runtime defect has been
established by the inventory.

Run the validator with the accepted Defaults checkout supplied explicitly:

```powershell
./Validate-LoggerObligations.ps1 -CanonicalDefaultsRoot B:/maliev-legacy/Legacy.Maliev.ServiceDefaults
```

This works from a frozen packet directory as well as the repository. The validator
requires the accepted commit and verifies target body hashes, exact membership
and native supporting case identities. Source9e51 and source5ac supersede obsolete
adapter projects and generated documentation; operational diagnostics remain
required. Mixed non-Defaults owners, fleet delivery, plaintext compatibility,
cloud ingestion and read-body failures outside the selected observer are excluded.
`operational-owner-resolutions.json` records 19 bounded operational adapter
assignments, each with exact committed source identity, accepted replacement
bodies and passed native case IDs. Validate those records with:

```powershell
./Validate-OperationalResolutions.ps1 -CanonicalDefaultsRoot B:/maliev-legacy/Legacy.Maliev.ServiceDefaults
```

The 100 SQL delivery/query assignments remain separate.

This documentation-only slice changes no runtime source, build inputs or test
implementation. Build and native test execution are not applicable to the new
evidence files; the native observations were previously executed on accepted
main. Current validation checks JSON, source/path membership, body hashes,
retained test identities, PowerShell execution and secret scanning. The separate
IAM HTTP fixture still requires its own build and native validation.
