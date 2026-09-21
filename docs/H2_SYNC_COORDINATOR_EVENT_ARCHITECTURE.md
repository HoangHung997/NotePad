# H2 Notes — Cross-Transport Sync Coordinator Architecture

Status: **CANONICAL H2M-133 REMEDIATION ARCHITECTURE**  
Date: 2026-09-21

Read together with:

- `docs/H2_PRODUCT_MASTER_SPEC.md`
- `docs/H2_PRODUCT_MASTER_TASKS.md`
- `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`
- `docs/H2_NAS_REAL_ACCEPTANCE.md`

This document is normative for the H2M-133 redesign after real two-PC evidence proved that the user's mixed WebDAV/SMB paths do not share reliable cross-client locking semantics.

---

## 1. Why the architecture changes

Physical two-PC acceptance produced three independent lock failures on the same real shared storage:

1. `FileShare.None` did not exclude the other PC.
2. atomic `FileMode.CreateNew` did not exclude the other PC.
3. `FileStream.Lock(0, 1)` byte-range locking did not exclude the other PC.

The latest physical run, session `nas-final-06`, produced:

```text
PC1:
Peer acquired the byte-range commit lock while coordinator still held it.

PC2:
Byte-range commit lock was not enforced across nodes.
```

The user then clarified that:

- PC1 reaches the NAS through WebDAV and may do so from outside the LAN;
- PC2 normally uses a mapped LAN path, expected to be SMB;
- at other times both PCs may be on the same LAN;
- both PCs must be able to view the same H2 project and may submit AI work concurrently.

Therefore H2 must not make correctness depend on any file-lock, create-new, rename, share-mode or cache-coherency behavior being identical across client transports.

---

## 2. Architectural decision

The new shared-project path is:

```text
PC1 H2 Client ─────┐
                   │ HTTPS / authenticated H2 protocol
PC2 H2 Client ─────┼──────────────► H2 Sync Coordinator
                   │                    │
PC3 H2 Client ─────┘                    │ single writer
                                        ▼
                               Coordinator durable store
                                        │
                                        ├── immutable project event log
                                        ├── project snapshots
                                        ├── AI queue / leases
                                        ├── sync barriers
                                        ├── conflict records
                                        └── audit / migration metadata
                                        │
                                        ▼
                                 NAS backup/export
```

The **H2 Sync Coordinator is the only authority that sequences shared H2 project mutations**.

Clients no longer depend on direct WebDAV/SMB writes for shared-project correctness.

WebDAV/SMB may continue to exist for user files, legacy import/export, or backup access, but they are not the transaction protocol for H2 project state.

---

## 3. Coordinator is deliberately single-writer in v1

Version 1 uses exactly one active Coordinator process for a workspace.

Do not build distributed consensus, leader election or multi-node Coordinator HA yet.

The Coordinator owns one local durable database/store and serializes accepted project events transactionally.

Preferred deployments:

1. NAS-hosted Docker/container/service when the NAS supports it;
2. always-on LAN Windows/Linux host;
3. dedicated mini-PC/server.

Remote access should use a secure private network or HTTPS endpoint, for example:

- Tailscale/WireGuard/VPN;
- authenticated HTTPS reverse proxy;
- equivalent secure private connectivity.

Do not require raw SMB or WebDAV exposure to the public Internet.

---

## 4. Durable store

Initial implementation target:

```text
Coordinator local storage
  -> SQLite database on the Coordinator host
  -> WAL/local filesystem semantics
  -> one Coordinator process owns writes

NAS
  -> immutable export/archive/backups
  -> never opened as one SQLite database by multiple PCs
```

SQLite is acceptable because only the Coordinator process opens the authoritative database.

The database must not be placed on a remote mapped WebDAV/SMB path and opened independently by multiple clients.

---

## 5. Stable device identity

Every H2 installation has a persistent random `DeviceId`.

Do not use only:

