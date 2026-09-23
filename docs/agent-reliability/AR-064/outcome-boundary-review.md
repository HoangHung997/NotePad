# AR-064 typed plugin outcomes: integrated candidate, not accepted

ACTIVE / PARTIAL. This integrates the reviewed prior conversation patch against 1aef7d3386ea22ae1f8dd30e6ff1ad4948a6016e. New C# build, old-wrapper control, 26 new tests and full CI are NOT_RUN at delivery. The earlier AR064 49/49 x3 and Agent74 results belong only to that base.

The version wrapper now preserves IAgentToolOutcomeExecutor metadata and exact domain payload; legacy executors remain legacy. Existing host validation still rejects foreign identity and strips plugin self-awarded verification. Preflight package integrity failure is no-effect refusal, while inner-executor failures retain their actual effect classification. Existing call leases cover success, cancellation and exceptions. Quarantine keeps the plugin disabled when a fallback has invalid JSON/data, without repairing damaged source bytes.

Tests cover seven typed statuses; real temporary-file scheduler fixtures fence repeated uncertain/running writes; identity, legacy compatibility, revocation, exact callable, cancellation and lease release; corrupt active/fallback packages. Resolvers and job IDs are declared fixtures. No native E3, UI/model E4 or two-PC E5 acceptance is inferred.

The read-only validation runs a bounded Windows counterfactual using only the two old PluginManager classes with the unchanged seven metadata tests, then restores exact bytes and rebuilds current source before positive tests. Compiler errors or unexpected failures are not successful controls.

Still required: product-used lifecycle composition, task pin/journal integration, RC-28 real trusted package execution. No AgentRuntime or ProjectRecord redesign, endpoint change or mutation replay. After saving this AR064 checkpoint prioritize critical AR065-068 and AR069. AR020/033 E3 and AR051 E4 remain pending; AR083 stays DEFERRED_BY_USER.
