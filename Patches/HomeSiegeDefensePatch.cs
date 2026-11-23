using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Patches
{
    [HarmonyPatch(typeof(DefaultTargetScoreCalculatingModel), nameof(DefaultTargetScoreCalculatingModel.GetTargetScoreForFaction),
        typeof(Settlement), typeof(Army.ArmyTypes), typeof(MobileParty), typeof(float))]
    internal static class HomeSiegeDefensePatch
    {
        // From CAI's HomeSiegeDefensePatch. Branch around only the defender strength
        // calculation; both paths still use the native cap, distance, and target value.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var code = instructions.ToList();
            var max = AccessTools.Method(typeof(TaleWorlds.Library.MathF), "Max", new[] { typeof(float), typeof(float) });
            var pow = AccessTools.Method(typeof(TaleWorlds.Library.MathF), "Pow", new[] { typeof(float), typeof(float) });
            var matches = Enumerable.Range(0, Math.Max(0, code.Count - 39)).Where(i =>
                code[i].opcode == OpCodes.Ldarg_S && code[i].operand is byte arg && arg == 4 &&
                code[i + 1].opcode == OpCodes.Ldloc_S && code[i + 1].operand is LocalBuilder &&
                code[i + 2].opcode == OpCodes.Add && code[i + 3].LoadsConstant(100f) &&
                code[i + 4].opcode == OpCodes.Ldloc_S && code[i + 4].operand is LocalBuilder &&
                code[i + 5].Calls(max) && code[i + 6].LoadsConstant(1.1f) &&
                code[i + 9].opcode == OpCodes.Dup && code[i + 11].opcode == OpCodes.Div &&
                code[i + 12].IsStloc() && code[i + 16].LoadsConstant(0.9f) &&
                code[i + 21].LoadsConstant(1.75f) && code[i + 25].LoadsConstant(0.25f) &&
                (code[i + 30].opcode == OpCodes.Bge_Un || code[i + 30].opcode == OpCodes.Bge_Un_S) &&
                code[i + 30].operand is Label && code[i + 35].LoadsConstant(0.25f) &&
                code[i + 36].Calls(pow) && code[i + 38].opcode == code[i + 12].opcode &&
                Equals(code[i + 38].operand, code[i + 12].operand) &&
                (code[i + 39].opcode == OpCodes.Br || code[i + 39].opcode == OpCodes.Br_S)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Could not uniquely locate the native defender strength calculation.");

            int start = matches[0];
            var own = code[start];
            var reinforcements = code[start + 1];
            var attacker = code[start + 4];
            var score = code[start + 12];
            var vanilla = il.DefineLabel();
            var entry = new CodeInstruction(OpCodes.Ldarg_1);
            entry.labels.AddRange(own.labels);
            own.labels.Clear();
            own.labels.Add(vanilla);
            code.InsertRange(start, new[]
            {
                entry,
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(attacker.opcode, attacker.operand),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(HomeSiegeDefensePatch), nameof(IsIndependentHomeDefense))),
                new CodeInstruction(OpCodes.Brfalse, vanilla),
                new CodeInstruction(own.opcode, own.operand),
                new CodeInstruction(reinforcements.opcode, reinforcements.operand),
                new CodeInstruction(attacker.opcode, attacker.operand),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(HomeSiegeDefensePatch), nameof(HomeStrengthFactor))),
                new CodeInstruction(score.opcode, score.operand),
                new CodeInstruction(OpCodes.Br, code[start + 30].operand)
            });
            return code;
        }

        private static bool IsIndependentHomeDefense(Settlement settlement, MobileParty party, float attackerStrength) =>
            attackerStrength > 0f && party is { IsMainParty: false, IsLordParty: true, Army: null, AttachedTo: null } &&
            settlement is { IsFortification: true, SiegeEvent: not null } &&
            party.MapFaction is Kingdom kingdom && settlement.MapFaction == kingdom;

        private static float HomeStrengthFactor(float own, float committed, float attacker)
        {
            float factor = Math.Max(1f, (own + committed) / attacker);
            if (committed > Math.Max(100f, attacker) * 1.1f * 1.75f)
                factor *= 0.25f;
            return factor;
        }
    }
}
