# AR-062 — End-to-end document artifact verification and publication

Validated code: `af0133a8728a9084c539bb4b777329daa246e76b`.

## Implemented path

AR-062 reuses the existing Python/AppContainer + ScriptWorkspace + publish_artifact path. It does not create another document engine or artifact database.

Before publication, a staged artifact must have an exact-byte verification receipt bound to run ID, artifact path and SHA-256. Publication refuses stale/missing receipts and preserves the exact staged bytes.

### DOCX
- reopened independently with H2 document safety checks;
- read through the closed OpenXML Word structure reader;
- classifies extracted text, body/header/footer paragraphs, tables and sections;
- does not claim rendered layout fidelity.

### XLSX
- reopened independently with H2 document safety checks;
- read through the closed workbook reader;
- classifies stored values, formulas, styles/merges/sheet structure;
- does not recalculate formulas and does not claim chart/render/layout fidelity.

### PDF
- signature/size verification only in the standard runtime;
- content and layout remain unverified until a configured extraction/render path supplies proof;
- publish result carries requiresFurtherVerification=true.

## Publish contract

- missing destination: create-only;
- existing destination: exact expected hash required;
- prior destination bytes are backed up before replacement;
- output bytes equal the verified staged artifact bytes;
- changing staged bytes invalidates the verification receipt;
- verification of one output never authorizes another output from the same run;
- runtime completion verifier will not promote signature-only PDF publication into content/layout completion proof.

## Validation

Exact source `af0133a8728a9084c539bb4b777329daa246e76b`.

- AR-062 focused: 6/6.
- retained AR-060: 6/6.
- retained AR-052: 8/8.
- retained AR-042: 6/6.
- retained AR-041: 6/6.
- retained AR-033: 42/42.
- retained AR-024: 2/2.
- full H2: 1343/1343.
- required Agent suites: 75/75.
- dedicated AR-062 run 36249334920 / job 108424301064: SUCCESS.
- full Avalonia CI 36249335285 / job 108424319267: SUCCESS.
- all 21 pull-request workflow identities on exact source: SUCCESS.
- Windows x64 self-contained publish and packaged DesktopHost/OfficeHost IPC: PASS.

## Acceptance boundary

E1/E2 are PASS. Native E3/E4 is DEFERRED_BY_USER / AWAITING_ENVIRONMENT. No PDF-content PASS, rendered-layout PASS, native Excel recalculation PASS, or H2 UI + configured live-model acceptance claim is made.
