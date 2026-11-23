using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        // Read the actual assignment maps, not KingdomForces' already
        // deduplicated inventory. Inspect the native roster named by every
        // assignment independently, including formations closing safely.
        internal sealed class Commitment
        {
            internal readonly Army Army;
            internal readonly Settlement Target;
            internal readonly string Job;
            internal readonly bool Closing, NativeOrder;
            internal readonly double DepartureHour;
            internal readonly MobileParty[] Members, Present, Traveling, Protected, PendingRelease;
            internal Commitment(Army army, Settlement target, string job, bool closing, bool nativeOrder, double departureHour, MobileParty[] pending)
            {
                (Army, Target, Job, Closing, NativeOrder, DepartureHour, PendingRelease) =
                    (army, target, job, closing, nativeOrder, departureHour, pending);
                Members = Registered(army) ? ArmyOrders.Members(army) : Array.Empty<MobileParty>();
                Present = Members.Where(party => !pending.Contains(party) && (party == army.LeaderParty || party.AttachedTo == army.LeaderParty)).ToArray();
                Traveling = Members.Except(Present).Except(pending).ToArray();
                Protected = Members.Concat(pending).Distinct().Where(party => party.IsMainParty ||
                    ArmyOrders.ReleaseBlocker(party) is string blocker &&
                    (blocker != "native-event" || party.MapEvent != null || party.BesiegerCamp == null)).ToArray();
            }
        }

        internal Commitment[] ReadCommitments(string? kingdom) =>
            _relief.Where(pair => kingdom == null || pair.Value.State.Kingdom == kingdom).Select(pair =>
                new Commitment(pair.Key, pair.Value.Target, AssignmentOwner(pair.Value) == pair.Value.State.Kingdom ? "home" : "allied",
                    pair.Value.State.End != null, pair.Value.State.Departed && ArmyOrders.HasDefendOrder(pair.Key, pair.Value.Target),
                    pair.Value.State.DepartureHour,
                    pair.Value.Surplus.Where(party => party.Army == pair.Key ||
                        (party.Army == null && party.DefaultBehavior == AiBehavior.EscortParty && party.TargetParty == pair.Key.LeaderParty)).ToArray()))
            .Concat(_sieges.Where(pair => kingdom == null || pair.Value.State.Kingdom == kingdom).Select(pair =>
                new Commitment(pair.Key, pair.Value.Target, "siege", pair.Value.State.End != null,
                    pair.Value.State.Issued && ArmyOrders.HasSiegeOrder(pair.Key, pair.Value.Target),
                    pair.Value.State.DepartureHour, Array.Empty<MobileParty>())))
            .ToArray();

        internal static MobileParty[] ConflictingClaims(IEnumerable<Commitment> commitments) =>
            commitments.SelectMany(claim => claim.Members.Concat(claim.PendingRelease).Distinct())
                .GroupBy(party => party).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();

        private void LogCoordination(MilitaryObjective job, string action, KingdomCommander.ReliefForce[] planned,
            IEnumerable<Army> priorOffense, IEnumerable<Army> dispatched, double? first = null, double? ready = null)
        {
            var active = ReadCommitments(job.Kingdom.StringId);
            var changed = new HashSet<Army>(dispatched.Concat(priorOffense));
            if (changed.Count == 0)
                return;
            string targets = string.Join(", ", active.OrderBy(claim => claim.Job).ThenBy(claim => claim.Target.StringId)
                .ThenBy(claim => CampaignLog.ArmyId(claim.Army), StringComparer.Ordinal).Select(claim =>
                    $"{claim.Job}/{claim.Target.StringId}:{CampaignLog.ArmyId(claim.Army)}:" +
                    (claim.Closing ? "closing" : claim.NativeOrder ? "ordered" : "muster") +
                    (changed.Contains(claim.Army) ? $"(present={PartyIds(claim.Present)}; traveling={PartyIds(claim.Traveling)};" +
                        $" protected={PartyIds(claim.Protected)}; pendingRelease={PartyIds(claim.PendingRelease)})" : "")));
            _log.Event($"Coordination commitment: kingdom={job.Kingdom.StringId} defense={job.Target.StringId} action={action} " +
                $"camp={CampaignLog.Number(job.CampPower)} firstETA=+{CampaignLog.Number(first ?? planned.Min(force => force.First))}h " +
                $"combinedETA=+{CampaignLog.Number(ready ?? planned.Max(force => force.Ready))}h " +
                $"claims=[{targets}] duplicate=[{PartyIds(ConflictingClaims(active))}].");
        }

        private static string PartyIds(IEnumerable<MobileParty> parties) =>
            string.Join(",", parties.Select(party => party.StringId).OrderBy(id => id, StringComparer.Ordinal));

