# AR-040 — owned process lifecycle

Status: ACTIVE / PARTIAL / NOT_RUN at integration. Reuses the existing process service and local command tool. Shared bounded capture drains both pipes past retention caps, validates before dispatch and preserves cancellation/EOF/completeness separately. Twelve owned disposable subprocess cases are registered; no test PASS is claimed until logs are read.

The earlier local drain patch is integrated; do not reapply it. Long-job identity/poll/output/stdin, explicit owner lifetime and durable receipts remain to implement in AR-040. No E3/E4/E5 claim, model call, credential, personal document or new truth store. AR-020/033 E3 pending; AR-083 DEFERRED_BY_USER.
