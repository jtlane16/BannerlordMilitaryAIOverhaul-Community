# Allied-settlement relief acceptance

Load the disposable wartime save in the armed acceptance build and let world-map time run. Return the single **MAIO campaign acceptance** popup and its run ID. No console commands, target selection, or intermediate polling are needed.

A pass requires a **separate kingdom formally allied to the current fief owner and already at war with the actual besieger**, using its own production army. The army must have the native defend order and physically respond at the eligible camp. The result distinguishes local approach, waiting, engagement, and battle from saving the settlement. The owner's home response and the test attacker's camp cannot pass.

The runner prefers an existing eligible siege. Otherwise it can use the proven controlled frontier-to-siege path against a real hostile owner when the owner, attacker, and eligible reliever form a valid triad. It does not change diplomacy or create a camp directly. Only the test-owned attacker is cleaned up, when native battles permit. No formal alliance/war triad or no reachable force can produce an honest `INCONCLUSIVE`; the timeline includes alliance/war, route, search, and production evidence.

## Coordinator

Stage the tested acceptance DLL with `acceptance-allied-relief-v1.armed` containing `allied-relief-v1 <fresh lowercase 32-character GUID>`. Use `tests/InstallAcceptance.ps1 -Scenario allied-relief-v1 -Package <directory> -ExpectedHash <sha256>` only when the game and launcher are closed. Keep prior claims, results and shown receipts. The limits are 360 campaign hours, 15 real minutes, and at most three controlled attack attempts inside one run. They do not apply to ordinary home or allied relief.

Ordinary builds have no runner or test offense. Relief uses land routes and new formations require a reachable, unbesieged home muster fort. Naval-only camps stay open and are reported as unsupported. The headless suite exercises the installed alliance query and native defend approach with scene probes stubbed; the disposable campaign is needed for live physical evidence.