- Windows computer name;
- username;
- drive letter;
- local path;
- IP address.

Those values may change or collide.

The Coordinator also records bounded diagnostics such as device display name and last-seen time, but `DeviceId` is the protocol identity.

---

## 6. Shared project data is event-based

A shared project mutation is represented as an immutable event.

Conceptual envelope:

```text
ProjectEvent
{
    EventId
    WorkspaceId
    ProjectId

    DeviceId
    DeviceSequence
    ClientOperationId

    ServerSequence
    Kind

    EntityType
    EntityId
    FieldKey?

    ExpectedRevision?
    Payload
    PayloadHash

    ClientCreatedUtc
    AcceptedUtc
}
```

Rules:

- `EventId` is globally unique and idempotent.
- `ClientOperationId` makes retries safe.
- `ServerSequence` is assigned by the Coordinator transactionally.
- client time is audit information, not authoritative ordering.
- accepted order is determined by Coordinator sequence.
- an accepted event is immutable.
- retrying one operation must not create duplicate effects.

---

## 7. Do not overwrite one shared ProjectRecord file from two PCs

Legacy behavior conceptually did this:

```text
PC1 -> project.json
PC2 -> project.json
```

That is no longer the multi-device target.

The target is:

```text
Project Snapshot
    +
Project Events after snapshot
    =
Current Project Projection
```

`ProjectRecord`, `TaskRecord`, notes and links remain the H2 product model.

The change is how shared mutations are persisted and synchronized, not a replacement of the user-facing project model.

---

## 8. Initial event categories

The first implementation should cover existing H2 durable project truth without inventing a generic CRDT framework.

### Project

- create project;
- update one project field;
- archive/unarchive;
- reorder where supported.

### Task

- create;
- update one field;
- complete/reopen;
- delete/tombstone;
- reorder.

### Notes

- update project note rich document as one bounded field revision initially;
- update board/general note where still part of shared truth.

Do not implement collaborative character-level rich-text CRDT in v1.

Concurrent whole-note edits may become a conflict requiring resolution.

### Links/resources

- add;
- update;
- remove.

### Conversation presentation data

User/assistant conversation messages that must be shared across H2 devices are append-only records/events.

Agent execution internals remain in Agent state; do not copy full tool traces into project events.

---

## 9. Conflict rule

H2 must never silently use last-writer-wins for incompatible same-field edits.

Each mutable field/entity carries a revision known to the client when it creates the event.

Examples:

### Safe automatic merge

```text
PC1: Task A.Name changes
PC2: Task A.Deadline changes
```

Different fields may merge.

```text
PC1: Task A changes
PC2: Task B changes
```

Different entities may merge.

### Conflict

```text
PC1: Task A.Name -> "Final dossier"
PC2: Task A.Name -> "Acceptance dossier"
both based on revision 7
```

The Coordinator records a structured conflict instead of overwriting one value silently.

Resolution creates a new explicit event referencing the conflicting event IDs.

Delete-vs-update is also a conflict unless a documented deterministic rule applies.

---

## 10. Machine-local outbox

Human editing must remain usable when the Coordinator or Internet is temporarily unavailable.

Each client maintains a durable local immutable outbox.

Flow:

```text
User edit
 -> create local operation/event
 -> apply optimistic local projection
 -> persist local outbox
 -> try Coordinator
 -> accepted + ServerSequence
 -> mark outbox item acknowledged
```

If offline:

```text
Local edit remains durable
Status = Offline / local changes pending
```

On reconnect:

```text
send pending operations
 -> Coordinator validates revisions/idempotency
 -> accepted events or conflicts
 -> client refreshes authoritative projection
```

Do not discard a local pending edit merely because another PC advanced the project.

---

## 11. Shared sync protocol

Clients synchronize through the Coordinator API, not by watching shared JSON files.

Minimum conceptual operations:

