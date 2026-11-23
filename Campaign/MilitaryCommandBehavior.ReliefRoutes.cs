using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        // Native route estimates can move by hours between map ticks. A pure
        // speed upgrade must beat both the relative and absolute ETA margins.
        private const double ReliefEtaImprovementFraction = 0.15d;
        private readonly Dictionary<(Kingdom, bool), (string Orders, double Since)> _pendingReliefEtaSwitches = new();

        // Match field armies across this kingdom's competing home (or allied)
        // sieges before the value-ranked allocator can take them one town at a time.
        // Reservations last only for this planning pass; the next one starts from
        // observed positions, locks, orders and current camp strengths, even on load.
        private Dictionary<Army, Settlement> MatchNearbyRelief(Kingdom kingdom, bool home, MilitaryObjective[] tier, double now,
            out HashSet<Settlement> adequate)
        {
            var reserved = new Dictionary<Army, Settlement>();
            adequate = new HashSet<Settlement>();
            var scope = (kingdom, home);
            if (tier.Length < 2)
            {
                _pendingReliefEtaSwitches.Remove(scope);
                return reserved;
            }
            var forces = KingdomForces.Read(kingdom);
            var routes = new List<(int rank, KingdomCommander.ReliefForce force)>();
            var fixedArrivals = new List<(int rank, KingdomCommander.ReliefForce force)>();
            var mobile = new HashSet<Army>();
            foreach (var army in forces.Armies)
            {
                if (!Registered(army) || OwnsControlledFixture(army) || ArmyTakeoverBehavior.Current?.IsPending(army) == true)
                    continue;
                if (_relief.TryGetValue(army, out var claim))
                {
                    if (claim.Surplus.Count != 0 || !OwnsReliefOrder(army, claim) ||
                        (!tier[0].IsHome && claim.State.End == null && AssignmentOwner(claim) == kingdom.StringId))
                        continue;
                    if (!claim.State.Departed && claim.State.End == null)
                    {
                        int rank = Array.FindIndex(tier, job => job.Target == claim.Target);
                        if (rank >= 0 && claim.State.End == null && claim.Muster is { } site && ArmyOrders.CanRelease(army) &&
                            KingdomCommander.RedirectMuster(army, tier[rank], site, keepCurrentSite: true) is { } gathering)
                            fixedArrivals.Add((rank, gathering));
                        continue;
                    }
                }
                else if (_sieges.TryGetValue(army, out var offense))
                {
                    if (!offense.State.Issued || SiegeDiversionBlocker(army, offense) != null)
                        continue;
                }
                else if (!IdleReliefArmy(army))
                    continue;
                for (int rank = 0; rank < tier.Length; ++rank)
                {
                    var force = KingdomCommander.ReadyRelief(army, tier[rank], forces, _sieges.ContainsKey(army));
                    if (force == null)
                        continue;
                    routes.Add((rank, force));
                    mobile.Add(army);
                }
            }
            if (mobile.Count == 0)
            {
                _pendingReliefEtaSwitches.Remove(scope);
                return reserved; // only gathering or locked: use the normal muster planner
            }
            // Movable responders are counted once, at their ETA below. Never let a
            // distant promise or an unrelated battle stand in for local field cover.
            var basePower = tier.Select(job => Coverage(job).support + _relief.Where(p => !mobile.Contains(p.Key))
                .Sum(p => FormationCoverage(job, p.Key, p.Value).local)).ToArray();
            var power = (float[])basePower.Clone();
            var matched = new Dictionary<Army, (int rank, KingdomCommander.ReliefForce force)>();
            var ordered = routes.Concat(fixedArrivals).OrderBy(r => r.force.Ready).ThenBy(r => r.rank)
                .ThenByDescending(r => _relief.TryGetValue(r.force.Army!, out var claim) && claim.Target == tier[r.rank].Target)
                .ThenByDescending(r => r.force.Present).ThenBy(r => CampaignLog.ArmyId(r.force.Army!), StringComparer.Ordinal).ToArray();
            var byJob = tier.Select((_, rank) => ordered.Where(row => row.rank == rank).ToArray()).ToArray();
            bool CurrentOrder((int rank, KingdomCommander.ReliefForce force) row) =>
                _relief.TryGetValue(row.force.Army!, out var claim) && claim.State.End == null &&
                claim.Target == tier[row.rank].Target && (row.force.RetainedMuster != null ||
                    ArmyOrders.HasDefendOrder(row.force.Army!, claim.Target));
            bool OnStation((int rank, KingdomCommander.ReliefForce force) row) =>
                row.force.RetainedMuster == null && CurrentOrder(row) && tier[row.rank].IsLocal(row.force.Army!.LeaderParty);

            void Assign((int rank, KingdomCommander.ReliefForce force) row)
            {
                matched.Add(row.force.Army!, row);
                power[row.rank] += row.force.Power;
            }

            List<(int rank, KingdomCommander.ReliefForce force)>? SufficientGroup(
                int rank, float total, IEnumerable<(int rank, KingdomCommander.ReliefForce force)> eligible)
            {
                var group = new List<(int rank, KingdomCommander.ReliefForce force)>();
                if (tier[rank].Sufficient(total)) return group;
                foreach (var row in eligible)
                {
                    group.Add(row);
                    total += row.force.Power;
                    if (!tier[rank].Sufficient(total)) continue;
                    // Release any redundant early arrivals so they can cover a
                    // second siege; the last needed arrival still sets the ETA.
                    foreach (var spare in group.OrderBy(row => row.force.Power).ToArray())
                        if (tier[rank].Sufficient(total - spare.force.Power))
                        {
                            group.Remove(spare);
                            total -= spare.force.Power;
                        }
                    return group;
                }
                return null;
            }

            void MatchUnassigned(HashSet<Army>? retain = null)
            {
                bool Unused((int rank, KingdomCommander.ReliefForce force) row) => !matched.ContainsKey(row.force.Army!);
                // Don't strip a defense already adequately covered on the ground.
                for (int rank = 0; rank < tier.Length; rank++)
                    if (!tier[rank].Sufficient(power[rank]) &&
                        SufficientGroup(rank, power[rank], byJob[rank].Where(row => Unused(row) && OnStation(row))) is { } local)
                        foreach (var row in local)
                            Assign(row);

                // Reserve the fastest complete response first, not the first army
                // that can get somewhere. A pair can beat a siege neither can cover alone.
                while (true)
                {
                    List<(int rank, KingdomCommander.ReliefForce force)>? best = null;
                    for (int rank = 0; rank < tier.Length; rank++)
                        if (!tier[rank].Sufficient(power[rank]) &&
                            SufficientGroup(rank, power[rank], byJob[rank].Where(Unused)) is { } group &&
                            (best == null || group[group.Count - 1].force.Ready < best[best.Count - 1].force.Ready))
                            best = group;
                    if (best == null) break;
                    foreach (var row in best)
                        Assign(row);
                }

                // Help the closest uncovered camp with any leftover force, even if
                // it cannot meet the benchmark. Leave the decision to fight to native AI.
                foreach (var row in ordered)
                    if (Unused(row) && !tier[row.rank].Sufficient(power[row.rank]))
                        Assign(row);

                // A spare can substitute on an adequately covered job to free its
                // only routable responder. Don't pull defenders off station early.
                for (int rank = 0; rank < tier.Length; rank++)
                {
                    if (tier[rank].Sufficient(power[rank])) continue;
                    foreach (var transfer in byJob[rank].Where(r => r.force.RetainedMuster == null &&
                        tier[rank].Sufficient(power[rank] + r.force.Power)))
                    {
                        var army = transfer.force.Army!;
                        if (retain?.Contains(army) == true || !matched.TryGetValue(army, out var donor) || donor.rank == rank ||
                            !tier[donor.rank].Sufficient(power[donor.rank])) continue;
                        bool onStation = OnStation(donor);
                        var spare = byJob[donor.rank].FirstOrDefault(r => r.force.RetainedMuster == null && Unused(r) &&
                            tier[donor.rank].Sufficient(power[donor.rank] - donor.force.Power + r.force.Power) &&
                            (tier[donor.rank].IsLocal(r.force.Army!.LeaderParty) || !onStation && r.force.Ready <= transfer.force.Ready));
                        if (spare.force == null) continue;
                        matched[army] = transfer;
                        power[donor.rank] -= donor.force.Power;
                        Assign(spare);
                        power[rank] += transfer.force.Power;
                        break;
                    }
                }
            }

            MatchUnassigned();
            // Compare live orders and the proposal using the same force/route
            // snapshot. Losing a ready route or enough power invalidates an
            // incumbent response immediately; only ETA upgrades need confirmation.
            var incumbents = new List<(int rank, KingdomCommander.ReliefForce force)>();
            var changes = new List<string>();
            double largestGain = 0;
            bool materialGain = false, losesTime = false;
            int gainedCoverage = 0, lostCoverage = 0;
            string ArmyIds(IEnumerable<(int rank, KingdomCommander.ReliefForce force)> group) =>
                string.Join(",", group.Select(row => CampaignLog.ArmyId(row.force.Army!)).OrderBy(id => id, StringComparer.Ordinal));
            for (int rank = 0; rank < tier.Length; rank++)
            {
                var current = SufficientGroup(rank, basePower[rank], byJob[rank].Where(OnStation)) ??
                    SufficientGroup(rank, basePower[rank], byJob[rank].Where(CurrentOrder));
                bool proposedAdequate = tier[rank].Sufficient(power[rank]);
                if (current == null)
                {
                    if (proposedAdequate) gainedCoverage++;
                    continue;
                }
                incumbents.AddRange(current);
                if (!proposedAdequate)
                {
                    lostCoverage++;
                    changes.Add($"{tier[rank].Target.StringId}:{ArmyIds(current)}->-");
                    continue;
                }
                if (current.Count == 0) continue;
                var proposed = SufficientGroup(rank, basePower[rank], byJob[rank].Where(row =>
                    matched.TryGetValue(row.force.Army!, out var dest) && dest.rank == row.rank))!;
                if (current.Count == proposed.Count && current.All(row => proposed.Contains(row))) continue;
                double currentEta = current[current.Count - 1].force.Ready;
                double proposedEta = proposed[proposed.Count - 1].force.Ready;
                double gain = currentEta - proposedEta;
                losesTime |= gain < 0;
                largestGain = Math.Max(largestGain, gain);
                materialGain |= gain > Math.Max(ReliefPlanInterval, currentEta * ReliefEtaImprovementFraction);
                changes.Add($"{tier[rank].Target.StringId}:{ArmyIds(current)}->{ArmyIds(proposed)}");
            }

            bool dropsCoverage = lostCoverage > gainedCoverage;
            bool speedOnly = lostCoverage == 0 && gainedCoverage == 0 && changes.Count != 0;
            if (dropsCoverage || speedOnly)
            {
                string orders = string.Join(";", changes);
                bool clearLead = speedOnly && materialGain && !losesTime;
                bool confirmed = clearLead && _pendingReliefEtaSwitches.TryGetValue(scope, out var pending) &&
                    pending.Orders == orders && now >= pending.Since + ReliefPlanInterval;
                if (confirmed || !clearLead)
                    _pendingReliefEtaSwitches.Remove(scope);
                else if (!_pendingReliefEtaSwitches.TryGetValue(scope, out var previous) || previous.Orders != orders)
                    _pendingReliefEtaSwitches[scope] = (orders, now);
                string reason = dropsCoverage ? "preserve-coverage" : confirmed ? "confirmed-faster" :
                    clearLead ? "awaiting-confirmation" : "no-clear-gain";
                if (_log.Changed("relief-eta-switch", $"{kingdom.StringId}/{home}", $"{reason}:{orders}"))
                    _log.Event($"Relief ETA review: kingdom={kingdom.StringId} scope={(home ? "home" : "allied")} " +
                        $"decision={reason} bestGain=+{CampaignLog.Number(largestGain)}h orders=[{orders}].");
                if (!confirmed)
                {
                    matched.Clear();
                    Array.Copy(basePower, power, power.Length);
                    foreach (var row in incumbents)
                        Assign(row);
                    MatchUnassigned(new HashSet<Army>(matched.Keys));
                }
            }
            else
            {
                _pendingReliefEtaSwitches.Remove(scope);
                _log.Changed("relief-eta-switch", $"{kingdom.StringId}/{home}", null);
            }

            for (int rank = 0; rank < tier.Length; rank++)
                if (tier[rank].Sufficient(power[rank]))
                    adequate.Add(tier[rank].Target);
            foreach (var (rank, force) in matched.Values)
            {
                var job = tier[rank];
                var army = force.Army!;
                reserved.Add(army, job.Target);
                if (force.RetainedMuster != null)
                    continue;
                var prior = _relief.TryGetValue(army, out var a) ? a.Target.StringId : "-";
                string basis = adequate.Contains(job.Target) ? "adequate-response" : "nearest-understrength";
                if (_log.Changed("relief-route-match", CampaignLog.ArmyId(army), $"{prior}:{job.Target.StringId}:{basis}"))
                {
                    var alt = routes.Where(r => r.rank != rank && r.force.Army == army).OrderBy(r => r.force.Ready).FirstOrDefault();
                    _log.Event($"Relief route match: kingdom={kingdom.StringId} army={CampaignLog.ArmyId(army)} target={job.Target.StringId} previous={prior} " +
                        $"eta=+{CampaignLog.Number(force.Ready)}h other={(alt.force == null ? "-" : tier[alt.rank].Target.StringId)} " +
                        $"otherETA={(alt.force == null ? "-" : CampaignLog.Number(alt.force.Ready))}h " +
                        $"present={CampaignLog.Number(force.Present)} plannedCoverage={CampaignLog.Number(power[rank])} " +
                        $"camp={CampaignLog.Number(job.CampPower)} basis={basis}.");
                }
            }
            return reserved;
        }
    }
}
