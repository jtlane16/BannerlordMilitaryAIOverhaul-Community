# Optional normal-play pacing capture

The ordinary DLL contains the pacing capture but no acceptance runner. Prefer it for profiling normal play. This is not a scripted acceptance test and it does not start the game, change time control, move a party, display a popup, or touch saves or acceptance receipts. The capture writes to its own file; normal campaign diagnostics retain their existing Desktop destination.

## After approval and installation

With the game closed, create **one** explicit marker in `%LOCALAPPDATA%\BannerlordMilitaryAIOverhaul\Performance`. The file is `capture-v1.armed`, containing `performance-v1 <fresh GUID in lowercase N format>`. This example refuses to overwrite an existing marker:

```powershell
$dir = Join-Path $env:LOCALAPPDATA 'BannerlordMilitaryAIOverhaul\Performance'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$id = [Guid]::NewGuid().ToString('N')
$file = [IO.File]::Open((Join-Path $dir 'capture-v1.armed'), 'CreateNew', 'Write', 'None')
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes("performance-v1 $id")
    $file.Write($bytes, 0, $bytes.Length)
} finally { $file.Dispose() }
$id
```

Leave acceptance markers unarmed. Play normally. On the next campaign start the marker is renamed to `performance-v1.<id>.claimed`. The capture waits up to 120 wall seconds after claim for a running map, then collects for up to 120 wall seconds. Pause/off-map time after collection starts does not extend the deadline. The output to return for review is **`performance-v1.<id>.log`** in that folder. A second load does not rearm it. If the application stops invoking callbacks, the file simply ends at the last written summary until callbacks resume or campaign shutdown runs. No manual commands or in-play interaction are necessary.

## Reading the result

- The signal is **elapsed wall time between entries to this mod's `MBSubModuleBase.OnApplicationTick` callback**. In the installed game, `Module.OnApplicationTick` calls the game manager's update before it calls the submodules. A callback boundary is therefore not the beginning of a renderer frame. Rows do **not** report renderer FPS, GPU time, or a precise inside-engine phase. Slow intervals can include native simulation jobs, other mods, render waits, operating-system scheduling, GC, and untimed MAIO work.
- Nominal windows are five wall seconds, flushed on the next callback. Long gaps aren't backfilled. Each window records count, mean, maximum, counts at about 33/50/100 ms or more, the slowest interval's index and start campaign hour, and the work measured **between those exact same callbacks**. The work columns are `calls / total ms / peak per-interval ms / ms on the identified slowest interval`. Peaks for distinct work categories may occur in different intervals. A zero/absent last column means **no measured work**, not proof of zero total mod or engine work.
- `reliefInclusive` includes discovery, reviews and allocation, with `reliefPlanSubset` measuring the allocation calls within it. Siege reviews and scheduled planning have separate scopes. `reportBuild` includes force snapshot enumeration, formatting and concatenation for reports sent via the batch path; `reportAppend` is the batch append. `logAppend` covers the ordinary single-line append call separately. The acceptance category covers active observer tick/frame and coordination evidence collection when those actually run; it is absent from ordinary play. Appends and some observer work are **nested** in other scopes. Do not add these durations, subtract them from the callback interval as exclusive categories, or infer causation from a match alone.
- In the sliced offensive build `siegePlan` still covers the scheduled offensive planning work (now after the application boundary, so it belongs to the interval starting at that boundary). `siegeCommitSubset` shows the separate commit callback's validation and native mutations **within**, not in addition to, `siegePlan`. See [the first live replay and comparison](LiveOffenseReview.md).
- First boundary establishes an anchor; no preceding interval is invented. Stable running-map intervals are sampled; transitions between normal/fast or to/from pause, menus/encounters/off-map and loading are excluded when visible at the boundaries. Save queue state and save-start/end notifications flag save intervals. Unclassified gaps of 2 s or more are counted and the largest exclusion is reported, but are not scored as normal ticks. This can omit a real long stall, or fail to mark a brief interruption not exposed by the engine. Mixed exclusion reasons count once in priority order shown by the code. The interval after each capture append (including the header) is excluded as capture-write contamination; the next summary shows the preceding capture append call's elapsed time. Final unfinished intervals are not assigned work; a hard exit can leave a partial file.

The capture uses fixed arrays and two timestamps per measured main-thread scope, not timers per party/model query. When unarmed there is one marker check at campaign start, a null check on each application callback, and default disposable timing tokens at instrumented scope boundaries—no timestamp, counter, formatting, or disk write for profiling. Headless tests exercise real report/event timing scopes with the Desktop append intercepted and controlled wall segments for alignment, zero-work stalls, pause/save/gap/capture-write exclusions, bounded row count, opt-in, and expiry. These are instrumentation checks, not a live overhead or FPS measurement.

## Two-arrow relief replay

On the pre-fix run `0d5fc7bbc38a4bd6b398ef43f5d61b5c` (campaign log session `32365c3f`), windows labeled fast contained roughly 72 sampled seconds, with 67 intervals at least 50 ms and 15 at least 100 ms. In six successive windows around campaign hours 2203069–2203095, relief allocation accounted for 60–106 ms of each window's slowest interval (69–112 ms wall time). The separate 158 ms interval at 2203038.72 had no timed relief, siege, or report work; this fix doesn't explain that stall. Window counts are aggregate and only the slowest interval gets exact work attribution.

The relief planner now avoids retrying an unchanged free-party quote and skips replacement searches when no army can be released. It reuses native route/price quotes only before any orders or spending in the same objective pass, skips escort routes when no leader can make the trip, and refreshes after an order attempt that might have mutated the world. The headless no-op fixture checks route counts and that a route becoming viable on the next review dispatches the expected roster with the normal debit. Replay the same starting save at two arrows, ideally through the same active sieges. Compare slow intervals per active minute and `reliefPlanSubset` peaks, not just the overall maximum. Separately check relief choices and dispatch timing against the campaign log.
