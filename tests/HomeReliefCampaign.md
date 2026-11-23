# Home-settlement relief acceptance

## Tester

On the armed build, load the disposable wartime save and leave world-map time running. Return the single **MAIO campaign acceptance** popup, including its run ID. No console commands, target choice, or save changes are needed.

`PASS` means a real hostile home siege was active and a production army had the matching native defend order **and physically responded** at that camp. The result distinguishes local approach, waiting, engagement, and battle. It does not claim the settlement was saved. A defend order, a muster, or the test attacker joining camp does not pass by itself.

The runner observes existing sieges first. If none is active, the acceptance build can use the proven frontier guard-to-siege path to create a real enemy camp in an existing war. Its test attacker has a separate fixture slot, so it need not cancel an existing manual order. Production defense is separate. Only the test-owned attacking assignment is cleaned up by the runner, after tactical protections allow it. If the popup says cleanup is deferred, the native battle must finish before the test army can be withdrawn. An unarmed ordinary build responds to real home sieges but never creates this test attack.

## Coordinator

Use `tests/RunSmoke.ps1 -CampaignAcceptance -PreviousModDll <baseline>` to verify the acceptance DLL and repeat without `-CampaignAcceptance` for the ordinary DLL. Stage the tested DLL plus `acceptance-relief-v1.armed` containing `relief-v1 <fresh 32-character lowercase GUID>`. Install with `tests/InstallAcceptance.ps1 -Scenario relief-v1 -Package <directory> -ExpectedHash <SHA-256>`. It refuses to deploy over a running game/launcher, a locked DLL, or another pending marker. Keep old receipts. Return to the ordinary DLL once acceptance is complete if desired.

To refresh a DLL before a run has been claimed, use `-KeepPendingArm` with an exact copy of the installed marker in the package. The installer rejects a different or second marker, or any receipt for that run ID; it does not rewrite the installed marker.

The run has a 360 campaign-hour / 15 wall-minute cap and at most three test-attack attempts. These limits do **not** apply to production relief. `INCONCLUSIVE` means the live run did not prove the scenario. Search counters, `Relief objective`, `Relief review`, `Relief departure`, and `Relief execution` lines explain what was available, committed, present, or blocked.

Production currently plans **land** responses. A naval-only camp is logged and left open; a land response requires native land paths, and new formations require a reachable unbesieged home fort. An unowned active army order and an army pending takeover are not ours to divert. The headless suite models the map and forces but uses native order, membership/debit, save-data, and release paths. Live movement and combat outcomes still require the disposable campaign.

A higher-priority siege can take over a safe lower-priority muster without rebuilding the army or re-buying its escorts. The existing muster fort is preferred if it still has a route; diversion waits for protected events. A battle-held traveling escort does not, by itself, block cohesion upkeep for the leader and attached formation.