```text
RegisterDevice(workspaceId, deviceIdentity)
GetWorkspaceHead(workspaceId)
GetProjectSnapshot(projectId, atOrBeforeSequence?)
GetProjectEvents(projectId, afterSequence, limit)
SubmitProjectEvents(batch)
GetProjectConflicts(projectId)
ResolveConflict(...)
```

Polling is acceptable for the first implementation.

SSE/WebSocket push may be added later for lower latency, but correctness must not depend on push delivery.

---

## 12. Snapshot and compaction

The immutable event log must not grow without bound.

Coordinator periodically creates a verified snapshot:

```text
ProjectSnapshot
{
    ProjectId
    ThroughServerSequence
    State
    StateHash
    CreatedUtc
}
```

Rules:

- snapshot is produced only from accepted events;
- snapshot hash is verified before publication;
- old events are retained at least until rollback/backup policy permits compaction;
- compaction never changes event identity/order;
- client may rebuild from snapshot + later events.

For the first production version, favor safety and retention over aggressive deletion.

---

## 13. AI queue is separate from human project editing

Human project edits use event synchronization and do not require a global project lock.

AI execution for one project is serialized.

Version-1 invariant:

> **At most one project-associated AI execution may be RUNNING for one ProjectId at a time.**

Different projects may run AI concurrently.

Quick work with `ProjectId = null` remains independent unless it later attaches to a project.

---

## 14. AI queue example

PC1 submits first:

```text
Project A
Request R100
Device PC1
CoordinatorQueueSequence 500
State RUNNING
```

PC2 submits while PC1 is waiting for the model:

```text
Project A
Request R101
Device PC2
CoordinatorQueueSequence 501
State WAITING
```

PC2's user message is accepted/displayed immediately.

The UI may show:

```text
Đang chờ lượt AI
PC1 đang xử lý dự án này
```

PC2 may continue to:

- view the project;
- edit tasks/notes;
- create additional durable local/shared events;
- enqueue more AI requests.

PC2 must not start the project AI runtime for R101 until the Coordinator grants its lease.

---

## 15. AI lease

A running project AI request owns a Coordinator lease:

```text
ProjectAiLease
{
    LeaseId
    ProjectId
    AiRequestId
    OwnerDeviceId

    QueueSequence
    GrantedUtc
    LastHeartbeatUtc
    ExpiresUtc

    RequiredProjectSequence
}
```

Correctness depends on the Coordinator transaction, not a NAS file.

The client sends heartbeat while the Agent run is active.

If heartbeat stops beyond the allowed timeout:

```text
RUNNING -> ABANDONED
```

The Coordinator must not immediately start the next writer until it has resolved the previous run's commit state.

---

## 16. AI sync barrier

Before an AI run starts, the client must have an authoritative project projection through the lease's `RequiredProjectSequence`.

Flow:

```text
Coordinator grants lease
    |
    +-- RequiredProjectSequence = N
    |
client syncs snapshot/events through N
    |
verifies projection
    |
AI run starts
```

This prevents PC2 from running AI on a stale project after PC1 completed a prior turn.

Because the barrier is served through Coordinator data, it does not wait for WebDAV/SMB cache propagation.

---

## 17. AI completion barrier

AI text alone does not complete the lease.

For a mutating AI run:

```text
Agent response
 -> verified project mutations
 -> submit immutable project events
 -> Coordinator accepts/assigns sequence
 -> assistant message/activity reference committed
 -> CompleteAiRun
 -> release lease
 -> grant next queued request
```

If project events fail or conflict, the run may be:

- WAITING_FOR_SYNC;
- WAITING_FOR_REPAIR;
- NEEDS_USER_REVIEW;
- FAILED;

but it must not falsely become COMPLETED.

The next queued AI request starts only from an explicit resolved terminal state.

---

## 18. Concurrent human edit during AI

Human editing is allowed while an AI run is active.

AI must not silently overwrite a human change that occurred after the AI's base revision.

