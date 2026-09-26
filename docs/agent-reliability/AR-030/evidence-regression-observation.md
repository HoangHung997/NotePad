# AR-030 — executed evidence regressions, repair still blocked

**ACTIVE / REPAIR_REQUIRED; not DONE.**

Tests and registration committed at `44959a651d08fa363ab8adf65196d54269432a26`. No application runtime changed. Six earlier LOCAL_ONLY cases now compile and execute: **4 pass / 2 fail** in each of three AR-030 runs (**37 pass / 2 fail** overall). Full CI on `85000d327f6cb0e0053b1301253317fe6faff070` has **756 pass / 2 fail**. The missing-reference and conflicting-identity requirements fail. This is actual current-source behavior, not a mutation-control result; failures remain failures.

Focused run 35774591898; full CI 35774591845. Build passed (34 warnings), downstream full-CI suites/publish skipped. No portable artifact or product acceptance. Retained AR-020/012/011/010/001 passed in the focused job; its independent Agent-suite step did not run. [Exact structured observations](evidence-regression-observation.json).

The direct source repair was denied by the tool safety layer. Independently permitted tests do not alter or route around that denied operation. Only tests and documentation were written. The original multi-file patch is now stale because its test files already exist; only the resolver repair remains uncommitted.

Keep AR-030 active. Resolve the source-write safety restriction, then review the source-only AgentGoalState patch. Six evidence tests are now committed and executed; do not reapply the stale four-file package. Run the six cases, full AR-030/RC-11/12, retained regressions, all mandatory Agent suites and full CI on the repaired SHA. Do not weaken assertions, convert the two real failures to expected PASS, mark AR-030 DONE or advance AR-031.

AR-020 native E3 pending; E4 NOT_RUN; AR-083 DEFERRED_BY_USER. No personal data, new model endpoint/credential or native Office application was used. A successful documentation writer is not successful application CI.
