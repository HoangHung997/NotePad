# Recovery from failed or incorrect work

Read this when a command failed, output did not meet the request, an API assumption
was wrong, or repeated attempts made no progress. Apply the same process to code,
documents and data work; do not reduce it to a list of known error strings.

1. Identify the unmet requirement and observed failure, keeping assumptions separate.
2. Inspect the smallest relevant source of truth: actual names/paths, returned schema,
   traceback, installed library API, original document, saved run or current UI state.
3. Choose a changed approach justified by that evidence. Use an alternate supported
   library or a smaller test when useful, not repeated identical calls.
4. Run on copies and verify both the requested change and preserved surrounding data.
   A successful command or a self-written 'OK' message is not independent verification.
5. Report completion only to the extent checked; otherwise explain the investigated
   scope, blocker and smallest missing input. Keep run IDs so work can resume.

Wrong names: observe exact names with list_files/find_files. A likely typo may have
multiple matches; compare user context and read metadata/content before selecting,
and ask when ambiguous. Never rename or create a file just to satisfy a guessed path.
Wrong skill/resource: inspect the actual catalog and read SKILL.md. Do not invent
references or treat instructions for an absent runtime as an installed capability.
Wrong code/output: inspect the failing inputs, APIs, assertions and artifact, then
revise. Do not weaken tests or silently change the requirement to declare success.

Approval refusal, a scope boundary or unavailable capability is not a coding error
to bypass. After uncertain external writes/clicks inspect current state before any
repeat. Stop on user cancellation. No background retries after the turn ends. There
is no general web-browsing adapter in this Lab yet: do not claim Internet research.
