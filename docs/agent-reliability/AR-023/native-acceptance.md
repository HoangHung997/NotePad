# AR-023 native Word acceptance — deferred

Use final portable artifact **10897572845**, source SHA `c87778aa972d10a83a34b046ebf06fd5e5e34eed`.

Run later only on an authorized Windows PC with real Word and a disposable document.

1. Long document with unique first/middle/end markers: page through with nextCursor + the same ContentVersion.
2. Move selection only: content version remains valid.
3. Edit document content: old continuation must return stale_content.
4. Read a bounded range containing table/field/embedded/content-control/section-break structure: H2 must report structural kinds, not silently certify flattened plain text.
5. Read table rows/cells through structured table paging.
6. Apply multiline replacement to a plain uniformly formatted paragraph using observed ContentVersion; verify following paragraphs, tables, sections, headers and footers are preserved.
7. Mixed-format or unsupported structured text replacement must reject before effect.
8. Unsaved documents must remain bound to the exact live document.
9. Layout equality is separate acceptance and must not be inferred from text equality.

For failure evidence capture prompt, permission preset, source SHA, session/document, cursor/contentVersion and before/after structure. Reopen AR-023 for repair.
