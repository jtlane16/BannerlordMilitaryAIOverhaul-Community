using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Command
{
    internal static partial class KingdomCommander
    {
        internal const double SiegeRecoveryMusterHours = 24d;

        internal const string OutsideSiegeFrontier = "outside-two-fief-frontier";

        // The native land fortification graph is undirected. Look back from the
        // target at most two edges; only current ownership establishes a foothold.
        internal static Settlement? SiegeFoothold(Kingdom kingdom, Settlement target, out int links)
        {
            links = 0;
            if (target.Town == null)
                return null;
            var firstRing = target.Town.GetNeighborFortifications(MobileParty.NavigationType.Default);
            foreach (var neighbor in firstRing)
                if (neighbor?.Town != null && neighbor.MapFaction == kingdom)
                {
                    links = 1;
                    return neighbor;
                }
            foreach (var neighbor in firstRing)
                if (neighbor?.Town != null)
                    foreach (var second in neighbor.Town.GetNeighborFortifications(MobileParty.NavigationType.Default))
                        if (second?.Town != null && second.MapFaction == kingdom)
                        {
                            links = 2;
                            return second;
                        }
            return null;
        }

        internal readonly struct FreshSiegeRules
        {
            internal readonly float Floor, PaidFloor;
            internal readonly string? PaidBlock;
            internal readonly bool ReadyMuster;
            internal FreshSiegeRules(float floor, float paidFloor, string? paidBlock, bool readyMuster) =>
                (Floor, PaidFloor, PaidBlock, ReadyMuster) = (floor, paidFloor, paidBlock, readyMuster);
            internal float RequiredPower(bool pays) => pays ? Math.Max(Floor, PaidFloor) : Floor;
        }

        internal sealed class SiegeChoice
        {
            internal readonly Settlement Target;
            internal readonly Army? Army;
            internal readonly Plan? Muster;
            internal readonly CampaignVec2 Origin;
            internal readonly double Distance;
            internal readonly float Value, Strength, Score, AffordablePrefixPower;
            internal SiegeChoice(Settlement target, Army? army, Plan? muster, double distance, float value, float strength, float score,
                float affordablePrefixPower, CampaignVec2 origin) =>
                (Target, Army, Muster, Distance, Value, Strength, Score, AffordablePrefixPower, Origin) =
                    (target, army, muster, distance, value, strength, score, affordablePrefixPower, origin);
        }

        internal static bool SiegeStrength(MobileParty leader, Settlement target, float power, out float score)
        {
            score = power > 0f ? SiegeScore(leader, target, power) : 0f;
            return score > 0f && !float.IsInfinity(score);
        }

        // Fresh formations must still pass the native gate with 10% less
        // power. This tests headroom without reimplementing vanilla's model.
        internal static float ConservativeSiegePower(float power) => power * 0.90f;

        // Retention probe only. For the default 2x new-siege gate this asks
        // whether actual power meets 1.6x, without duplicating adjusted defense.
        internal static float SiegeContinuationProbePower(float power) => power * 1.25f;

        internal static bool SiegeRoute(MobileParty leader, CampaignVec2 from, Settlement target) =>
            ArmyOrders.CanRouteToRelief(leader, from, target.GatePosition);

        internal static bool LiveSiegeRoute(MobileParty leader, CampaignVec2 from, Settlement target) =>
            LiveSiegeRoute(leader, from, target, out _);

        internal static bool LiveSiegeRoute(MobileParty leader, CampaignVec2 from, Settlement target, out double distance)
        {
            distance = LandDistance(from, target.GatePosition, unrestricted: true);
            return !double.IsInfinity(distance) && SiegeRoute(leader, from, target);
        }

        internal static SiegeChoice? ChooseSiege(Kingdom kingdom, IEnumerable<Settlement> targets,
            IEnumerable<Army> armies, KingdomForces? inventory, Func<Settlement, string?> blocked,
            Func<MobileParty, bool> reserved, Action<string> count, Action<SiegeChoice> viable,
            Func<MobileParty, Settlement, Settlement, bool> failed, Action<string>? farther = null,
            Func<Settlement, CampaignVec2?>? priorCamp = null)
        {
            SiegeChoice? best = null;
            foreach (var _ in SweepSiege(kingdom, targets, armies, inventory, blocked, reserved, count, viable, failed,
                choice => best = choice, farther, priorCamp)) { }
            return best;
        }

        // Each yield is a work boundary, NOT a campaign tick. Search results are
        // proposals: no authority or resources are reserved by this iterator.
        internal static IEnumerable<int> SweepSiege(Kingdom kingdom, IEnumerable<Settlement> targets,
            IEnumerable<Army> armies, KingdomForces? inventory, Func<Settlement, string?> blocked,
            Func<MobileParty, bool> reserved, Action<string> count, Action<SiegeChoice> viable,
            Func<MobileParty, Settlement, Settlement, bool> failed, Action<SiegeChoice?> complete,
            Action<string>? farther = null, Func<Settlement, CampaignVec2?>? priorCamp = null,
            Func<Settlement, (FreshSiegeRules rules, int work)>? freshSiegePolicy = null,
            Func<Settlement, IEnumerable<string>, bool>? paidRosterBlocked = null,
            Func<Army, Settlement, bool>? allowExisting = null)
        {
            var legal = targets.Distinct().Where(target =>
            {
                string? reason = blocked(target);
                if (reason != null)
                    count(reason);
                return reason == null;
            }).ToArray();
            if (legal.Length == 0)
            {
                complete(null);
                yield break;
            }

            var forces = armies.Select(army => (army, leader: LiveArmyLeader(army)))
                .Where(pair => pair.leader != null && !pair.leader.Party.IsStarving)
                .Select(pair => (pair.army, leader: pair.leader!, power: SiegeRoster(pair.army)))
                .Where(pair => pair.power > 0).ToArray();
            var free = inventory?.Parties.Where(row => row.Army == null && row.Exclusion == null &&
                    !row.Party.Party.IsStarving && !reserved(row.Party))
                .Select(row => (row.Party, power: row.Strength))
                .Where(row => row.power > 0).ToArray() ?? Array.Empty<(MobileParty Party, float power)>();
            var leaders = new HashSet<MobileParty>(free.Where(row =>
                ArmyOrders.FreePartyBlocker(row.Party, kingdom, true) == null && ArmyOrders.VisitLock(row.Party) == null).Select(row => row.Party));
            var members = new HashSet<MobileParty>(free.Where(row => ArmyOrders.FreePartyBlocker(row.Party, kingdom, false) == null).Select(row => row.Party));
            if (leaders.Count == 0 && forces.Length == 0)
            {
                count("no-available-formation-or-leader");
                complete(null);
                yield break;
            }
            var musters = leaders.Count == 0 ? Array.Empty<Settlement>() : kingdom.Fiefs.Select(fief => fief.Settlement)
                .Where(site => OpenFortification(site, kingdom)).ToArray();
            var sites = new List<(Settlement target, float value)>(legal.Length);
            foreach (var target in legal)
            {
                sites.Add((target, target.GetSettlementValueForFaction(kingdom)));
                yield return 1;
            }
            var origins = forces.Where(pair => LiveArmyLeader(pair.army) == pair.leader)
                .Select(pair => (army: (Army?)pair.army, leader: (MobileParty?)pair.leader, muster: (Settlement?)null, point: Origin(pair.leader), pair.power))
                .Concat(musters.Select(site => (army: (Army?)null, leader: (MobileParty?)null, muster: (Settlement?)site, point: site.GatePosition, power: 0f)));
            // Straight-line distance is only a lower bound for pruning native
            // route quotes. It never ranks two feasible targets.
            var pairs = origins.SelectMany(origin => sites.Select(site => (origin, site,
                lower: origin.point.DistanceSquared(site.target.GatePosition.ToVec2()))))
                .OrderBy(pair => pair.lower).ToArray();
            var roster = new Dictionary<Settlement, Candidate[]>();
            var callsByLeader = new Dictionary<(MobileParty, MobileParty), int?>();
            var affordable = new Dictionary<(Settlement, MobileParty, int), Candidate[]>();
            var visits = new Dictionary<(Settlement, MobileParty), bool>();
            var freshRules = new Dictionary<Settlement, FreshSiegeRules>();
            // The native API sees the actual party, not the hypothetical
            // muster location. Quotes for the same party/target/power are
            // proposals within this sweep, not live eligibility. In production
            // the sweep spans callbacks; no stored score can authorize an order.
            var strengthQuotes = new Dictionary<(MobileParty, Settlement, float), float>();
            SiegeChoice? best = null;
            const int ordinary = 0, readyRecovery = 1, freeOnly = 2;
            yield return 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                var (origin, site, lower) = pairs[i];
                if (best != null && lower > (best.Distance + 1d) * (best.Distance + 1d))
                {
                    if (farther != null)
                        foreach (var pair in pairs.Skip(i).Where(pair => pair.site.target != best.Target)
                            .GroupBy(pair => pair.site.target).Select(group => group.First()).Take(3))
                            farther($"{pair.site.target.StringId}:straightLowerBound={Math.Sqrt(pair.lower):0.##}:value={pair.site.value:0.##}");
                    break;
                }
                var target = site.target;
                double distance = LandDistance(origin.point, target.GatePosition, unrestricted: true);
                yield return 1;
                if (double.IsInfinity(distance))
                {
                    count("no-land-route");
                    continue;
                }
                if (best != null && distance > best.Distance)
                {
                    if (target != best.Target)
                        farther?.Invoke($"{target.StringId}:route={distance:0.##}:value={site.value:0.##}");
                    continue;
                }
                if (target.MapFaction == null || target.Town == null)
                {
                    count("stale-siege-target");
                    continue;
                }
                if (origin.army is Army army)
                {
                    var armyLeader = LiveArmyLeader(army);
                    if (armyLeader == null || armyLeader != origin.leader)
                    {
                        count("stale-siege-army");
                        continue;
                    }
                    if (allowExisting?.Invoke(army, target) == false)
                    {
                        count("idle-target-unchanged");
                        continue;
                    }
                    if (!QuoteStrength(armyLeader, target, SiegeContinuationProbePower(origin.power), out float score, out int work))
                    {
                        yield return work;
                        count("native-new-siege-strength-check");
                        continue;
                    }
                    yield return work;
                    if (LiveArmyLeader(army) != armyLeader)
                    {
                        count("stale-siege-army");
                        continue;
                    }
                    bool route = SiegeRoute(armyLeader, origin.point, target);
                    yield return 1;
                    if (!route)
                    {
                        count("no-native-path");
                        continue;
                    }
                    if (LiveArmyLeader(army) != armyLeader)
                    {
                        count("stale-siege-army");
                        continue;
                    }
                    float hostile = 0f;
                    foreach (var step in SweepSiegeThreat(armyLeader, target, priorCamp?.Invoke(target), value => hostile = value, includeObserver: true))
                        yield return step;
                    if (LiveArmyLeader(army) != armyLeader)
                        count("stale-siege-army");
                    else if (!Overwhelmed(hostile, SiegePresent(army, kingdom)))
                        Consider(new SiegeChoice(target, army, null, distance, site.value, origin.power, score, origin.power, origin.point));
                    else
                        count("visible-overwhelming-local-threat");
                    continue;
                }
                var muster = origin.muster!;
                FreshSiegeRules rules = default;
                if (freshSiegePolicy != null && !freshRules.TryGetValue(target, out rules))
                {
                    (rules, int work) = freshSiegePolicy(target);
                    freshRules[target] = rules;
                    yield return work;
                }
                if (rules.PaidBlock is string paidBlock)
                    count(paidBlock);
                if (!roster.TryGetValue(muster, out var arrivals))
                {
                    var candidates = new List<Candidate>();
                    foreach (var row in free)
                    {
                        if (!leaders.Contains(row.Party) && !members.Contains(row.Party))
                            continue;
                        double hours = ReliefMusterHours(row.Party, muster);
                        yield return 1;
                        if (!double.IsInfinity(hours))
                            candidates.Add(new Candidate(row.Party, hours, row.power));
                    }
                    roster[muster] = arrivals = candidates.OrderBy(row => row.Hours).ThenByDescending(row => row.Strength)
                        .ThenBy(row => row.Party.StringId, StringComparer.Ordinal).ToArray();
                }
                foreach (var leader in arrivals.Where(row => leaders.Contains(row.Party)))
                {
                    yield return 0;
                    var payer = FreeQuoteClan(leader.Party);
                    if (payer == null)
                    {
                        count("stale-siege-leader");
                        continue;
                    }
                    if (failed(leader.Party, muster, target))
                    {
                        count("unchanged-failed-muster");
                        continue;
                    }
                    // Ordinary, ready short-range recovery, or strictly free.
                    // A failed paid recovery never removes a truly free option.
                    int firstMode = rules.PaidBlock != null ? freeOnly : !rules.ReadyMuster ? ordinary :
                        leader.Party.CurrentSettlement == muster ? readyRecovery : freeOnly;
                    int lastMode = firstMode == ordinary && rules.PaidFloor <= 0f && paidRosterBlocked == null ? ordinary : freeOnly;
                    if (rules.ReadyMuster && firstMode == freeOnly && rules.PaidBlock == null)
                        count("paid-siege-recovery-leader-not-at-muster");
                    bool selectedLeader = false;
                    for (int mode = firstMode; mode <= lastMode; mode = mode == ordinary ? freeOnly : mode + 1)
                    {
                        if (!affordable.TryGetValue((muster, leader.Party, mode), out var quoteCalls))
                        {
                            int cost = 0;
                            var quoted = new List<Candidate>();
                            foreach (var member in arrivals)
                            {
                                if (mode == readyRecovery && member.Hours > SiegeRecoveryMusterHours)
                                    break;
                                if (member == leader || !members.Contains(member.Party))
                                    continue;
                                if (FreeQuoteClan(leader.Party) != payer)
                                    break;
                                if (FreeQuoteClan(member.Party) == null)
                                    continue;
                                var key = (leader.Party, member.Party);
                                if (!callsByLeader.TryGetValue(key, out var quote))
                                {
                                    bool inRange = WithinCallRange(leader.Party, member.Party);
                                    yield return 1;
                                    if (FreeQuoteClan(leader.Party) != payer)
                                        break;
                                    if (FreeQuoteClan(member.Party) == null)
                                        continue;
                                    quote = inRange ? ArmyOrders.CallCost(leader.Party, member.Party) : (int?)null;
                                    if (inRange)
                                        yield return 1;
                                    callsByLeader[key] = quote;
                                }
                                if (quote is not int price || price < 0 || mode == freeOnly && price != 0)
                                    continue;
                                if (!ArmyOrders.Affordable(payer.Influence, cost + price))
                                {
                                    count("clan-reserve");
                                    continue;
                                }
                                if (mode == readyRecovery)
                                {
                                    if (!visits.TryGetValue((muster, member.Party), out bool path))
                                    {
                                        visits[(muster, member.Party)] = path = ArmyOrders.CanPathVisit(member.Party, muster);
                                        yield return 1;
                                    }
                                    if (!path)
                                        continue;
                                }
                                quoted.Add(new Candidate(member.Party, member.Hours, member.Strength, price));
                                cost += price;
                            }
                            affordable[(muster, leader.Party, mode)] = quoteCalls = quoted.ToArray();
                        }
                        if (FreeQuoteClan(leader.Party) != payer)
                        {
                            count("stale-siege-leader");
                            break;
                        }
                        float power = leader.Strength + quoteCalls.Sum(call => call.Strength);
                        float affordablePower = power;
                        int paidCalls = quoteCalls.Count(call => call.Cost > 0);
                        if (power < rules.RequiredPower(paidCalls > 0))
                        {
                            count("pre-camp-retry-needs-stronger-force");
                            continue;
                        }
                        // Avoid scratch path trials for leaders whose affordable
                        // roster cannot meet the conservative native check.
                        bool strong = QuoteStrength(leader.Party, target, ConservativeSiegePower(power), out float score, out int scoreWork);
                        yield return scoreWork;
                        if (FreeQuoteClan(leader.Party) != payer)
                        {
                            count("stale-siege-leader");
                            break;
                        }
                        if (!strong)
                        {
                            count(mode == readyRecovery ? "paid-siege-recovery-not-ready-within-24h" : "fresh-muster-insufficient-headroom");
                            continue;
                        }
                        bool route = SiegeRoute(leader.Party, origin.point, target);
                        yield return 1;
                        if (!route)
                        {
                            count("no-native-path");
                            break;
                        }
                        if (FreeQuoteClan(leader.Party) != payer)
                        {
                            count("stale-siege-leader");
                            break;
                        }
                        float hostile = 0f;
                        foreach (var step in SweepSiegeThreat(leader.Party, target, priorCamp?.Invoke(target), value => hostile = value))
                            yield return step;
                        if (FreeQuoteClan(leader.Party) != payer)
                        {
                            count("stale-siege-leader");
                            break;
                        }
                        if (Overwhelmed(hostile, power))
                        {
                            count("visible-overwhelming-local-threat");
                            break;
                        }
                        if (!visits.TryGetValue((muster, leader.Party), out bool visit))
                        {
                            visits[(muster, leader.Party)] = visit = ArmyOrders.CanPathVisit(leader.Party, muster);
                            yield return 1;
                        }
                        if (!visit)
                            break;
                        int length = quoteCalls.Length;
                        while (length > 0)
                        {
                            if (FreeQuoteClan(leader.Party) != payer)
                                break;
                            var last = quoteCalls[length - 1];
                            float reducedPower = power - last.Strength;
                            int reducedPaid = paidCalls - (last.Cost > 0 ? 1 : 0);
                            if (reducedPower < rules.RequiredPower(reducedPaid > 0))
                                break;
                            bool reducedStrong = QuoteStrength(leader.Party, target, ConservativeSiegePower(reducedPower), out float reduced, out int work);
                            yield return work;
                            if (!reducedStrong || Overwhelmed(hostile, reducedPower))
                                break;
                            power = reducedPower;
                            paidCalls = reducedPaid;
                            score = reduced;
                            length--;
                        }
                        var selectedCalls = quoteCalls.Take(length).ToArray();
                        if (FreeQuoteClan(leader.Party) != payer || target.MapFaction == null ||
                            selectedCalls.Any(call => FreeQuoteClan(call.Party) == null))
                        {
                            count("stale-siege-roster");
                            break;
                        }
                        if (paidCalls > 0 && paidRosterBlocked?.Invoke(target,
                            selectedCalls.Where(call => call.Cost > 0).Select(call => call.Party.StringId)) == true)
                        {
                            count("cross-target-recent-paid-roster");
                            continue;
                        }
                        var plan = new Plan(muster, leader, selectedCalls, TravelHours(distance, leader.Party.Speed));
                        Consider(new SiegeChoice(target, null, plan, distance, site.value, power, score, affordablePower, origin.point));
                        selectedLeader = true;
                        break;
                    }
                    if (selectedLeader)
                        break; // first eligible, affordable leader in arrival order at this fort
                }
            }
            complete(best);

            // The campaign can disperse an army or destroy a party between yields.
            MobileParty? LiveArmyLeader(Army army) =>
                !army.IsDispersing && army.Kingdom == kingdom && army.LeaderParty is { } leader &&
                leader.Army == army && ArmyOrders.CanDirect(leader, kingdom) ? leader : null;
            Clan? FreeQuoteClan(MobileParty party) =>
                party.Army == null && ArmyOrders.CanDirect(party, kingdom) ? party.LeaderHero.Clan : null;

            bool QuoteStrength(MobileParty leader, Settlement target, float power, out float score, out int work)
            {
                work = 0;
                score = 0;
                if (target.MapFaction == null || target.Town == null)
                    return false;
                var key = (leader, target, power);
                if (!strengthQuotes.TryGetValue(key, out score))
                {
                    SiegeStrength(leader, target, power, out score);
                    strengthQuotes.Add(key, score);
                    work = 1;
                }
                return score > 0f && !float.IsInfinity(score);
            }

            void Consider(SiegeChoice choice)
            {
                viable(choice);
                if (best == null || choice.Distance < best.Distance ||
                    (choice.Distance == best.Distance && (choice.Value > best.Value ||
                     (choice.Value == best.Value && (choice.Army != null && best.Army == null ||
                      ((choice.Army == null) == (best.Army == null) &&
                       StringComparer.Ordinal.Compare(choice.Target.StringId, best.Target.StringId) < 0))))))
                    best = choice;
            }
        }

        internal static float SiegePresent(Army army, Kingdom kingdom) => ArmyOrders.Members(army)
            .Where(party => party.Army == army && (party == army.LeaderParty || party.AttachedTo == army.LeaderParty) &&
                ArmyOrders.CanDirect(party, kingdom)).Sum(SiegeEstimate);

        // A native membership backlink, not a quote, payment or current order.
        // Members() also deduplicates parties listed in both native collections.
        internal static float SiegeRoster(Army army) => ArmyOrders.Members(army)
            .Where(party => party.Army == army && ArmyOrders.CanDirect(party, army.Kingdom)).Sum(SiegeEstimate);

        internal static float SiegeEstimate(MobileParty party) =>
            MilitaryObjective.Power(party.Party, BattleSideEnum.Defender, MapEvent.PowerCalculationContext.Estimated);

        internal static float SiegeRadius => NativeCampaign.Current.Models.MobilePartyAIModel.SettlementDefendingNearbyPartyCheckRadius;
        internal static bool Overwhelmed(float hostile, float friendly) => friendly > 0 && 5d * hostile > 6d * friendly;

        internal static bool KnownSiegeThreat(MobileParty observer, Settlement target, float power, CampaignVec2? priorCamp = null) =>
            Overwhelmed(KnownSiegeThreatPower(observer, target, priorCamp), power);

        internal static float KnownSiegeThreatPower(MobileParty observer, Settlement target, CampaignVec2? priorCamp, bool includeObserver = false)
        {
            float result = 0;
            foreach (var _ in SweepSiegeThreat(observer, target, priorCamp, power => result = power, includeObserver)) { }
            return result;
        }

        private static IEnumerable<int> SweepSiegeThreat(MobileParty observer, Settlement target, CampaignVec2? priorCamp,
            Action<float> complete, bool includeObserver = false)
        {
            // Discover candidates atomically; spatial iterator state must not
            // survive a native update. Expensive contact paths are separable.
            float sight = NativeCampaign.Current.Models.MapVisibilityModel?.GetPartySpottingRange(observer).ResultNumber ?? 0f;
            float radius = SiegeRadius;
            var gate = target.GatePosition;
            float result = 0f;
            var centers = priorCamp is CampaignVec2 prior && prior != gate ? new[] { gate, prior } : new[] { gate };
            foreach (var center in includeObserver ? centers.Append(observer.Position).Distinct() : centers.AsEnumerable())
            {
                if (!(sight > 0) || observer.Position.DistanceSquared(center.ToVec2()) > (sight + radius) * (sight + radius))
                    continue;
                var contacts = new List<(MobileParty party, float distance, float power)>();
                foreach (var step in SweepVisibleThreat(observer, center, radius, sight, contacts))
                    yield return step;
                result = Math.Max(result, contacts.Sum(row => row.power));
            }
            complete(result);
        }

        internal static (MobileParty party, float distance, float power)[] VisibleThreat(
            MobileParty observer, CampaignVec2 center, float radius, float sight)
        {
            var result = new List<(MobileParty party, float distance, float power)>();
            foreach (var _ in SweepVisibleThreat(observer, center, radius, sight, result)) { }
            return result.ToArray();
        }

        private static IEnumerable<int> SweepVisibleThreat(MobileParty observer, CampaignVec2 center, float radius, float sight,
            List<(MobileParty party, float distance, float power)> result)
        {
            if (!(sight > 0) || !(radius > 0) || !center.IsOnLand || !observer.Position.IsOnLand)
                yield break;
            var roots = new List<MobileParty>();
            var counted = new HashSet<MobileParty>();
            var search = MobileParty.StartFindingLocatablesAroundPosition(center.ToVec2(), radius);
            for (var root = MobileParty.FindNextLocatable(ref search); root != null; root = MobileParty.FindNextLocatable(ref search))
                roots.Add(root);
            yield return 0;
            foreach (var root in roots)
            {
                if (root.AttachedTo != null || !FieldReady(root) || root.MapFaction == null ||
                    observer.MapFaction?.IsAtWarWith(root.MapFaction) != true)
                    continue;
                float local = root.Position.DistanceSquared(center.ToVec2());
                if (local > radius * radius || root.Position.DistanceSquared(observer.Position.ToVec2()) > sight * sight)
                    continue;
                bool path = ArmyOrders.CanPathLand(root, root.Position, center);
                yield return 1;
                if (!path)
                    continue;
                foreach (var party in root.AttachedParties.Prepend(root))
                {
                    if (!FieldReady(party) || party.MapFaction != root.MapFaction || counted.Contains(party) ||
                        (party != root && party.AttachedTo != root))
                        continue;
                    float distance = party.Position.DistanceSquared(center.ToVec2());
                    if (distance > radius * radius || party.Position.DistanceSquared(observer.Position.ToVec2()) > sight * sight)
                        continue;
                    float power = MilitaryObjective.Power(party.Party);
                    if (power > 0)
                    {
                        counted.Add(party);
                        result.Add((party, (float)Math.Sqrt(distance), power));
                    }
                }
            }
        }

        private static bool FieldReady(MobileParty party) => party.IsActive && !party.IsDisbanding && !party.IsGarrison &&
            !party.IsMilitia && !party.IsCaravan && !party.IsVillager && party.CurrentSettlement == null && !party.IsCurrentlyAtSea && party.Position.IsOnLand &&
            !party.IsTransitionInProgress && !party.IsInRaftState && party.MapEvent == null && party.BesiegerCamp == null && !party.IsFleeing();

        internal static float CampFieldPower(BesiegerCamp camp, float radius) => camp.GetInvolvedPartiesForEventType().Distinct()
            .Where(party => party.MobileParty is { IsActive: true, IsCurrentlyAtSea: false } mobile && mobile.Position.IsOnLand &&
                !mobile.IsTransitionInProgress && mobile.Position.DistanceSquared(camp.LeaderParty.Position.ToVec2()) <= radius * radius)
            .Sum(party => MilitaryObjective.Power(party, BattleSideEnum.Defender));
    }
}
