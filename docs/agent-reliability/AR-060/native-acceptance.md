# AR-060 live web/browser acceptance — deferred

Use final portable artifact **10907333886**, source SHA `b550855946e2378767917e0c5611f20497a9f791`.

This acceptance is deferred by the user. Do not record E3/E4 PASS until run with explicitly authorized external configuration.

## Search / fetch

1. Configure a user-authorized Brave Search API key through `H2_BRAVE_SEARCH_API_KEY`; do not write the secret to evidence.
2. Through H2 UI + configured model, issue a current-information search and confirm returned facts cite the actual search result/source metadata.
3. Verify 401/403/429/timeout/provider-off states are typed and do not fabricate results.
4. Fetch a known explicit public URL and verify redirect/body limits and exact final provenance.
5. Test a disposable redirect toward localhost/private IP and confirm it is blocked before request.
6. Confirm page prompt-injection text is shown/handled as untrusted data and cannot grant new permissions or instruct H2 to exfiltrate local data.

## Browser

1. Start a dedicated disposable Chrome/Edge profile with an explicit loopback DevTools endpoint; set `H2_BROWSER_CDP_ENDPOINT`.
2. List tabs, inspect/query one exact tab, then navigate/click/type only on a disposable sample site.
3. Confirm wrong/stale tab identity is rejected and that returning a URL is never reported as a successful browser action.
4. Do not use a personal logged-in browser profile. Do not submit forms, upload files, purchase, send messages or trigger external side effects for acceptance unless separately authorized.
5. Login/CAPTCHA/account-only surfaces are NotTested unless explicitly permitted.

Capture build SHA, provider readiness, tab identity, source URL/provenance, typed failure state and H2 task evidence. Never include API keys/cookies in evidence.
