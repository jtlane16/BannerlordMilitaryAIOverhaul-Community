using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Command
{
    // One explicit friendly land march; no discovery of live objectives.
    internal static partial class KingdomCommander
    {
        internal sealed class Candidate
        {
            internal readonly MobileParty Party;
            internal readonly double Hours;
            internal readonly float Strength;
            internal readonly int Cost;

            internal Candidate(MobileParty party, double hours, float strength, int cost = 0)
            {
                Party = party;
                Hours = hours;
                Strength = strength;
                Cost = cost;
            }
        }

        internal sealed class Plan
        {
            internal readonly Settlement Muster;
            internal readonly Candidate Leader;
            internal readonly Candidate[] Calls;
            internal readonly double MarchHours;
            internal double ArrivalHours => Calls.Aggregate(Leader.Hours, (hours, call) => Math.Max(hours, call.Hours));
            internal int Cost => Calls.Sum(call => call.Cost);

            internal Plan(Settlement muster, Candidate leader, Candidate[] calls, double marchHours)
            {
                Muster = muster;
                Leader = leader;
                Calls = calls;
                MarchHours = marchHours;
            }
        }

        internal enum MusterDecision { Wait, Depart, Cancel }

        // Acceptance looks only at the nominated frontier fort. The manual
        // planner still needs a separate muster point and a timed friendly march.
        internal sealed class FrontierSearch
        {
            internal readonly Action<string> Count;
            internal readonly Func<MobileParty, double> LatestArrival;
            internal readonly Func<MobileParty, float, bool> StrongEnough;

            internal FrontierSearch(Action<string> count, Func<MobileParty, double> latestArrival,
                Func<MobileParty, float, bool> strongEnough) =>
                (Count, LatestArrival, StrongEnough) = (count, latestArrival, strongEnough);
        }

        internal static bool OpenFortification(Settlement? settlement, Kingdom kingdom) =>
            settlement != null && settlement.IsFortification && settlement.MapFaction == kingdom && !settlement.IsUnderSiege &&
            settlement.Party.MapEvent == null;

        internal static string? NewSiegeBlocker(Army army, Kingdom kingdom, Settlement target, float present, out double hours, out float score)
        {
            hours = double.PositiveInfinity;
            score = 0f;
            if (!MilitaryObjective.EnemyFortification(target, kingdom))
                return "not-an-enemy-fortification-at-war";
            if (target.IsUnderSiege || target.Party.MapEvent != null)
                return "target-already-engaged";
            if (SiegeFoothold(kingdom, target, out _) == null)
                return OutsideSiegeFrontier;
            hours = HoursTo(army.LeaderParty, target);
            if (double.IsInfinity(hours) || double.IsNaN(hours))
                return "no-land-route";
            if (!(present > 0f))
                return "no-present-fighting-strength";
            score = SiegeScore(army.LeaderParty, target, present);
            return score > 0f && !float.IsInfinity(score) ? null : "native-new-siege-strength-check";
        }

        internal static string? FollowOnBlocker(Army army, Kingdom kingdom, Settlement origin, Settlement target, float present,
            out double hours, out float score)
        {
            hours = double.PositiveInfinity;
            score = 0f;
            if (target == origin || !origin.IsFortification ||
                !origin.Town.GetNeighborFortifications(MobileParty.NavigationType.All).Contains(target))
                return "not-an-immediate-fortification-neighbor";
            if (SiegeWarStance.BlocksNewTarget(kingdom, target.MapFaction))
                return "defensive-war-stance";
            var defense = HigherPriorityDefense(kingdom, army.LeaderParty);
            if (defense != null)
                return $"higher-priority-defense({defense.StringId})";
            return NewSiegeBlocker(army, kingdom, target, present, out hours, out score);
        }

        // Conservative veto for a controlled offensive follow-up, not relief
        // allocation or a claim that an existing response covers the siege.
        internal static float SiegeScore(MobileParty leader, Settlement target, float present) =>
            NativeCampaign.Current.Models.TargetScoreCalculatingModel.GetTargetScoreForFaction(
                target, Army.ArmyTypes.Besieger, leader, present);

        internal static Settlement? HigherPriorityDefense(Kingdom kingdom, MobileParty leader)
        {
            bool Qualifies(Town fief) => fief.Settlement.SiegeEvent?.BesiegerCamp.MapFaction is IFaction attacker &&
                kingdom.IsAtWarWith(attacker) && !double.IsPositiveInfinity(HoursTo(leader, fief.Settlement));
            var home = kingdom.Fiefs.FirstOrDefault(Qualifies);
            if (home != null)
                return home.Settlement;
            var alliances = NativeCampaign.Current.GetCampaignBehavior<IAllianceCampaignBehavior>();
            return alliances == null ? null : Kingdom.All.Where(ally => ally != kingdom && !ally.IsEliminated &&
                alliances.IsAllyWithKingdom(kingdom, ally)).SelectMany(ally => ally.Fiefs).FirstOrDefault(Qualifies)?.Settlement;
        }

        internal static Plan? Preflight(Kingdom kingdom, Settlement target, float required, double now, double deadline, CampaignLog? log,
            FrontierSearch? search = null, KingdomForces? inventory = null)
        {
            var free = new List<KingdomForces.PartyForce>();
            foreach (var row in (inventory ?? KingdomForces.Read(kingdom)).Parties)
            {
                string? reason = row.Exclusion ?? (row.Army != null ? "already-committed" : null);
                if (reason == null)
                    free.Add(row);
                else
                    log?.Event($"Muster preflight excluded party={row.Party.StringId} reason={reason}.");
            }

            var leaderVetoes = search == null ? null : free.ToDictionary(row => row.Party,
                row => ArmyOrders.FreePartyBlocker(row.Party, kingdom, asLeader: true));
            if (search != null)
            {
                foreach (var veto in leaderVetoes!.Values)
                    search.Count(veto == null ? "eligibleLeaderChecks" : veto == "native-command-role" ? "leaderRole" : "leaderEligibility");
                if (!leaderVetoes.Values.Any(veto => veto == null))
                {
                    search.Count("noEligibleLeader");
                    return null;
                }
            }
            var sites = search != null ? new[] { (site: target, distance: 0d) } : kingdom.Fiefs.Select(fief => fief.Settlement)
                .Where(site => site != target && OpenFortification(site, kingdom))
                .Select(site => (site, distance: LandDistance(site.GatePosition, target.GatePosition)))
                .OrderBy(item => item.distance).ThenBy(item => item.site.StringId, StringComparer.Ordinal).ToArray();
            var memberVetoes = new Dictionary<MobileParty, string?>();
            var callTerms = new Dictionary<(MobileParty, MobileParty), int?>();
            Plan? best = null;
            foreach (var (site, distance) in sites)
            {
                if (double.IsPositiveInfinity(distance))
                {
                    log?.Event($"Muster preflight rejected muster={site.StringId} reason=no-land-route-to-target.");
                    continue;
                }
                var arrivals = free.Select(row => new Candidate(row.Party, HoursTo(row.Party, site), row.Strength))
                    .OrderBy(row => search != null && row.Party.CurrentSettlement == site ? 0 : 1)
                    .ThenBy(row => row.Hours).ThenByDescending(row => row.Strength)
                    .ThenBy(row => row.Party.StringId, StringComparer.Ordinal).ToArray();
                foreach (var leader in arrivals)
                {
                    var veto = leaderVetoes == null ? ArmyOrders.FreePartyBlocker(leader.Party, kingdom, asLeader: true) : leaderVetoes[leader.Party];
                    if (search != null && veto != null)
                        continue;
                    double march = TravelHours(distance, leader.Party.Speed);
                    double latest = Math.Min(deadline, search?.LatestArrival(leader.Party) ?? deadline) - march;
                    if (veto != null || !Fits(now, latest, leader.Hours) ||
                        (search != null && latest <= now && leader.Party.CurrentSettlement != site))
                    {
                        if (!double.IsNegativeInfinity(latest))
                            search?.Count("leaderFrontierTimeOrRoute");
                        log?.Event($"Muster preflight rejected muster={site.StringId} leader={leader.Party.StringId} " +
                            $"reason={veto ?? "leader-route-or-ETA"} leaderETA={CampaignLog.Number(now + leader.Hours)} latestDeparture={CampaignLog.Number(latest)}.");
                        continue;
                    }

                    var payer = leader.Party.LeaderHero.Clan;
                    var calls = new List<Candidate>();
                    var skipped = new List<string>();
                    float committed = leader.Strength;
                    int cost = 0;
                    // Acceptance evaluates the affordable roster once, then
                    // shortens it if a smaller arrival prefix is still feasible.
                    bool feasible = search == null && committed >= required;
                    foreach (var member in arrivals)
                    {
                        if (feasible)
                            break;
                        if (member == leader)
                            continue;
                        if (search == null || !memberVetoes.TryGetValue(member.Party, out _))
                            memberVetoes[member.Party] = ArmyOrders.FreePartyBlocker(member.Party, kingdom, asLeader: false);
                        var reason = memberVetoes[member.Party] ?? (!Fits(now, latest, member.Hours) ||
                            (search != null && latest <= now && member.Party.CurrentSettlement != site) ? "route-or-ETA" : null);
                        int price = 0;
                        if (reason == null)
                        {
                            var key = (leader.Party, member.Party);
                            if (search == null || !callTerms.TryGetValue(key, out _))
                                callTerms[key] = WithinCallRange(leader.Party, member.Party) ? ArmyOrders.CallCost(leader.Party, member.Party) : (int?)null;
                            if (callTerms[key] is int quote)
                                price = quote;
                            else
                                reason = "outside-native-call-range";
                        }
                        if (reason == null && (price < 0 || !ArmyOrders.Affordable(payer.Influence, cost + price)))
                            reason = $"reserve(price={price})";
                        if (reason != null)
                        {
                            if (log != null)
                                skipped.Add($"{member.Party.StringId}:{reason}:eta={CampaignLog.Number(now + member.Hours)}");
                            search?.Count(reason.StartsWith("reserve", StringComparison.Ordinal) ? "reserve" :
                                reason == "route-or-ETA" ? "memberTime" :
                                reason == "outside-native-call-range" ? "callRange" : "memberEligibility");
                            continue;
                        }
                        calls.Add(new Candidate(member.Party, member.Hours, member.Strength, price));
                        committed += member.Strength;
                        cost += price;
                        feasible = search == null && committed >= required;
                    }
                    if (search != null)
                    {
                        feasible = search.StrongEnough(leader.Party, committed);
                        if (!feasible)
                            search.Count("nativeStrength");
                        else
                            while (calls.Count > 0 && search.StrongEnough(leader.Party, committed - calls[calls.Count - 1].Strength))
                            {
                                var last = calls[calls.Count - 1];
                                committed -= last.Strength;
                                cost -= last.Cost;
                                calls.RemoveAt(calls.Count - 1);
                            }
                    }
                    if (feasible && ArmyOrders.VisitLock(leader.Party) is string movementLock)
                    {
                        search?.Count("leaderMovementLock");
                        log?.Event($"Muster preflight rejected muster={site.StringId} leader={leader.Party.StringId} reason={movementLock}.");
                        continue;
                    }
                    if (feasible && !ArmyOrders.CanPathVisit(leader.Party, site))
                    {
                        search?.Count("nativeMusterPath");
                        log?.Event($"Muster preflight rejected muster={site.StringId} leader={leader.Party.StringId} reason=native-muster-path-unreachable.");
                        continue;
                    }
                    log?.Event($"Muster preflight {(feasible ? "selected" : "insufficient")}: muster={site.StringId} " +
                        $"leader={leader.Party.StringId} payer={payer.StringId} influence={CampaignLog.Number(payer.Influence)} " +
                        $"budget={CampaignLog.Number(Math.Max(0f, payer.Influence - ArmyOrders.InfluenceReserve))} " +
                        $"combinedCost={cost} required={CampaignLog.Number(required)} committed={CampaignLog.Number(committed)} " +
                        $"leaderETA={CampaignLog.Number(now + leader.Hours)} routeDistance={CampaignLog.Number(distance)} " +
                        $"marchHours={CampaignLog.Number(march)} latestDeparture={CampaignLog.Number(latest)} " +
                        $"calls=[{string.Join(", ", calls.Select(row => $"{row.Party.StringId}:power={CampaignLog.Number(row.Strength)}:eta={CampaignLog.Number(now + row.Hours)}:cost={row.Cost}"))}] " +
                        $"skipped=[{string.Join(", ", skipped)}].");
                    if (feasible)
                    {
                        var plan = new Plan(site, leader, calls.ToArray(), march);
                        if (search == null)
                            return plan;
                        if (best == null || plan.ArrivalHours < best.ArrivalHours ||
                            (plan.ArrivalHours == best.ArrivalHours && plan.Leader.Hours == 0 && best.Leader.Hours != 0) ||
                            (plan.ArrivalHours == best.ArrivalHours && plan.Leader.Hours == best.Leader.Hours && plan.Cost < best.Cost))
                            best = plan;
                    }
                }
            }
            return best;
        }

        internal static MusterDecision CheckMuster(float required, float present, bool atMuster, double now,
            double deadline, double marchHours, double leaderHours, IEnumerable<(float strength, double hours)> traveling)
        {
            double latest = deadline - marchHours;
            if (!Fits(now, latest, leaderHours))
                return MusterDecision.Cancel;
            if (atMuster && present >= required && now <= latest)
                return MusterDecision.Depart;
            if (now >= latest)
                return MusterDecision.Cancel; // A zero ETA is not an observed arrival.
            float possible = present + traveling.Where(row => Fits(now, latest, row.hours)).Sum(row => row.strength);
            return possible >= required ? MusterDecision.Wait : MusterDecision.Cancel;
        }

        private static bool Fits(double now, double latest, double hours) =>
            !double.IsInfinity(hours) && !double.IsNaN(hours) && now + hours <= latest;

        internal static double DistanceTo(MobileParty party, Settlement target) => party.CurrentSettlement == target
            ? 0 : party.IsCurrentlyAtSea || party.IsTransitionInProgress || !party.HasLandNavigationCapability
                ? double.PositiveInfinity : LandDistance(Origin(party), target.GatePosition);

        internal static double HoursTo(MobileParty party, Settlement target) => TravelHours(DistanceTo(party, target), party.Speed);

        internal static CampaignVec2 Origin(MobileParty party) => party.CurrentSettlement?.GatePosition ?? party.Position;

        internal static bool WithinCallRange(MobileParty leader, MobileParty member) =>
            LandDistance(Origin(leader), Origin(member)) < NativeCampaign.Current.Models.ArmyManagementCalculationModel.MaximumDistanceToCallToArmy;

        internal static double LandDistance(CampaignVec2 from, CampaignVec2 to, bool unrestricted = false)
        {
            if (!from.IsOnLand || !to.IsOnLand)
                return double.PositiveInfinity;
            var a = from.Face;
            var b = to.Face;
            if (!a.IsValid() || !b.IsValid())
                return double.PositiveInfinity;
            var campaign = NativeCampaign.Current;
            var models = campaign.Models;
            var excluded = models.PartyNavigationModel.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.Default);
            bool found = campaign.MapSceneWrapper.GetPathDistanceBetweenAIFaces(a, b, from.ToVec2(), to.ToVec2(),
                0.3f, unrestricted ? float.MaxValue : NativeCampaign.MapDiagonal * 5f, out float distance, excluded,
                models.MapDistanceModel.RegionSwitchCostFromLandToSea, models.MapDistanceModel.RegionSwitchCostFromSeaToLand);
            return found && distance >= 0f && !float.IsInfinity(distance) ? distance : double.PositiveInfinity;
        }

        internal static double TravelHours(double distance, float speed) =>
            distance == 0d ? 0d : speed > 0f && !float.IsInfinity(speed) ? distance / speed : double.PositiveInfinity;
    }
}
