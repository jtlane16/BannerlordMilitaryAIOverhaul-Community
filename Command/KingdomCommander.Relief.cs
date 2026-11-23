using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Command
{
    internal static partial class KingdomCommander
    {
        internal const double MaxReliefHours = 72d;
        internal static bool WithinReliefRange(double hours) => hours <= MaxReliefHours;

        internal static bool HasHomeSiege(Kingdom kingdom) => kingdom.Fiefs.Any(fief =>
            fief.Settlement.MapFaction == kingdom && fief.Settlement.IsUnderSiege);

        internal static Settlement[] BesiegedFortifications(IEnumerable<Kingdom> kingdoms) => kingdoms
            .SelectMany(owner => owner.Fiefs).Select(fief => fief.Settlement).Where(target => target.IsUnderSiege).Distinct().ToArray();

        internal static MilitaryObjective[] ReliefObjectives(Kingdom kingdom, IEnumerable<Settlement>? besieged = null) =>
            (besieged ?? BesiegedFortifications(Kingdom.All))
            .Select(target => MilitaryObjective.Relief(target, kingdom)).OfType<MilitaryObjective>()
            .OrderByDescending(job => job.IsHome).ThenByDescending(job => job.Target.IsTown).ThenByDescending(job => job.Value)
            .ThenBy(job => job.Target.StringId, StringComparer.Ordinal).ToArray();

        internal sealed class ReliefForce
        {
            internal readonly Army? Army;
            internal readonly Plan? Muster;
            internal readonly Settlement? RetainedMuster;
            internal readonly float Power, Present;
            internal readonly double First, Ready;
            internal readonly Army[] Releases;

            internal ReliefForce(Army army, float present, double first, Settlement? retainedMuster = null,
                float projectedArrivals = 0, double ready = 0) =>
                (Army, Present, Power, First, Ready, RetainedMuster, Releases) =
                    (army, present, present + projectedArrivals, first, retainedMuster == null ? first : ready, retainedMuster, Array.Empty<Army>());

            internal ReliefForce(Plan plan, Army[] releases) =>
                (Muster, Present, Power, First, Ready, Releases) = (plan, plan.Leader.Strength, plan.Leader.Strength + plan.Calls.Sum(call => call.Strength),
                    plan.Leader.Hours + plan.MarchHours, plan.ArrivalHours + plan.MarchHours, releases);
        }

        internal static double ReliefHours(MobileParty leader, MilitaryObjective job)
        {
            if (!job.HasLandCamp || leader.IsCurrentlyAtSea || !leader.HasLandNavigationCapability || leader.IsTransitionInProgress)
                return double.PositiveInfinity;
            return TravelHours(LandDistance(Origin(leader), job.CampPosition, unrestricted: true), leader.Speed);
        }

        internal static double ReliefMusterHours(MobileParty party, Settlement muster) => party.CurrentSettlement == muster
            ? 0d : party.IsCurrentlyAtSea || !party.HasLandNavigationCapability || party.IsTransitionInProgress
                ? double.PositiveInfinity : TravelHours(LandDistance(Origin(party), muster.GatePosition, unrestricted: true), party.Speed);

        internal static double ReliefMarchFromMuster(MobileParty leader, Settlement muster, MilitaryObjective job) =>
            job.HasLandCamp ? TravelHours(LandDistance(muster.GatePosition, job.CampPosition, unrestricted: true), leader.Speed) :
                double.PositiveInfinity;

        internal static ReliefForce? ReadyRelief(Army army, MilitaryObjective job, KingdomForces inventory, bool canWithdraw = false)
        {
            var leader = army.LeaderParty;
            if ((!canWithdraw && !ArmyOrders.CanRelease(army)) || ArmyOrders.VisitLock(leader) != null ||
                ArmyOrders.Members(army).Any(party => !ArmyOrders.CanDirect(party, job.Kingdom)))
                return null;
            double hours = ReliefHours(leader, job);
            if (!WithinReliefRange(hours) || !ArmyOrders.CanRouteToRelief(leader, Origin(leader), job.CampPosition))
                return null;
            float power = inventory.Parties.Where(row => row.Army == army && row.IsPresent && (row.Exclusion == null ||
                    canWithdraw && row.Party.BesiegerCamp == leader.BesiegerCamp && ArmyOrders.CanDirect(row.Party, job.Kingdom)))
                .Sum(row => MilitaryObjective.Power(row.Party.Party));
            return power > 0 ? new ReliefForce(army, power, hours) : null;
        }

        internal static IEnumerable<(float power, double hours, MobileParty party)> ReliefArrivals(Army army, Kingdom kingdom, Settlement muster, bool siegeEstimate = false)
        {
            var leader = army.LeaderParty;
            foreach (var party in ArmyOrders.Members(army))
            {
                if (party == leader || party.AttachedTo == leader || party.Army != army || !ArmyOrders.CanDirect(party, kingdom) ||
                    ArmyOrders.ReleaseBlocker(party) != null || party.DefaultBehavior != AiBehavior.EscortParty || party.TargetParty != leader)
                    continue;
                double hours = ReliefMusterHours(party, muster);
                if (!double.IsInfinity(hours))
                    yield return (siegeEstimate ? SiegeEstimate(party) : MilitaryObjective.Power(party.Party), hours, party);
            }
        }

        internal static IEnumerable<(float power, double hours, MobileParty party)> TimelyReliefArrivals(
            Army army, MilitaryObjective job, Settlement muster, double march) =>
            ReliefArrivals(army, job.Kingdom, muster).Where(arrival => WithinReliefRange(arrival.hours + march));

        internal static ReliefForce? RedirectMuster(Army army, MilitaryObjective job, Settlement currentMuster, bool keepCurrentSite = false)
        {
            var leader = army.LeaderParty;
            if (ArmyOrders.VisitLock(leader) != null || ArmyOrders.Members(army).Any(party => !ArmyOrders.CanDirect(party, job.Kingdom)))
                return null;
            float present = ArmyOrders.Members(army).Where(party => party.Army == army &&
                (party == leader || party.AttachedTo == leader)).Sum(party => MilitaryObjective.Power(party.Party));
            if (!(present > 0))
                return null;
            // Preserve the already reachable muster and paid escort orders.
            // If that fort cannot serve the higher-priority job, try another
            // home fort from actual positions without creating a second army.
            ReliefForce? Quote(Settlement site)
            {
                if (!OpenFortification(site, job.Kingdom))
                    return null;
                double leaderHours = ReliefMusterHours(leader, site);
                double march = ReliefMarchFromMuster(leader, site, job);
                if (!WithinReliefRange(leaderHours + march) ||
                    !ArmyOrders.CanPathVisit(leader, site) || !ArmyOrders.CanRouteToRelief(leader, site.GatePosition, job.CampPosition))
                    return null;
                var arrivals = TimelyReliefArrivals(army, job, site, march).ToArray();
                double ready = arrivals.Aggregate(leaderHours, (hours, arrival) => Math.Max(hours, arrival.hours));
                return new ReliefForce(army, present, leaderHours + march, site, arrivals.Sum(arrival => arrival.power), ready + march);
            }
            var current = Quote(currentMuster);
            if (keepCurrentSite || current != null)
                return current;
            return job.Kingdom.Fiefs.Select(fief => fief.Settlement).Where(site => site != currentMuster)
                .OrderBy(site => site.GatePosition.DistanceSquared(job.CampPosition.ToVec2()))
                .Select(Quote).FirstOrDefault(force => force != null);
        }

        // Compare the quickest adequate group, individual alternatives, and
        // a group that preserves scarce routes to other home jobs. Never
        // speculate about a distant ally or promise a remaining army twice.
        internal static ReliefForce[] ChooseReliefFormations(MilitaryObjective job, ReliefForce[] ordered, float committed,
            MilitaryObjective[] lower, Func<MilitaryObjective, float> existing, Func<Army, MilitaryObjective, float> existingShare,
            out string explanation)
        {
            explanation = "send-available-understrength";
            ReliefForce[] Pack(IEnumerable<ReliefForce> forces)
            {
                var choices = new List<ReliefForce>();
                float sum = committed;
                foreach (var force in forces)
                {
                    choices.Add(force);
                    sum += force.Power;
                    if (job.Sufficient(sum))
                        break;
                }
                return choices.OrderBy(force => force.Ready).ToArray();
            }
            var fastest = Pack(ordered);
            if (!job.Sufficient(committed + fastest.Sum(force => force.Power)))
                return fastest; // send what is available
            if (lower.Length == 0)
            {
                explanation = "adequate-quickest-ready-response";
                return ordered.Where(force => job.Sufficient(committed + force.Power) && force.Ready <= fastest.Last().Ready)
                    .Select(force => new[] { force }).FirstOrDefault() ?? fastest;
            }

            float Potential(ReliefForce force, MilitaryObjective other)
            {
                if (force.RetainedMuster != null)
                    return RedirectMuster(force.Army!, other, force.RetainedMuster)?.Power ?? 0f;
                var leader = force.Army!.LeaderParty;
                return WithinReliefRange(ReliefHours(leader, other)) && ArmyOrders.CanRouteToRelief(leader, Origin(leader), other.CampPosition) ?
                    force.Power : 0f;
            }
            var alternatives = lower.Select(other => (job: other, basePower: existing(other),
                shares: ordered.ToDictionary(force => force, force => existingShare(force.Army!, other)),
                routes: ordered.Select(force => (force, power: Potential(force, other))).Where(route => route.power > 0).ToArray())).ToArray();
            int LeftCovered(ReliefForce[] used)
            {
                var available = new HashSet<ReliefForce>(ordered.Except(used));
                int covered = 0;
                foreach (var other in alternatives)
                {
                    float power = other.basePower - used.Sum(force => other.shares[force]);
                    if (other.job.Sufficient(power))
                    {
                        covered++;
                        continue;
                    }
                    var usedHere = new List<ReliefForce>();
                    foreach (var spare in other.routes.Where(route => available.Contains(route.force) && other.shares[route.force] <= 0f))
                    {
                        usedHere.Add(spare.force);
                        power += spare.power;
                        if (!other.job.Sufficient(power))
                            continue;
                        covered++;
                        available.ExceptWith(usedHere);
                        break;
                    }
                }
                return covered;
            }
            var flexible = Pack(ordered.OrderBy(force => alternatives.Count(other => other.routes.Any(route => route.force == force)))
                .ThenBy(force => force.Ready));
            var best = ordered.Where(force => job.Sufficient(committed + force.Power)).Select(force => new[] { force })
                .Concat(new[] { fastest, flexible }).Select(group => (group, left: LeftCovered(group))).OrderByDescending(item => item.left)
                .ThenBy(item => item.group.Last().Ready).ThenBy(item => item.group.Length).First();
            explanation = $"adequate; lowerCovered={best.left}/{lower.Length}, quickestAlternativeLowerCovered={LeftCovered(fastest)}/{lower.Length}";
            return best.group;
        }

        // Share native quotes between the free-party and replacement searches for
        // one objective, while no orders, roster mutations or payments can occur.
        internal sealed class ReliefMusterQuotes
        {
            private readonly MilitaryObjective _job;
            private (Settlement site, double distance)[]? _sites;
            private readonly Dictionary<(MobileParty, Settlement), double> _arrivalHours = new();
            private readonly Dictionary<(MobileParty, Settlement), bool> _paths = new();
            private readonly Dictionary<(MobileParty, MobileParty), int?> _callCosts = new();

            internal ReliefMusterQuotes(MilitaryObjective job) => _job = job;

            internal (Settlement site, double distance)[] Sites() => _sites ??= _job.Kingdom.Fiefs
                .Select(fief => fief.Settlement).Where(site => OpenFortification(site, _job.Kingdom))
                .Select(site => (site, distance: LandDistance(site.GatePosition, _job.CampPosition, unrestricted: true)))
                .Where(pair => !double.IsInfinity(pair.distance)).OrderBy(pair => pair.distance)
                .ThenBy(pair => pair.site.StringId, StringComparer.Ordinal).ToArray();

            internal double ArrivalHours(MobileParty party, Settlement site)
            {
                if (!_arrivalHours.TryGetValue((party, site), out double hours))
                    _arrivalHours[(party, site)] = hours = ReliefMusterHours(party, site);
                return hours;
            }

            internal bool CanRoute(MobileParty leader, Settlement site)
            {
                if (!_paths.TryGetValue((leader, site), out bool routable))
                    _paths[(leader, site)] = routable = ArmyOrders.CanPathVisit(leader, site) &&
                        ArmyOrders.CanRouteToRelief(leader, site.GatePosition, _job.CampPosition);
                return routable;
            }

            internal int? CallCost(MobileParty leader, MobileParty member)
            {
                if (!_callCosts.TryGetValue((leader, member), out var price))
                    _callCosts[(leader, member)] = price = WithinCallRange(leader, member) ? ArmyOrders.CallCost(leader, member) : (int?)null;
                return price;
            }
        }

        internal static ReliefForce? MusterRelief(MilitaryObjective job, KingdomForces inventory, float committed, ReliefMusterQuotes quotes,
            Func<Army, bool>? canRelease, Func<MobileParty, Settlement, bool> failed, Func<MobileParty, bool> reserved, Action<string> count)
        {
            if (!job.HasLandCamp)
                return null;
            var kingdom = job.Kingdom;
            var available = canRelease == null ? null : new HashSet<Army>(inventory.Armies.Where(canRelease));
            // The replacement quote is useful only if it can actually release an army.
            if (available?.Count == 0)
                return null;
            var free = inventory.Parties.Where(row => row.Exclusion == null && !reserved(row.Party) && (row.Army == null || available?.Contains(row.Army) == true))
                .Select(row => (row.Party, Power: MilitaryObjective.Power(row.Party.Party))).Where(row => row.Power > 0).ToArray();
            var leaders = free.Where(row =>
                ArmyOrders.FreePartyBlocker(row.Party, kingdom, true, row.Party.Army) == null &&
                ArmyOrders.VisitLock(row.Party, afterRelease: row.Party.Army) == null).ToArray();
            if (leaders.Length == 0)
            {
                count("no-eligible-leader");
                return null;
            }
            (MobileParty Party, float Power)[]? members = null;
            Candidate[] SortArrivals(IEnumerable<(MobileParty Party, float Power)> roster, Settlement site) =>
                roster.Select(row => new Candidate(row.Party, quotes.ArrivalHours(row.Party, site), row.Power))
                    .Where(row => !double.IsInfinity(row.Hours)).OrderBy(row => row.Hours)
                    .ThenByDescending(row => row.Strength).ThenBy(row => row.Party.StringId, StringComparer.Ordinal).ToArray();

            ReliefForce? best = null;
            foreach (var (site, distance) in quotes.Sites())
            {
                Candidate[]? escortArrivals = null;
                bool MarchCanFit(MobileParty party)
                {
                    // This is the exact native second leg, not a straight-line
                    // guess. The first leg cannot make an over-limit march fit.
                    if (WithinReliefRange(TravelHours(distance, party.Speed)))
                        return true;
                    count("relief-travel-limit");
                    return false;
                }
                foreach (var leader in SortArrivals(leaders.Where(row => MarchCanFit(row.Party)), site))
                {
                    if (failed(leader.Party, site))
                    {
                        count("unchanged-failed-muster");
                        continue;
                    }
                    var march = TravelHours(distance, leader.Party.Speed);
                    if (!WithinReliefRange(leader.Hours + march))
                    {
                        count("relief-travel-limit");
                        continue;
                    }
                    float strength = leader.Strength;
                    var calls = new List<Candidate>();
                    int cost = 0;
                    // Don't ask native navigation about any escort until a leader
                    // can reach this camp within the limit and actually needs help.
                    if (!job.Sufficient(committed + strength))
                    {
                        members ??= free.Where(row => ArmyOrders.FreePartyBlocker(row.Party, kingdom, false, row.Party.Army) == null).ToArray();
                        escortArrivals ??= SortArrivals(members, site);
                        foreach (var member in escortArrivals)
                        {
                            if (job.Sufficient(committed + strength) || !WithinReliefRange(member.Hours + march))
                                break;
                            if (member.Party == leader.Party)
                                continue;
                            if (quotes.CallCost(leader.Party, member.Party) is not int quote || quote < 0)
                            {
                                count("native-call-range-or-quote");
                                continue;
                            }
                            if (!ArmyOrders.Affordable(leader.Party.LeaderHero.Clan.Influence, cost + quote))
                            {
                                count("clan-reserve");
                                continue;
                            }
                            calls.Add(new Candidate(member.Party, member.Hours, member.Strength, quote));
                            strength += member.Strength;
                            cost += quote;
                        }
                    }
                    var plan = new Plan(site, leader, calls.ToArray(), march);
                    var releases = calls.Select(call => call.Party.Army).Append(leader.Party.Army).OfType<Army>().Distinct().ToArray();
                    var force = new ReliefForce(plan, releases);
                    if (best != null && !BetterMuster(job, committed, force, best))
                        continue;
                    if (!quotes.CanRoute(leader.Party, site))
                    {
                        count("native-land-path");
                        continue;
                    }
                    best = force;
                    if (job.Sufficient(committed + strength))
                        break; // earliest legal sufficient leader at this site
                }
                if (best != null && job.Sufficient(committed + best.Power))
                    break; // nearest muster site with a viable response
            }
            if (best == null)
                count("no-reachable-muster");
            return best;
        }

        private static bool BetterMuster(MilitaryObjective job, float committed, ReliefForce a, ReliefForce b)
        {
            bool ampleA = job.Sufficient(committed + a.Power), ampleB = job.Sufficient(committed + b.Power);
            if (ampleA != ampleB)
                return ampleA;
            if (!ampleA && a.Power != b.Power)
                return a.Power > b.Power;
            return a.Ready < b.Ready || (a.Ready == b.Ready && a.Releases.Length < b.Releases.Length);
        }
    }
}
