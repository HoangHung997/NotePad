# AR-012 — scoped resource binding

Status: ACTIVE / NOT_RUN for the new integration until exact-source Windows evidence is reviewed.

The prior 19-case draft has been transferred to the existing branch, not a new engine/store. New integration calls the existing deterministic binding policy from Office dispatch/readback, keeps per-session pins, rejects ambiguous model-selected targets, protects frozen active/selection context, and checks selected desktop identity before action. Existing progress/archive and AgentTurnView now project Project/Global/External target chips; permission gates stay separate. Fifteen new registered cases exercise the production wrapper with a controlled no-network Office transport, real temporary file effects, headless UI tree and a dedicated Windows junction.

Native Office multi-instance PID/view discovery, content-version separation, atomic handle-based file I/O and NAS alias certification are NOT claimed. Current native catalog session/path and helper-connection identity are checked as available; incomplete or stale explicit capture fails closed. Arbitrary FullAccess PowerShell/Python is not an OS sandbox. No new model credential/endpoint, personal document, outgoing user message or second Agent store. E3/E4 NOT_RUN; AR-083 DEFERRED_BY_USER.

## Resumed review — exact Word revision preflight

The inherited candidate `3ee09035e06bb8a02a483d684e141419769b5160` passed its 39-case Windows E2 corpus (three repetitions), retained AR-011/010/001 and 74 Agent suites in run `35720746799`; archive SHA256 `f300e623a1bf876490c01cdbee2d8ddee453a7fa94119a4870a42e5a70a71dbd` was downloaded and verified. This is prior evidence, not acceptance of the following repair.

Review found that Word's locally observed stale StateToken returned a legacy failure without no-effect proof BEFORE calling PatchWordAsync. The outcome bridge consequently classified a rejected input as Unknown, unlike the equivalent Excel guard. The Word check now throws the existing ToolPreflightException with stale_resource. Five registered concrete production tests distinguish three stale operation rejections (replace/format/insert), a valid read-back verified single-line Word fixture edit preserving a guard paragraph, and an actual disposable-file write followed by lost response which must remain Unknown and not repeat. Native Word layout/paging is not simulated as accepted.

Current scope is still AR-012 only. New tests/CI must be reviewed at their actual source SHA before final acceptance. E3/E4 NOT_RUN; AR-083 DEFERRED_BY_USER.
