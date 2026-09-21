# H2M-013 — Real two-PC NAS/SMB acceptance runbook

Status: **READY FOR PHYSICAL TWO-PC RUN — NOT YET ACCEPTED**  
Date: 2026-09-19

This runbook exists because local temp-folder tests and GitHub-hosted CI cannot prove the SMB/NAS semantics required by H2-NONAI-004 and H2-NONAI-006.

## Safety

Use a **dedicated empty test folder on the same NAS/share/filesystem type** used by H2 Notes.

Do not point the probe at the live production `.Note` workspace if avoidable.

Example:

```text
PC1 mapped view: X:\H2-NAS-PROBE
PC2 mapped view: X:\Dữ liệu Hưng\H2-NAS-PROBE
```

The two paths may differ, but they must resolve to the same physical NAS folder.

The probe writes only below:

```text
<shared-root>\.h2-nas-acceptance\<session-id>\
```

## Binary

GitHub Actions publishes a self-contained Windows artifact named:

```text
H2Notes-NasAcceptance-win-x64
```

No .NET installation is required for the published artifact.

CI also runs `--self-test`, but that self-test uses a local filesystem and is **not** H2M-013 acceptance evidence.

## H2M-133 final-gate artifact — 2026-09-21

Use the NAS acceptance probe published by the exact post-physical-failure remediation source:

- H2 source: `82ad43d6dff97e0e7d9bf38caecc428ac02fa996`
- GitHub Actions run: `35555590910` — SUCCESS
- artifact: `H2Notes-NasAcceptance-win-x64`
- H2 Notes regression suite: PASS
- NAS acceptance harness self-test: PASS
- full Agent/provider/transport/Windows publish pipeline: PASS

### Physical attempt #1 finding and remediation

The first real two-PC run reached the lock case with two distinct Windows node fingerprints, proving that both physical PCs observed the same shared session. It then failed because the target NAS/share allowed the peer to open the same file with `FileShare.None` while the coordinator still held it.

That is a real storage-semantics finding, not a launcher failure. Production H2 Notes had used the same share-mode assumption for `.h2-commit.lock`, so weakening the probe would have hidden a real multi-writer risk.

The remediation in source `82ad43d6dff97e0e7d9bf38caecc428ac02fa996` replaces cross-device commit serialization with an atomic `FileMode.CreateNew` lease named `.h2-commit.lease`. Lease ownership no longer depends on the NAS honoring Windows share modes. `DeleteOnClose` plus token-checked cleanup is used for crash/release cleanup, and the real probe now exercises the same production lease implementation.

A new physical run is mandatory; the code/CI result alone does not close H2M-133.

Do not substitute a local temp-folder self-test for the physical two-PC run.

## Run on two physical PCs

Choose one session ID, for example:

```text
nas-20260919-a
```

On PC1:

```powershell
.\H2Notes.NasAcceptance.exe --node coordinator "X:\H2-NAS-PROBE" nas-20260919-a "C:\H2NasEvidence\PC1"
```

On PC2, at approximately the same time:

```powershell
.\H2Notes.NasAcceptance.exe --node peer "X:\Dữ liệu Hưng\H2-NAS-PROBE" nas-20260919-a "C:\H2NasEvidence\PC2"
```

Do not use identical local path strings merely to make the test pass. Use each PC's real mapped/UNC view.

## Required cases

The coordinator and peer JSON reports must both say `Overall = PASS`.

The protocol records these cases:

1. `TWO-PC-RENDEZVOUS`
   - both machines can observe the same acceptance session.

2. `EXCLUSIVE-LOCK`
   - PC1 holds the production atomic-create commit lease;
   - PC2 cannot create the same lease while PC1 holds it, even on shares that do not enforce `FileShare.None`;
   - PC2 can acquire the lease after PC1 releases it.

3. `FLUSH-VISIBILITY`
   - PC1 writes and calls durable flush;
   - PC2 observes the exact SHA-256 and byte length.

4. `RENAME-REPLACE-VISIBILITY`
   - PC1 publishes a flushed temp file using rename/replace;
   - PC2 observes the exact published bytes, not a partial generation.

5. `CONCURRENT-WRITERS-READ-AFTER-COMMIT`
   - both PCs preload the same Schema-6 workspace;
   - both mutate different projects and save from the same baseline;
   - commit locking/merge preserves both edits;
   - both machines reread the final committed generation successfully.

6. `INTERRUPTED-SAVE-RECOVERY`
   - an intentional child-process crash occurs after the first workspace entry is published and before the index completes;
   - the transaction journal remains;
   - recovery restores the previous generation;
   - the second PC reopens that recovered generation.

## Evidence files

PC1 local output:

```text
nas-acceptance-coordinator.json
```

PC2 local output:

```text
nas-acceptance-peer.json
```

The shared session also retains:

```text
coordinator.summary.json
peer.summary.json
session.complete
```

Keep the complete session folder until H2M-013 review is finished.

## Acceptance rule

H2M-013 is not PASS merely because the probe builds or its local self-test passes.

Required closure evidence:

- two distinct physical Windows node fingerprints in the reports; computer names may be identical;
- same session ID;
- same intended NAS filesystem/share;
- all six cases PASS on both sides;
- exact H2 Notes/source build recorded;
- no persistent mixed-generation/hash mismatch after the run;
- resulting JSON reports committed or otherwise preserved as review evidence.

If the real share fails the production atomic-create lease, durable visibility or recovery semantics, multi-writer NAS mode must be explicitly blocked/limited for that storage type instead of weakening the test.