#if CAMPAIGN_ACCEPTANCE
        internal sealed class CoordinationContest
        {
            internal readonly double Hour;
            internal readonly MilitaryObjective Higher;
            internal readonly (MilitaryObjective job, Army[] routes, bool UnderstrengthTransfer)[] Lower;
            internal readonly (Army army, Settlement target)[] Offense;
            internal readonly Army[] Candidates, Chosen;
            internal readonly bool NeedsBoth;
            internal CoordinationContest(double hour, MilitaryObjective higher, (MilitaryObjective, Army[], bool)[] lower,
                (Army, Settlement)[] offense, Army[] candidates, Army[] chosen, bool needsBoth) =>
                (Hour, Higher, Lower, Offense, Candidates, Chosen, NeedsBoth) = (hour, higher, lower, offense, candidates, chosen, needsBoth);
        }

        private readonly List<CoordinationContest> _coordinationContests = new();
        internal IReadOnlyList<CoordinationContest> CoordinationContests => _coordinationContests;
        internal bool TrackCoordinationContests { get; set; }

        private void ObserveCoordinationContest(MilitaryObjective higher, IEnumerable<MilitaryObjective> lower,
            KingdomCommander.ReliefForce[] candidates, KingdomCommander.ReliefForce[] selected, float coverage, double now)
        {
            if (!TrackCoordinationContests || selected.Length == 0)
                return;
            using var timing = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.Acceptance);
            var offense = _sieges.Where(pair => pair.Key.Kingdom == higher.Kingdom && pair.Value.State.End == null &&
                pair.Value.State.Issued && ArmyOrders.HasSiegeOrder(pair.Key, pair.Value.Target))
                .Select(pair => (army: pair.Key, target: pair.Value.Target)).ToArray();
            bool divertsOffense = selected.Any(force => offense.Any(prior => prior.army == force.Army));
            if (candidates.Length < 2 && !(divertsOffense && offense.Length >= 2))
                return;
            bool combinedAdequate = higher.Sufficient(coverage + selected.Sum(force => force.Power));
            var lowerRoutes = lower.Where(job => job.HasLandCamp && job.StillEligible && NeedsCandidate(job)).Select(job =>
            {
                var routes = candidates.Where(force => force.RetainedMuster != null ?
                    KingdomCommander.RedirectMuster(force.Army!, job, force.RetainedMuster) != null :
                     KingdomCommander.WithinReliefRange(KingdomCommander.ReliefHours(force.Army!.LeaderParty, job)) &&
                    ArmyOrders.CanRouteToRelief(force.Army.LeaderParty, KingdomCommander.Origin(force.Army.LeaderParty), job.CampPosition))
                    .Select(force => force.Army!).ToArray();
                // A fresh understrength group is not enough to prove contested
                // allocation. Require the actual group to give up another
                // still-routable, native defense assignment as a unit.
                bool transfer = !combinedAdequate && selected.Length > 1 && selected.Length == candidates.Length &&
                    selected.All(force => routes.Contains(force.Army!) && _relief.TryGetValue(force.Army!, out var prior) &&
                        prior.Target == job.Target && prior.State.End == null && prior.State.Departed &&
                        AssignmentOwner(prior) == job.Owner.StringId && ArmyOrders.HasDefendOrder(force.Army!, job.Target));
                return (job, routes, UnderstrengthTransfer: transfer);
            }).Where(pair => pair.routes.Length != 0).ToArray();
            if (lowerRoutes.Length == 0 && !(divertsOffense && offense.Length >= 2))
                return;
            _coordinationContests.RemoveAll(item => item.Higher.Kingdom == higher.Kingdom && item.Higher.Target == higher.Target);
            if (_coordinationContests.Count >= 32)
                _coordinationContests.RemoveAt(0);
            _coordinationContests.Add(new CoordinationContest(now, higher, lowerRoutes, divertsOffense ? offense : Array.Empty<(Army, Settlement)>(),
                candidates.Select(force => force.Army!).ToArray(), selected.Select(force => force.Army!).ToArray(),
                selected.Length > 1 && selected.All(force => !higher.Sufficient(coverage + force.Power)) &&
                combinedAdequate));

            bool NeedsCandidate(MilitaryObjective job)
            {
                var existing = Coverage(job);
                float shared = candidates.Sum(force => _relief.TryGetValue(force.Army!, out var claim) ?
                    FormationCoverage(job, force.Army!, claim).committed : 0f);
                return !job.Sufficient(existing.support + existing.committed - shared);
            }
        }
#endif
    }
}
