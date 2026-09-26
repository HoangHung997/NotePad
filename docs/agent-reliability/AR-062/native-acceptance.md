# AR-062 native document acceptance — deferred

Use final portable artifact **10908970510**, source SHA `af0133a8728a9084c539bb4b777329daa246e76b`.

This acceptance is deferred by the user. Do not record E3/E4 PASS until it is run on an authorized Windows PC using disposable documents and the configured model/provider.

## DOCX

1. Through H2 UI, ask the configured model to create a disposable DOCX containing multiple paragraphs, a table, header/footer and at least two sections.
2. Inspect the staged artifact through the product path and confirm the returned verification receipt is tied to the exact bytes.
3. Publish create-only to an empty target and confirm the artifact card opens the exact published file.
4. Modify the staged bytes after inspection and confirm publication refuses the stale receipt.
5. For overwrite, change the existing destination and confirm a stale expected hash fails before replacement.
6. Verify native Word opens the result and manually inspect pagination/layout. Only this native/render evidence may close layout acceptance.

## XLSX

1. Create a disposable XLSX with stored values, formulas, styles, merged cells and at least two sheets.
2. Inspect staged content through H2 and confirm formula/value/structure classification.
3. Publish and verify exact bytes/path.
4. Open with native Excel and force recalculation where formula correctness depends on recalculated values.
5. Compare stored versus recalculated values explicitly; do not treat stored cached values as native recalc proof.
6. Exercise overwrite with current expected hash and confirm prior destination backup remains recoverable.

## PDF

1. Create/export a disposable PDF with known text and multiple pages.
2. Standard AR-062 inspection should report signature-level verification only unless a configured PDF content/render verifier is used.
3. Publication must keep requiresFurtherVerification=true when only signature proof exists.
4. Use the configured extraction/render path or native viewer to verify text/page count/layout before claiming PDF content/layout success.

## Multi-output / interruption

1. Generate at least two outputs; inspect only one and verify the other cannot be published as verified.
2. Interrupt the publication sequence before replacement/commit boundary and verify the original destination is not silently lost or misreported as verified.
3. Test destination collision and wrong output folder scope.
4. Capture exact task/tool/artifact/evidence IDs and before/after hashes.

## Evidence boundary

E3/E4 evidence must identify build SHA, provider/model, source artifact hash, verification receipt, published path/hash, native application/viewer used, and any recalculation/render evidence. Do not use personal documents.
