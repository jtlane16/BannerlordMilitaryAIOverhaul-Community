using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Campaign;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Patches
{
    internal static class NativeArmyControlPatches
    {
        private static readonly MethodInfo FindMuster =
            AccessTools.DeclaredMethod(typeof(Army), "FindBestGatheringSettlementAndMoveTheLeader");
        private static readonly Action<Army, Settlement> NativeRetarget =
            AccessTools.MethodDelegate<Action<Army, Settlement>>(FindMuster);

        // Swap an individual call while preserving the existing stack and the rest
        // of the native method. A changed call shape must not silently leave a leak.
        internal static List<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> il, MethodInfo original, MethodInfo replacement)
        {
            var code = il.ToList();
            var call = code.Single(instruction => instruction.Calls(original));
            call.opcode = OpCodes.Call;
            call.operand = replacement;
            return code;
        }

        private static MethodInfo Hook(string name) => AccessTools.DeclaredMethod(typeof(NativeArmyControlPatches), name);

        [HarmonyPatch(typeof(Army), "OnAddPartyInternal", typeof(MobileParty))]
        private static class GuardCommanderMembershipDebit
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il)
            {
                var code = ReplaceCall(il, AccessTools.DeclaredMethod(typeof(ChangeClanInfluenceAction), nameof(ChangeClanInfluenceAction.Apply)),
                    AccessTools.DeclaredMethod(typeof(ArmyOrders), nameof(ArmyOrders.ApplyMembershipInfluence)));
                int index = code.FindIndex(instruction => instruction.Calls(
                    AccessTools.DeclaredMethod(typeof(ArmyOrders), nameof(ArmyOrders.ApplyMembershipInfluence))));
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                first.labels.AddRange(code[index].labels);
                code[index].labels.Clear();
                code.Insert(index, first);
                code.Insert(index + 1, new CodeInstruction(OpCodes.Ldarg_1));
                return code;
            }
        }

        [HarmonyPatch(typeof(Army), "ThinkAboutCohesionBoost")]
        private static class AutonomousBoost
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il) =>
                ReplaceCall(il, AccessTools.DeclaredMethod(typeof(Army), nameof(Army.BoostCohesionWithInfluence)),
                    Hook(nameof(BlockBoost)));
        }

        private static void BlockBoost(Army army, float gain, int cost)
        {
            var log = CampaignLog.Current;
            if (log == null || !ArmyTakeoverBehavior.IsAiLed(army))
            {
                army.BoostCohesionWithInfluence(gain, cost);
                return;
            }

            var clan = army.LeaderParty.LeaderHero?.Clan;
            var id = CampaignLog.ArmyId(army);
            if (log.Changed("boost", id, $"clan={clan?.StringId} cost={cost}"))
                log.Event($"Blocked native cohesion boost after eligibility/roll: {CampaignLog.ArmyState(army)} " +
                    $"clan={clan?.StringId ?? "-"} influence={(clan == null ? "-" : CampaignLog.Number(clan.Influence))} " +
                    $"cost={cost} proposedGain={CampaignLog.Number(gain)}; actualPayment=0 actualBoost=0.");
        }

        [HarmonyPatch(typeof(Army), "MoveLeaderToGatheringLocationIfNeeded")]
        private static class AutonomousMusterMovement
        {
            private static bool Prefix(Army __instance)
            {
                var log = CampaignLog.Current;
                if (log == null || !ArmyTakeoverBehavior.IsAiLed(__instance))
                    return true;

                // This is the native entry condition for making a new muster move.
                var leader = __instance.LeaderParty;
                if (__instance.AiBehaviorObject is Settlement muster && leader.SiegeEvent == null &&
                    leader.MapEvent == null && leader.ShortTermBehavior == AiBehavior.Hold && __instance.IsWaitingForArmyMembers())
                    LogMusterBlock(log, __instance, muster, "hourly muster movement");
                return false;
            }
        }

        [HarmonyPatch]
        private static class AutonomousMusterRetarget
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(Army), "OnSiegeStarted");
                yield return AccessTools.DeclaredMethod(typeof(Army), "OnSettlementOwnerChanged");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il) =>
                ReplaceCall(il, FindMuster, Hook(nameof(BlockMusterRetarget)));
        }

        private static void BlockMusterRetarget(Army army, Settlement focus)
        {
            var log = CampaignLog.Current;
            if (log != null && ArmyTakeoverBehavior.IsAiLed(army))
                LogMusterBlock(log, army, focus, "muster was besieged or changed ownership");
            else
                NativeRetarget(army, focus);
        }

        private static void LogMusterBlock(CampaignLog log, Army army, Settlement muster, string trigger)
        {
            var condition = $"muster={muster.StringId} siege={muster.IsUnderSiege} raid={muster.IsUnderRaid}";
            if (log.Changed(trigger, CampaignLog.ArmyId(army), condition))
                log.Event($"Blocked native gathering order: {CampaignLog.ArmyState(army)} trigger={trigger} {condition}; no new muster or move.");
        }

        [HarmonyPatch(typeof(Army), "CheckInactivity")]
        private static class AutonomousInactivityDisband
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il) =>
                ReplaceCall(il, AccessTools.DeclaredMethod(typeof(DisbandArmyAction), nameof(DisbandArmyAction.ApplyByInactivity)),
                    Hook(nameof(BlockInactivityDisband)));
        }

        private static void BlockInactivityDisband(Army army)
        {
            var log = CampaignLog.Current;
            if (log == null || !ArmyTakeoverBehavior.IsAiLed(army))
                DisbandArmyAction.ApplyByInactivity(army);
            else if (army.Kingdom != null && log.Changed("disband-inactivity", CampaignLog.ArmyId(army), "blocked"))
                log.Event($"Blocked discretionary native disband: {CampaignLog.ArmyState(army)} reason=inactivity; army retained.");
        }

        [HarmonyPatch(typeof(Army), "CheckArmyDispersion")]
        private static class AutonomousNoActiveWarDisband
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il, ILGenerator generator)
            {
                var code = il.ToList();
                var original = AccessTools.DeclaredMethod(typeof(DisbandArmyAction), nameof(DisbandArmyAction.ApplyByNoActiveWar));
                var call = code.Single(instruction => instruction.Calls(original));
                int index = code.IndexOf(call);
                if (code[index + 1].opcode != OpCodes.Ret || code[index + 2].opcode != OpCodes.Ldarg_0 ||
                    !code[index + 3].Calls(AccessTools.PropertyGetter(typeof(Army), nameof(Army.Cohesion))))
                    throw new InvalidOperationException("Expected no-active-war disband and return before the native cohesion check.");

                call.opcode = OpCodes.Call;
                call.operand = Hook(nameof(TryNoActiveWarDisband));
                // Return only if vanilla actually disbanded. Otherwise resume at
                // the native cohesion and inactive-leader checks, not the method exit.
                Label resume = generator.DefineLabel();
                code[index + 2].labels.Add(resume);
                code[index + 1].opcode = OpCodes.Brfalse;
                code[index + 1].operand = resume;
                code.Insert(index + 2, new CodeInstruction(OpCodes.Ret));
                return code;
            }
        }

        private static bool TryNoActiveWarDisband(Army army)
        {
            var log = CampaignLog.Current;
            if (log == null || !ArmyTakeoverBehavior.IsAiLed(army))
            {
                DisbandArmyAction.ApplyByNoActiveWar(army);
                return true;
            }

            if (log.Changed("disband-no-active-war", CampaignLog.ArmyId(army), "blocked"))
                log.Event($"Blocked discretionary native disband: {CampaignLog.ArmyState(army)} " +
                    "reason=no-active-war (native roll found no enemy with fiefs); no dispersal from this decision; forced checks continue.");
            return false;
        }

        [HarmonyPatch(typeof(Army), "DisperseInternal", typeof(Army.ArmyDispersionReason))]
        private static class ObserveDispersal
        {
            private static void Prefix(Army __instance, out (string army, string parties)? __state)
            {
                __state = ArmyTakeoverBehavior.Current != null && !__instance.IsDispersing && __instance.Kingdom != null
                    ? (CampaignLog.ArmyState(__instance), ArmyTakeoverBehavior.DescribeMembers(__instance))
                    : null;
            }

            private static void Postfix(Army __instance, Army.ArmyDispersionReason __0, (string army, string parties)? __state)
            {
                if (__state is { } before)
                    ArmyTakeoverBehavior.Current?.OnDispersalReturned(__instance, __0, before.army, before.parties);
            }
        }
    }
}
