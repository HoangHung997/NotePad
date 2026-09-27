# AR-063 real AutoCAD acceptance — deferred

Use final portable artifact **10910881244**, source SHA `da8b9daefa1bbb1e86b833a72c68ebd395e62cb0`.

This acceptance is deferred by the user. Do not record E3/E4 PASS until it is run on an authorized Windows PC with a real installed AutoCAD instance and a disposable DWG.

## Live selection / read

1. Open a disposable DWG in AutoCAD in the same Windows user session as H2.
2. Select one attributed block with a unique handle/layer/tag/value.
3. Through H2 Agent, discover the live document and current PickFirst selection.
4. Confirm reported document/session identity, entity handle, object type, layer and attribute values match the selected block.
5. Change the AutoCAD selection before the next read. The old entity must not silently remain readable as the current selected target.

## Bounded live mutation

1. With one attributed block still selected, read its current document/entity tokens.
2. Update exactly one attribute tag to a disposable value.
3. Confirm the same handle/tag changed and unrelated attributes/entities remain unchanged.
4. Verify exact readback through the live provider.
5. Try the old document/entity tokens again; stale state must reject before another effect.
6. Remove the block from PickFirst selection before mutation; mutation must reject before effect.

## Session rebinding

1. Save As the drawing to a new path and verify the previous live identity is not silently reused.
2. Close and reopen the drawing, including a same-name/near-name case.
3. Confirm stale session/handle/token is rejected and H2 does not substitute the saved file or another open drawing.
4. Test an unsaved drawing and confirm no disk snapshot is substituted for its live state.

## Undo / transaction boundary

The current external COM bridge does **not** claim transaction/undo atomicity. Observe actual AutoCAD undo-stack behavior after the bounded attribute write and record it as native evidence. Do not advertise safe undo unless the native provider path is separately implemented and verified.

## Unsupported scope

Confirm the Agent does not advertise or execute general live `update_entity`, live plot/verify-plot, arbitrary command/LISP/script, or general dynamic-block operations through this AR-063 provider.

Capture build SHA, AutoCAD version, document/session identity, entity handle/layer/tag, before/after tokens, selection state, mutation/readback evidence and any close/reopen/Save As behavior. Reopen AR-063 for any native regression.
