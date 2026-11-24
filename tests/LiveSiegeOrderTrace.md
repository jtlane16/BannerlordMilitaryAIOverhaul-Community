# Live siege order trace

This is a diagnostic replay, not a behavior change. Use the normal build, not a scripted acceptance run. The optional trace never suppresses a native `Hold`, changes a retry counter, or writes to the save.

## Replay

1. With the game closed, place the one-shot marker `%LOCALAPPDATA%\BannerlordMilitaryAIOverhaul\SiegeOrderTrace\trace-v1.armed` with the text `siege-order-v1 <new GUID in N format>`. The next campaign start renames it to `siege-order-v1.<id>.claimed` so later loads are not automatically traced.
2. Launch Bannerlord with the same mod set and load the same starting save used for `3f8d6344`. Check the Desktop `BannerlordMilitaryAIOverhaul.log` for `Siege order trace: … armed`. If it reports `unavailable` or no arm line appears, stop and review the log before running the replay.
3. Run the campaign normally from that save for about **28 in-game days** (roughly 660 campaign hours). Keep the player off impassable terrain and avoid changing diplomacy or manually forcing army orders. Pause at the end and report that the run is complete; a new load will not continue this capture.

Trace lines are mixed into the normal campaign log and carry their own run ID. Look for the same `army`, `target`, `attempt` and `seq` on the `hold` and `review` rows. A `hold` row lists the caller chain and the party state before and after the native method. Review rows distinguish a native lock, a reissue, a failed path and an exhausted reissue budget. `orderRestored` verifies the resulting native order. `straightProgressSinceHold` is distance toward the destination as the crow flies, **not** native route distance. Only route checks the normal decision already ran are logged.

The observer captures active AI siege leaders in muster or march, before camp or closure. It is capped at 32 campaign days, 128 traced assignments, 12 relevant Hold calls and 32 review rows per assignment. A plain `Hold` with no targets is not repeatedly sampled. An observed writer → clear → review chain is evidence for that incident. No repeat or an untraced interruption does not prove who cleared previous orders.

## Inspecting a saved relief order

Use the same `trace-v1.armed` marker with `relief-order-v1 <GUID in N format> <settlement-id> <leader-party-id> [second-leader-party-id]`. For the Lageta save the IDs are `town_EW1 lord_1_26_party_1 lord_1_62_1_party_1`. Load the affected save first. The new `Relief order trace: … armed` lines go to the normal Desktop campaign log. Let the map run for up to two in-game days without overwriting the starting save.

`initial` records the watched assignments before the first relief review, even if their orders are already gone. `review` records the actual reason a reissue waits, whether it succeeded, and the native short-term movement; unchanged states get a sample every two campaign hours. `hold` records the caller only when a subsequent `SetMoveModeHold()` hits an active defend order. The trace observes at most two formations, 12 relevant Hold calls and 64 review rows per formation. It cannot reconstruct a call from before the save.

## Checking progress-based recovery

Live session `6917cbc2` traced 13 clears from native field battle or raid cleanup. Twelve reissues restored the order and five of the traced armies reached camp. That run did not include a third clear in the same phase.

The normal campaign log now records `Siege order recovery progress` when verified new route progress refreshes the budget and `Siege order recovery stalled` for the 24-hour no-progress exit. No marker is needed to see these rows. Match `army` and target across reissues, progress, and native camp; a new army ID or a repeated summons debit is not a successful recovery. The regression fixtures exercise both traveling phases, repeated clears after real progress, stationary route-estimate changes, backtracking, a failed native reissue, changing strength, battle locks, competing orders, and save/load.
