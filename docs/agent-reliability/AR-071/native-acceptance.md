# AR-071 real-environment adapter trial acceptance — deferred

Use the final portable artifact **10921366935**, source SHA `d414490715b5c88bc8e67dcfdc072672c1a474e0`.

This E3 acceptance is deferred by the user. Do not record PASS until a real, authorized third-party application/version and a trusted compatibility adapter package are available. Use disposable/copied data only.

1. Record exact target application ID/version and adapter API version.
2. Record adapter archive SHA256, manifest/payload hashes and every dependency ID/version/SHA256.
3. Use only an explicitly approved workspace/input set; the trial must copy inputs and operate on the copy.
4. Run capability probe before execution. If a cached trial exists, probe again; reuse only when archive/host/dependencies/fingerprint still match.
5. Change the application version or adapter dependency and confirm cache reuse is rejected until a new probe succeeds.
6. Attempt a path outside the trial SafeWorkspace; it must be blocked.
7. Run a bounded successful trial and independently read back the expected copied output; record hash/size diff.
8. Run a self-test or execution failure case and confirm the staged candidate/copy is discarded and the stable production plugin/provider remains unchanged.
9. Do not edit the installed application's executable/service/config to make the trial pass.
10. Do not promote the trial globally in AR-071. Promotion requires a separate explicit user decision plus regression evidence.

Capture source/build SHA, package hashes, host/dependency pins, probe fingerprint, cache decision, trial receipt, bounded diff/readback and stable version before/after.

AR-071 proves a task-local compatibility trial mechanism, not universal external-adapter compatibility or a general security sandbox.
