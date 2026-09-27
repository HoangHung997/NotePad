# AR-040 production job lifecycle — under validation

Status ACTIVE / NOT_RUN. Source integration is not acceptance.

The existing process service is registered in the ordinary Global/Project tool session. Host-owned task/revision/permission and native job handles bind start, poll, output, result, stdin and cancel. Suspended process identity is journaled before Resume; terminal observations advance the original invocation and are re-verified through the existing router. Running/Unknown and missing user outcomes remain blockers. ArtifactStore retains bounded stdout/stderr; no parallel engine/database or PID adoption. Full-access only; scripts are not filesystem/network sandboxes. CancelOnHostExit is explicit; restart replay remains AR-041.

Tests are registered in the existing runner: E1 identity/policy; E2 production adapter with real disposable PowerShell processes/files/archive and scripted model transport. Native Office/model E3/E4 and E5 are not claimed. AR-020/033 native debt and AR-083 DEFERRED_BY_USER remain.