The AI's project mutation events use the same expected-revision rules as human events.

If conflict occurs:

```text
AI result remains visible
Project mutation conflict is recorded
No silent overwrite
```

The Agent may be asked to repair/rebase after the current authoritative project state is reread.

---

## 19. Coordinator unavailable

### Human editing

Allowed locally.

State:

```text
Offline
Local changes pending
```

Changes remain in durable outbox and synchronize later.

### Project-associated mutating AI

Do not start a new run without a Coordinator lease.

State:

```text
Waiting for Coordinator
```

This avoids duplicate AI writers on two disconnected devices.

### Read-only/quick work

May be supported later under explicit rules.

Version 1 should prefer fail-safe behavior rather than infer that a project operation is harmless.

---

## 20. Coordinator restart

Coordinator state is durable.

After restart it reconstructs:

- workspace/project sequences;
- pending AI queue;
- leases;
- lease expiry/abandoned state;
- conflicts;
- snapshots.

A lease whose owner cannot resume safely becomes ABANDONED/RECOVERY_REQUIRED before another mutating AI run is granted.

Do not rely on in-memory dictionaries for correctness.

---

## 21. Security and device onboarding

Do not place reusable Coordinator credentials in shared project JSON.

Required direction:

- Coordinator endpoint uses authenticated transport;
- device registration/pairing creates per-device credential/token;
- secrets remain machine-local and protected;
- revoke one device without rotating all project data;
- Coordinator authorizes workspace access and mutation scope;
- log bounded audit metadata, not passwords/API keys.

Initial implementation may use a workspace pairing/admin secret plus derived per-device credentials, but the shared NAS folder must not be the secret store.

---

## 22. NAS role after migration

NAS remains useful, but its role changes.

Allowed roles:

- Coordinator local volume when the service actually runs on the NAS;
- immutable backup/export target;
- attachment/user-file storage;
- legacy import source;
- portable archive destination.

Not allowed as a correctness dependency:

```text
PC1 opens shared H2 project JSON directly
PC2 opens same shared H2 project JSON directly
both rely on filesystem locks to coordinate writes
```

That protocol is retired for multi-device mode.

---

## 23. Data portability

The user must still be able to move the H2 system to another machine.

Coordinator must support a complete export containing at minimum:

- workspace identity;
- project snapshots;
- immutable project events;
- conflict records;
- migration metadata;
- shared conversation presentation data;
- necessary schema/version metadata;
- references/manifests for stored project resources.

Agent provider secrets and machine-local credentials are not exported as plaintext.

The export must be sufficient to restore shared project truth on a new Coordinator.

---

## 24. Legacy Schema-6 migration

Existing H2 project data is preserved.

Migration flow:

```text
legacy workspace
 -> validate complete old snapshot
 -> immutable pre-migration backup
 -> import current ProjectRecord/TaskRecord/Notes/Links
 -> create Coordinator baseline snapshot
 -> record MigrationId + source hash
 -> switch clients to Coordinator mode
```

After successful migration, the old shared multi-writer workspace becomes read-only historical/migration evidence.

Do not allow some clients to continue legacy direct-write mode while other clients use Coordinator mode for the same WorkspaceId.

That would create split-brain.

---

## 25. Migration rollback

Rollback means:

- preserve the original legacy source bytes;
- preserve the pre-migration backup;
- allow the user to return to the old application/data copy if Coordinator migration is rejected;
- never rewrite the source as part of import.

New Coordinator-only events do not need to be silently backported into the old schema.

If the user wants to abandon the new architecture after creating new data, use a deliberate export/conversion workflow rather than pretending the schemas are equivalent.

---

## 26. H2 UI changes

Command Center sync state should evolve from NAS-file wording to Coordinator state.

Examples:

```text
✓ Synced · seq 1842
● Syncing · 3 local changes
⚠ Offline · local changes safe
⚠ Conflict · 1 field needs review
⚠ Coordinator unavailable
⚠ AI waiting for project turn
```

