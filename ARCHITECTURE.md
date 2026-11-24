# Project layout

Name code for the job it does, not the stage that introduced it. Stages in [SPEC.md](SPEC.md) are build and test milestones. They are not folders, namespaces, types, or owners of campaign state. Keep one mod project and one shipped DLL.

## Current source layout

| Source | Responsibility |
| --- | --- |
| `SubModule.cs` | Bannerlord entry point. Install hooks, register campaign behaviors, and open and close the campaign log. |
| `ModSettings.cs` | Read player-tunable values from `_Module/Settings.xml` at campaign start. A missing file or invalid value keeps the built-in default. |
| `Campaign/ArmyTakeoverBehavior.cs` | Release inherited AI armies once. Own the pending releases, native event callbacks, and save/load reconciliation. |
| `Campaign/MilitaryCommandBehavior.cs` | Schedule the daily force report, expose `maio.muster` for campaign testing, and track the active test assignment from muster to release. Persist owned movement recovery and deferred releases, not a copy of the kingdom roster. |
| `Command/KingdomForces.cs` | Build one kingdom's inventory from current parties and army membership. Distinguish present from traveling strength, and observed troops from troops the commander could assign. |
| `Command/KingdomCommander.cs` | For the fixed friendly march, select a reachable muster point, leader, and affordable summons; decide whether to wait, depart, or cancel from actual arrivals and the deadline. |
| `Armies/ArmyOrders.cs` | Execute formation, summons, movement, and release through native actions. Check the paying clan's reserve at the actual native debit and leave battle participants alone until they can be released safely. |
| `Patches/PartyOrderSelectionPatch.cs` | Filter native formation and AI army-leader order proposals at the point of selection. Preserve independent-party and player-led behavior. |
| `Patches/HomeSiegeDefensePatch.cs` | Adjust the native defender strength score for an independent AI lord considering a besieged home fortification. The native party AI selects and executes the order. |
| `Patches/AlliedSettlementDefensePatch.cs` | Add eligible allied settlements to that lord's native defense candidates when home fortifications are clear. |
| `Patches/NativeArmyControlPatches.cs` | Intercept autonomous muster, boost, and discretionary disband decisions; observe native dispersal and guard the native membership charge for commander summons. |
| `Diagnostics/CampaignLog.cs` | Write the single Desktop campaign log, format identifiers, and suppress repeated entries. The code that made a decision supplies its reason. |
| `tests/` | Regression fixtures and `RunSmoke.ps1`. Test code builds separately and is not shipped in the mod DLL. |

Use the destination filename for the main type and the folder for its namespace under `BannerlordMilitaryAIOverhaul`. Keep `BannerlordMilitaryAIOverhaul.SubModule` as the launcher entry point. `CampaignLog` is shared across the campaign; later features must not depend on `ArmyTakeoverBehavior` to write a log entry or to check if a takeover is pending.

## Where the next features belong

These are planned homes, not files to create up front. Add each when it has working behavior and a test.

| File | Owns | Does not own |
| --- | --- | --- |
| `Campaign/MilitaryCommandBehavior.cs` (extend when needed) | Schedule kingdom reviews and use `SyncData` for durable commander state such as assignments and failed-siege retry dates. | Ranking targets or issuing orders inside event callbacks. |
| `Command/KingdomCommander.cs` (extend when live objectives are introduced) | Decide which objectives the kingdom pursues and which forces cover them. One kingdom can have several objectives; several armies can share one objective. | Calling Harmony, controlling independent parties, or moving native parties directly. |
| `Command/MilitaryObjective.cs` | Identify the job: home relief, allied relief, or an offensive siege, and its target. Keep the current army assignments distinct from the objective so the job can survive an army's loss. | One subclass per priority or one commander per army. |
| `Armies/ArmyOrders.cs` (extend as orders are introduced) | Issue defend, besiege, boost, and withdraw orders for live objectives. Check current eligibility and spending limits before acting. | Picking the next target or reproducing Bannerlord's short-term waiting, engagement, and assault behavior. |

For example, when a home town comes under siege, `MilitaryCommandBehavior` requests a kingdom review. `KingdomForces` reports what is available. `KingdomCommander` assigns one or more armies to the town. `ArmyOrders` gives each ready leader the native defend order. A Harmony patch never makes that strategic choice. Home and allied relief use the same assignment and execution code; eligibility and priority differ.

## Refactor acceptance before adding force accounting

1. Move and rename the existing source and type references. Keep the **existing serialized behavior ID** `BannerlordMilitaryAIOverhaul.Stage0Takeover` and keys `captured` and `pendingArmies`. The ID is an on-disk compatibility string, not the new class name. Renaming it could make a loaded campaign repeat its takeover. Preserve the current Harmony owner ID during this move for the same kind of external compatibility.
2. Keep the same eight native patch targets, launcher entry point, and Desktop log path. Do not bundle force accounting or change gameplay in this pass.
3. Update the smoke harness's reflection lookups for the new names. Keep repeatable test sources under `tests/`, separate from shipping code; run compiled fixtures locally. Verify the Release build, patch install/uninstall, takeover and natural dispersal cases. Check that a save written by the previous build reads its takeover state without a second snapshot.
4. Review the file list against this layout before delegation and again before accepting an implementation. A new file should have a concrete responsibility that does not already have a home here.
