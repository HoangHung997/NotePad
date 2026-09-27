# AR-060 — Real search/fetch/browser production backends

Validated code: `b550855946e2378767917e0c5611f20497a9f791`.

## Implemented production behavior

- Brave Search backend enabled only by explicit `H2_BRAVE_SEARCH_API_KEY`.
- Search results preserve URL/title/publisher/description provenance and classify auth/rate-limit/provider failures.
- HTTP fetch/download path validates absolute HTTP/HTTPS URLs, rejects embedded credentials, validates every redirect target, blocks private-network targets on the public-fetch path, and bounds response size before context ingestion.
- Missing search configuration is a typed readiness state; explicit URL fetch does not pretend to be search.
- Optional browser provider is configured only from `H2_BROWSER_CDP_ENDPOINT`, which must identify a loopback DevTools HTTP/HTTPS endpoint.
- Browser operations keep exact tab/session identity and separate read-only list/inspect/query from navigate/click/type actions.
- Legacy URL-only browser fallback is advertised as unsupported for browser-control semantics.
- Web page text is untrusted data and cannot grant permissions or broaden resource authority.

## Validation

- AR-060 focused: 6/6.
- retained AR-052: 8/8.
- retained AR-042: 6/6.
- retained AR-041: 6/6.
- retained AR-024: 2/2.
- full H2: 1337/1337.
- required Agent suites: 75/75.
- dedicated AR-060 run 36245687613 / job 108414334901: SUCCESS.
- full Avalonia CI 36245687565 / job 108414335312: SUCCESS.
- all 20 pull-request workflows on exact SHA: SUCCESS.
- Windows x64 self-contained publish and packaged helper IPC: PASS.

## Acceptance boundary

E1/E2 are PASS. Tests use deterministic HTTP/CDP handlers and prove production wiring/policy, not a live external account/browser session. E3/E4 with real Brave credentials, real public web responses and an authorized dedicated CDP browser is deferred by the user. No live-search/current-fact/browser-act/form-submission claim is made.
