using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Patches
{
    // CAI's allied-defense candidates, appended to vanilla's standalone defender pass.
    // Reuse its already calculated strength, food and size rather than running a second think tick.
    [HarmonyPatch(typeof(AiMilitaryBehavior), "CalculateMilitaryBehaviorForFactionSettlements")]
    internal static class AlliedSettlementDefensePatch
    {
        private static readonly Func<AiMilitaryBehavior, Settlement, MobileParty, Army.ArmyTypes, bool, bool> Suitable =
            AccessTools.MethodDelegate<Func<AiMilitaryBehavior, Settlement, MobileParty, Army.ArmyTypes, bool, bool>>(
                AccessTools.DeclaredMethod(typeof(AiMilitaryBehavior), "CheckIfSettlementIsSuitableForMilitaryAction"));
        private static readonly Action<AiMilitaryBehavior, Settlement, Army.ArmyTypes, AiBehavior, PartyThinkParams, float, float, float, float> Score =
            AccessTools.MethodDelegate<Action<AiMilitaryBehavior, Settlement, Army.ArmyTypes, AiBehavior, PartyThinkParams, float, float, float, float>>(
                AccessTools.DeclaredMethod(typeof(AiMilitaryBehavior), "CalculateMilitaryBehaviorForSettlement"));

        private static void Postfix(AiMilitaryBehavior __instance, IFaction faction, PartyThinkParams p, Army.ArmyTypes missionType,
            AiBehavior aiBehavior, float ourStrength, float partySizeScore, float cohesionScore, float foodScore)
        {
            if (missionType != Army.ArmyTypes.Defender || p.WillGatherAnArmy ||
                faction is not Kingdom { IsEliminated: false } home || home.AlliedKingdoms.Count == 0)
                return;
            var party = p.MobilePartyOf;
            if (party is not { IsLordParty: true, IsMainParty: false, Army: null, AttachedTo: null, MapEvent: null, SiegeEvent: null } ||
                party.MapFaction != home)
                return;
            foreach (var fort in home.Fiefs)
                if (fort.Settlement.IsUnderSiege || fort.Settlement.IsUnderRaid)
                    return;

            foreach (var ally in home.AlliedKingdoms)
            {
                if (ally.IsEliminated || home.IsAtWarWith(ally))
                    continue;
                foreach (var target in ally.Settlements)
                {
                    if (target.MapFaction != ally)
                        continue;
                    var attacker = target.IsFortification && target.IsUnderSiege ? target.SiegeEvent.BesiegerCamp?.MapFaction :
                        target.IsVillage && target.IsUnderRaid ? target.Party.MapEvent.AttackerSide.LeaderParty?.MapFaction : null;
                    if (attacker != null && home.IsAtWarWith(attacker) && ally.IsAtWarWith(attacker) &&
                        Suitable(__instance, target, party, missionType, false))
                        Score(__instance, target, missionType, aiBehavior, p, ourStrength, partySizeScore, cohesionScore, foodScore);
                }
            }
        }
    }
}