The UI must distinguish:

- project-data sync;
- AI queue;
- Agent run state;
- external resource/file availability.

Do not label every network problem simply "NAS unavailable".

---

## 27. Existing Agent ownership boundary remains

This architecture does not move Agent internals into H2.

Agent still owns:

- task/runtime state;
- tools;
- verification;
- evidence;
- repair;
- provider execution.

Coordinator owns only shared H2 coordination concerns:

- project mutation sequencing;
- project snapshot/event synchronization;
- conflict records;
- per-project AI queue/lease/barrier;
- device/workspace coordination.

Do not create a second Agent engine in Coordinator.

---

## 28. External document edits are a separate problem

A Word/Excel/AutoCAD/file resource may itself live on WebDAV/SMB/cloud storage.

Coordinator project synchronization does not magically make arbitrary external documents collaboratively editable.

Agent/provider mutation must still use:

- resource identity;
- reread-before-write;
- expected hash/revision where available;
- application/provider-specific verification;
- conflict/fail-safe behavior.

Do not use project Coordinator success as proof that an external file write is conflict-free.

---

## 29. Acceptance scenarios

### A. Two PCs, same project, human edits

- PC1 and PC2 open Project A.
- both receive the same Coordinator head.
- PC1 edits Task A name.
- PC2 edits Task B deadline.
- both changes are accepted and appear on both PCs.
- no shared file lock is involved.

### B. Same-field conflict

- both PCs edit the same field from the same prior revision.
- both values are preserved in structured conflict evidence.
- no silent overwrite.

### C. AI FIFO

- PC1 submits Project A AI request.
- PC2 submits Project A AI request while PC1 is running.
- PC2 request remains WAITING.
- PC1 response + accepted project mutations complete.
- PC2 synchronizes through the completion barrier.
- PC2 receives the next lease and starts.

### D. Different projects

- PC1 Project A AI and PC2 Project B AI may run concurrently.

### E. Coordinator outage

- human edits remain durable locally;
- project AI does not start without lease;
- reconnect submits pending edits idempotently;
- conflicts are surfaced.

### F. Coordinator crash during AI

- lease expires/recovery state is detected;
- next queued AI request does not start against unknown commit state;
- no duplicate completion.

### G. Legacy migration

- existing project/tasks/notes/history preserved;
- original source remains untouched;
- both clients switch atomically to Coordinator mode;
- no legacy/new split-brain.

### H. Physical mixed transport

Physical acceptance must include the user's real topology:

- PC1 capable of remote/out-of-LAN use;
- PC2 on LAN;
- both using the same Coordinator;
- legacy WebDAV/SMB mount differences do not affect H2 shared-project correctness;
- disconnect/reconnect is exercised.

---

## 30. Explicit non-goals for v1

Do not build now:

- Raft/Paxos/distributed consensus;
- multi-Coordinator active/active;
- rich-text character CRDT;
- generic arbitrary-object CRDT;
- peer-to-peer leader election;
- NAS file locks as fallback correctness mechanism;
- automatic last-writer-wins on conflicts;
- public-Internet raw SMB;
- storing Coordinator secrets in shared project files.

---

## 31. Core invariants

The implementation is not accepted unless these remain true:

1. One shared-project mutation is accepted exactly once.
2. Coordinator order is monotonic and server-assigned.
3. One ProjectId has at most one RUNNING mutating AI lease.
4. Next AI turn cannot start before its required project sync barrier.
5. Client clock does not decide order.
6. Offline human edits remain durable.
7. Same-field concurrent edits never silently overwrite one another.
8. Coordinator outage does not cause two clients to assume ownership independently.
9. Legacy shared-file multi-writer mode cannot run concurrently with Coordinator mode for one WorkspaceId.
10. PC1 WebDAV vs PC2 SMB behavior cannot change the correctness of H2 shared-project state.
