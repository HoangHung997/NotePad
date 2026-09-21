H2 NOTES - REAL TWO-PC NAS ACCEPTANCE

1. Extract ONE current H2Notes-NasAcceptance-win-x64 artifact.
2. Copy that same extracted bundle to both physical PCs.
3. On both PCs, H2Notes.NasAcceptance.exe --info must show:
   CommitLeaseProtocol = byte-range-file-lock-v1
   and the same SourceStamp.
4. Use a dedicated empty test folder on the same NAS/share/filesystem type used by H2 Notes.
   Do not use the live production .Note folder.
5. Run PC1_RUN_NAS_TEST.cmd on PC1 first.
6. Run PC2_RUN_NAS_TEST.cmd on PC2.
7. Enter the same NEW Session ID on both PCs. Never reuse nas-final-05 or another old session.
8. Local mapped paths may differ, but they must resolve to the same physical NAS test folder.
9. Preserve:
   PC1: nas-acceptance-coordinator.json
   PC2: nas-acceptance-peer.json
   shared session: coordinator.summary.json, peer.summary.json, session.complete

PASS requires all six cases on both sides:
- TWO-PC-RENDEZVOUS
- EXCLUSIVE-LOCK (production byte-range FileStream.Lock)
- FLUSH-VISIBILITY
- RENAME-REPLACE-VISIBILITY
- CONCURRENT-WRITERS-READ-AFTER-COMMIT
- INTERRUPTED-SAVE-RECOVERY

If EXCLUSIVE-LOCK fails on the byte-range build, do not weaken the test. The share must be treated as unsupported/limited for safe multi-writer mode until another storage strategy is chosen.
