using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Campaign;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BannerlordMilitaryAIOverhaul.Patches
{
    [HarmonyPatch(typeof(AiPartyThinkBehavior), "PartyHourlyAiTick", typeof(MobileParty))]
    internal static class PartyOrderSelectionPatch
    {
        private static readonly MBReadOnlyList<(AIBehaviorData, float)> NoScores = new MBList<(AIBehaviorData, float)>();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> il) =>
            NativeArmyControlPatches.ReplaceCall(il,
                AccessTools.PropertyGetter(typeof(PartyThinkParams), nameof(PartyThinkParams.AIBehaviorScores)),
                AccessTools.DeclaredMethod(typeof(PartyOrderSelectionPatch), nameof(SelectableScores)));

        private static MBReadOnlyList<(AIBehaviorData, float)> SelectableScores(PartyThinkParams think)
        {
            var scores = think.AIBehaviorScores;
            var party = think.MobilePartyOf;
            var log = CampaignLog.Current;
            if (log == null || party.IsMainParty)
                return scores;

            if (party.Army is Army army)
            {
                // The commander already issued the escort. Leave its short-term
                // movement/attachment to native AI, but don't select a new order
                // (which can also abandon an event) for a commander summons.
                if (army.LeaderParty != party)
                    return !army.LeaderParty.IsMainParty && MilitaryCommandBehavior.Owns(army) ? NoScores : scores;

                // No new long-term choice for an AI leader. Suppress it BEFORE the
                // selector can abandon an in-progress event or take a disband branch.
                var (choice, score) = BestScore(scores);
                var target = CampaignLog.PointId(choice.Party);
                string? decision = score > -1f ? $"{choice.AiBehavior}:{target}" : null;
                if (log.Changed("selection", party.StringId, decision) && decision != null)
                    log.Event($"Blocked native strategic selection: {CampaignLog.ArmyState(army)} " +
                        $"proposal={decision} score={CampaignLog.Number(score)}; no new order, discretionary disband or event abandonment committed.");
                return NoScores;
            }

            // Remove only formation proposals. The game still picks among all
            // normal orders in their original order, using its normal rules.
            if (!scores.Any(entry => entry.Item1.WillGatherArmy))
                return scores;

            var standalone = new MBList<(AIBehaviorData, float)>();
            (AIBehaviorData order, float score)? formation = null;
            int filteredCount = 0;
            foreach (var entry in scores)
            {
                if (!entry.Item1.WillGatherArmy)
                {
                    standalone.Add(entry);
                    continue;
                }
                ++filteredCount;
                if (!formation.HasValue || float.IsNaN(formation.Value.score) || entry.Item2 > formation.Value.score)
                    formation = entry;
            }

            var (best, bestScore) = BestScore(scores);
            var (proposal, proposalScore) = formation.GetValueOrDefault();
            string rank = bestScore > -1f && best.WillGatherArmy ? "would-win-scoring" : "not-score-winner";
            string objective = DescribeOrder(proposal);
            // Report each rank separately so an alternating win/loss for the same
            // target doesn't generate a line on every selection.
            if (log.Changed("formation:" + rank, party.StringId, objective))
            {
                var (independent, independentScore) = BestScore(standalone);
                string fallback = independentScore > -1f
                    ? $"{DescribeOrder(independent)} score={CampaignLog.Number(independentScore)}"
                    : "none";
                var candidates = think.PossibleArmyMembersUponArmyCreation;
                var clan = party.LeaderHero?.Clan;
                log.Event($"Filtered vanilla formation candidate(s): kingdom={party.MapFaction?.StringId ?? "-"} " +
                    $"leader={party.StringId} count={filteredCount} topFormation={objective} score={CampaignLog.Number(proposalScore)} " +
                    $"rank={rank} topIndependentCandidate={fallback} " +
                    $"candidates=[{(candidates == null ? "" : string.Join(", ", candidates.Select(member => member.StringId)))}] " +
                    $"clan={clan?.StringId ?? "-"} influence={(clan == null ? "-" : CampaignLog.Number(clan.Influence))}; " +
                    "before commit/roll: no army, summons, or influence spend from these candidates; standalone candidate is not an issued order.");
            }
            return standalone;
        }

        private static string DescribeOrder(AIBehaviorData order) => $"{order.AiBehavior}:{CampaignLog.PointId(order.Party)}";

        private static (AIBehaviorData, float) BestScore(MBReadOnlyList<(AIBehaviorData, float)> scores)
        {
            var best = (AIBehaviorData.Invalid, -1f);
            foreach (var score in scores)
                if (score.Item2 > best.Item2)
                    best = score;
            return best;
        }
    }
}
