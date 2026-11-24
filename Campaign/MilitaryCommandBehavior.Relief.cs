using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Library;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        // Native Army and Settlement references use the vanilla save schema.
        // The scalar record deliberately contains no duplicate party roster.
        private sealed class ReliefState
        {
            public string Kingdom = "";
            public string? Owner; // null in pre-alliance saves: the objective was at home
            public string? End;
            public bool Departed;
            public bool Reached;
            public double UpkeepAfter, ReissueAfter, DepartureHour;
            public float DepartureX, DepartureY;
            public int Reissues, SurplusCount;
        }

        private sealed class ReliefAssignment
        {
            internal Settlement Target;
            internal Settlement? Muster;
            internal readonly ReliefState State;
            internal readonly List<MobileParty> Surplus = new();
            internal ReliefAssignment(Settlement target, Settlement? muster, ReliefState state) =>
                (Target, Muster, State) = (target, muster, state);
        }

        internal sealed class ReliefObservation
        {
            internal readonly Army Army;
            internal readonly MilitaryObjective Job;
            internal readonly string Phase;
            internal readonly float Attached, Local;
            internal readonly bool Physical;
            internal ReliefObservation(Army army, MilitaryObjective job, string phase, float attached, float local, bool physical) =>
                (Army, Job, Phase, Attached, Local, Physical) = (army, job, phase, attached, local, physical);
        }

        private readonly Dictionary<Army, ReliefAssignment> _relief = new();
        private Dictionary<string, string> _failedRelief = new(StringComparer.Ordinal);
        private readonly List<ReliefObservation> _reliefObservations = new();
        internal IReadOnlyList<ReliefObservation> ReliefObservations => _reliefObservations;
        private readonly Dictionary<(Kingdom kingdom, Settlement target), MilitaryObjective> _activeRelief = new();
        private readonly HashSet<Settlement> _fallenReliefTargets = new();
        private bool _reliefReview = true, _reliefPlan = true;
        private bool _reliefResumed;
        private const double ReliefPlanInterval = 1d;
        private double _nextReliefPlan;

        private void RegisterReliefEvents()
        {
            CampaignEvents.QuarterHourlyTickEvent.AddNonSerializedListener(this, () => _reliefReview = true);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, _ => RequestReliefPlanning());
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, _ => RequestReliefPlanning());
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, (target, _, _, _, _, detail) =>
            {
                if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege &&
                    (_activeRelief.Keys.Any(key => key.target == target) || _relief.Values.Any(assignment => assignment.Target == target)))
                    _fallenReliefTargets.Add(target);
                RequestReliefPlanning();
            });
            CampaignEvents.MakePeace.AddNonSerializedListener(this, (_, _, _) => RequestReliefPlanning());
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, (_, _, _) => RequestReliefPlanning());
            CampaignEvents.OnAllianceStartedEvent.AddNonSerializedListener(this, (_, _) => RequestReliefPlanning());
            CampaignEvents.OnAllianceEndedEvent.AddNonSerializedListener(this, (_, _) => RequestReliefPlanning());
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(this, _ => RequestReliefPlanning());
            CampaignEvents.ArmyDispersed.AddNonSerializedListener(this, (army, reason, _) =>
            {
                if (!_relief.TryGetValue(army, out var assignment) || ArmyOrders.Releasing == army)
                    return;
                _log.Event($"Relief natural dispersal: {ReliefId(army, assignment)} reason={reason} " +
                    $"cohesion={CampaignLog.Number(army.Cohesion)}; objective will be reviewed independently.");
                RequestReliefPlanning();
            });
            CampaignEvents.PartyAttachedAnotherParty.AddNonSerializedListener(this, RequestArmyReliefReview);
            CampaignEvents.AfterSettlementEntered.AddNonSerializedListener(this, (party, _, _) => RequestArmyReliefReview(party));
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, battle =>
            {
                foreach (var pair in _relief)
                    if (pair.Key.LeaderParty.MapEvent == battle)
                    {
                        // A completed battle is a new interruption, not another
                        // failure of the previous order-restoration attempt.
                        if (pair.Value.State.Departed)
                        {
                            pair.Value.State.Reissues = 0;
                            pair.Value.State.ReissueAfter = 0;
                        }
                        _log.Event($"Relief native battle ended: {ReliefId(pair.Key, pair.Value)} site={battle.MapEventSettlement?.StringId ?? "field"} " +
                            $"result={battle.BattleState}.");
                    }
                RequestReliefPlanning();
            });
        }

        private void RequestReliefPlanning() => _reliefReview = _reliefPlan = true;

        private void RequestArmyReliefReview(MobileParty? party)
        {
            // Both character-only and prisoner settlement entry dispatch a null party.
            if (party?.Army is Army army && _relief.ContainsKey(army))
                _reliefReview = true;
        }

        private void SyncRelief(IDataStore store)
        {
            if (store.IsSaving && _relief.Count == 0 && _failedRelief.Count == 0)
                return;
            var entries = store.IsSaving ? _relief.Where(pair => Registered(pair.Key) || pair.Value.Surplus.Count != 0).ToArray() :
                Array.Empty<KeyValuePair<Army, ReliefAssignment>>();
            var armies = entries.Select(pair => pair.Key).ToList();
            var targets = entries.Select(pair => pair.Value.Target).ToList();
            var musters = entries.Select(pair => pair.Value.Muster ?? pair.Value.Target).ToList();
            var surplus = entries.SelectMany(pair => pair.Value.Surplus).ToList();
            foreach (var pair in entries)
                pair.Value.State.SurplusCount = pair.Value.Surplus.Count;
            string? states = store.IsSaving ? JsonConvert.SerializeObject(entries.Select(pair => pair.Value.State).ToArray()) : null;
            string? failures = store.IsSaving ? JsonConvert.SerializeObject(_failedRelief) : null;
            store.SyncData("reliefArmies", ref armies);
            store.SyncData("reliefTargets", ref targets);
            store.SyncData("reliefMusters", ref musters);
            store.SyncData("reliefCancelledSummons", ref surplus);
            store.SyncData("reliefStates", ref states);
            store.SyncData("reliefFailedInputs", ref failures);
            if (!store.IsLoading)
                return;
            _relief.Clear();
            try
            {
                var records = states == null ? null : JsonConvert.DeserializeObject<ReliefState[]>(states);
                _failedRelief = failures == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, string>>(failures) ?? new(StringComparer.Ordinal);
                if (records != null && armies?.Count == records.Length && targets?.Count == records.Length && musters?.Count == records.Length)
                    for (int i = 0, offset = 0; i < records.Length; ++i)
                    {
                        int count = Math.Max(0, records[i]?.SurplusCount ?? 0);
                        if (surplus == null || count > surplus.Count - offset)
                            count = 0;
                        if (armies[i] != null && targets[i] != null && records[i] != null)
                        {
                            var assignment = new ReliefAssignment(targets[i], musters[i] == targets[i] ? null : musters[i], records[i]);
                            if (count > 0)
                                assignment.Surplus.AddRange(surplus!.Skip(offset).Take(count).Where(party => party != null && party.Army != armies[i]));
                            _relief[armies[i]] = assignment;
                        }
                        offset += count;
                    }
                _reliefResumed = _relief.Count != 0;
            }
            catch (JsonException error)
            {
                _log.Event($"Relief save metadata could not load; no saved orders will be replayed: {error.Message}.");
            }
        }

        private static bool Registered(Army army) => !army.IsDispersing && army.Kingdom?.Armies.Contains(army) == true &&
            army.LeaderParty?.Army == army;
        private static string ReliefId(Army army, ReliefAssignment assignment) =>
            $"kingdom={assignment.State.Kingdom} target={assignment.Target.StringId} army={CampaignLog.ArmyId(army)} " +
            $"owner={AssignmentOwner(assignment)} scope={(AssignmentOwner(assignment) == assignment.State.Kingdom ? "home" : "allied")}";

        private static string AssignmentOwner(ReliefAssignment assignment) => assignment.State.Owner ?? assignment.State.Kingdom;

        private void TickRelief(double now)
        {
            SiegeOrderTrace.ReliefStart(this, _relief.Keys, now);
            if (!_reliefReview && !_reliefPlan && now < _nextReliefPlan)
                return;
            using var timing = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.Relief);
            // In a quiet campaign only touch the native fortification list on
            // scheduled reviews. Never move parties from inside event callbacks.
            _reliefReview = false;
            bool plan = _reliefPlan || now >= _nextReliefPlan;
            _reliefPlan = false;
            if (plan)
                _nextReliefPlan = now + ReliefPlanInterval;
            _reliefObservations.Clear();
            var known = Kingdom.All.ToArray();
            var besieged = KingdomCommander.BesiegedFortifications(known);
            if (_relief.Count == 0 && _activeRelief.Count == 0 && _failedRelief.Count == 0 &&
                besieged.Length == 0)
                return;
            var jobs = known.Select(kingdom => KingdomCommander.ReliefObjectives(kingdom, besieged))
                .Where(list => list.Length > 0).ToArray();
            var objectives = jobs.SelectMany(list => list).ToDictionary(job => (job.Kingdom, job.Target));
            if (_reliefResumed)
            {
                _reliefResumed = false;
                _log.Event($"Relief loaded {_relief.Count} assigned formation(s); no payments or summons replayed.");
            }
            if (_failedRelief.Count != 0)
            {
                var active = new HashSet<string>(objectives.Values.Select(job => $"{job.Target.StringId}|{job.Kingdom.StringId}"), StringComparer.Ordinal);
                foreach (var key in _failedRelief.Keys.Where(key => !active.Contains(string.Join("|", key.Split('|').Take(2)))).ToArray())
                    _failedRelief.Remove(key);
            }
            foreach (var ended in _activeRelief.Where(pair => !objectives.TryGetValue(pair.Key, out var current) ||
                current.Owner != pair.Value.Owner || current.Siege != pair.Value.Siege || current.Besieger != pair.Value.Besieger).ToArray())
            {
                var oldJob = ended.Value;
                var target = oldJob.Target;
                var kingdom = oldJob.Kingdom;
                string outcome = objectives.TryGetValue(ended.Key, out var replacement) && replacement.Owner == oldJob.Owner ?
                    "native-siege-or-besieger-replaced" : ObjectiveEnd(target, kingdom, oldJob.Owner.StringId);
                _log.Event($"Relief objective ended: kingdom={kingdom.StringId} target={target.StringId} scope={oldJob.Scope} " +
                    $"outcome={outcome} currentOwner={target.MapFaction?.StringId ?? "-"}.");
                _activeRelief.Remove(ended.Key);
                _pendingReliefEtaSwitches.Remove((kingdom, oldJob.IsHome));
                _log.Changed("relief-allocation", oldJob.Key, null);
                foreach (var key in _failedRelief.Keys.Where(key => key.StartsWith($"{target.StringId}|{kingdom.StringId}|", StringComparison.Ordinal)).ToArray())
                    _failedRelief.Remove(key);
            }
            foreach (var pair in objectives)
            {
                if (!_activeRelief.ContainsKey(pair.Key))
                {
                    var job = pair.Value;
                    _log.Event($"Relief objective opened: kingdom={job.Kingdom.StringId} target={job.Target.StringId} scope={job.Scope} " +
                        $"owner={job.Owner.StringId} besieger={job.Besieger.StringId} " +
                        $"type={(job.Target.IsTown ? "town" : "castle")} nativeValue={CampaignLog.Number(job.Value)} " +
                        $"campLeader={job.CampLeader.StringId} campPower={CampaignLog.Number(job.CampPower)} " +
                        $"ageHours={CampaignLog.Number(job.Siege.SiegeStartTime.ElapsedHoursUntilNow)} " +
                        $"execution={(job.HasLandCamp ? "native-land-defense" : "unsupported-naval-camp")}.");
                }
                // Intact reassignments between planning passes need the latest camp power, not the opening-day estimate.
                _activeRelief[pair.Key] = pair.Value;
            }
            foreach (var pair in _relief.ToArray())
            {
                var army = pair.Key;
                var assignment = pair.Value;
                bool registered = Registered(army);
                if (!registered)
                {
                    ReconcileReliefSummons(army, assignment, registered: false);
                    if (assignment.Surplus.Count == 0)
                    {
                        _log.Event($"Relief assignment reconciled: {ReliefId(army, assignment)} native army no longer registered.");
                        _relief.Remove(army);
                        var formerJob = objectives.Values.FirstOrDefault(job => job.Target == assignment.Target &&
                            job.Kingdom.StringId == assignment.State.Kingdom && job.Owner.StringId == AssignmentOwner(assignment));
                        if (formerJob != null)
                            _failedRelief[FailureKey(formerJob, army.LeaderParty, assignment.Muster)] = ReliefInputs(KingdomForces.Read(formerJob.Kingdom));
                    }
                    continue;
                }
                if (army.Kingdom.StringId != assignment.State.Kingdom || army.LeaderParty.IsMainParty ||
                    ArmyTakeoverBehavior.Current?.IsPending(army) == true)
                {
                    _log.Event($"Relief assignment reconciled: {ReliefId(army, assignment)} no longer registered or under commander authority; no release.");
                    _relief.Remove(army);
                    continue;
                }
                bool objectiveActive = objectives.TryGetValue((army.Kingdom, assignment.Target), out var job) &&
                    job.Owner.StringId == AssignmentOwner(assignment) && job.StillEligible;
                if (!OwnsReliefOrder(army, assignment) && (!objectiveActive || assignment.State.End != null) && !IdleReliefArmy(army))
                {
                    _log.Event($"Relief ended with external order: {ReliefId(army, assignment)} no release or order change.");
                    _relief.Remove(army);
                    continue;
                }
                if (assignment.State.End != null)
                    continue;
                if (!objectiveActive)
                {
                    TraceReliefRecovery(army, assignment, "objective-no-longer-active", now);
                    EndRelief(army, assignment, ObjectiveEnd(assignment.Target, army.Kingdom, AssignmentOwner(assignment)));
                }
                else
                    ReviewRelief(army, assignment, job!, now);
            }
            // A route invalidated by live movement gets an immediate chance to
            // redirect intact before the stand-down pass releases the roster.
            if (_reliefPlan)
            {
                plan = true;
                _reliefPlan = false;
                _nextReliefPlan = now + ReliefPlanInterval;
            }
            if (plan)
                foreach (var list in jobs)
                    AllocateRelief(list, now);
            _fallenReliefTargets.Clear();
            foreach (var pair in _relief.ToArray())
            {
                if (pair.Value.State.End is not string reason || !Registered(pair.Key))
                    continue;
                var kingdom = pair.Key.Kingdom;
                if (reason == "siege-ended-owner-retained" && pair.Value.State.Reached && pair.Value.Surplus.Count == 0 &&
                    TrySiegeFollowOn(pair.Key, pair.Value.Target, now, pair.Value.State.UpkeepAfter))
                {
                    _relief.Remove(pair.Key);
                    continue;
                }
                var failureKey = pair.Value.Muster != null && !pair.Value.State.Departed &&
                    objectives.TryGetValue((kingdom, pair.Value.Target), out var remainingJob) &&
                    remainingJob.Owner.StringId == AssignmentOwner(pair.Value) ?
                    FailureKey(remainingJob, pair.Key.LeaderParty, pair.Value.Muster) : null;
                if (pair.Value.Surplus.Count != 0)
                    ReconcileReliefSummons(pair.Key, pair.Value, registered: true);
                if (reason is not ("no-eligible-command-or-strength" or "native-formation-failed" or "relief-travel-limit") &&
                    pair.Value.Surplus.Count == 0 &&
                    (OwnsReliefOrder(pair.Key, pair.Value) || IdleReliefArmy(pair.Key)) &&
                    BeginIdle(pair.Key, now, reason, failedReliefKey: failureKey))
                {
                    _log.Changed("relief-release", CampaignLog.ArmyId(pair.Key), null);
                    _reliefPlan = true;
                    continue;
                }
                if (ArmyOrders.Release(pair.Key, _log, "relief:" + reason))
                {
                    ReconcileReliefSummons(pair.Key, pair.Value, registered: false);
                    if (pair.Value.Surplus.Count == 0)
                        _relief.Remove(pair.Key);
                    // Cache after release so the membership change from closing
                    // the army is not mistaken for new recruitment inputs.
                    if (failureKey != null)
                        _failedRelief[failureKey] = ReliefInputs(KingdomForces.Read(kingdom));
                    _reliefPlan = true;
                }
                else
                    TraceDeferredReliefRelease(pair.Key, pair.Value, reason);
            }
        }

        private void TraceDeferredReliefRelease(Army army, ReliefAssignment assignment, string reason)
        {
            var blocked = ArmyOrders.Members(army).Select(party => (party, blocker: ArmyOrders.ReleaseBlocker(party)))
                .Where(row => row.blocker != null).OrderBy(row => row.party.StringId, StringComparer.Ordinal);
            string blockers = string.Join(", ", blocked.Select(row =>
            {
                var party = row.party;
                string detail = row.blocker switch
                {
                    "native-event" => $"battle={party.MapEvent?.Component?.GetType().Name ?? (party.MapEvent == null ? "-" : "unknown")} " +
                        $"finalized={party.MapEvent?.IsFinalized} siege={party.SiegeEvent?.BesiegedSettlement?.StringId ?? "-"} " +
                        $"insideBesieged={(party.CurrentSettlement?.IsUnderSiege == true ? party.CurrentSettlement.StringId : "-")}",
                    "party-control-lock" => $"quest={party.IsCurrentlyUsedByAQuest} decisionsDisabled={party.Ai?.DoNotMakeNewDecisions}",
                    "native-movement-lock" => $"aiDisabled={party.Ai?.IsDisabled} shortTerm={party.ShortTermBehavior}",
                    _ => ""
                };
                return $"{party.StringId}:{row.blocker}{(detail.Length == 0 ? "" : $"({detail})")}";
            }));
            if (army.IsDispersing)
                blockers = blockers.Length == 0 ? "army-dispersing" : "army-dispersing, " + blockers;
            if (blockers.Length == 0)
                blockers = "native-release-incomplete";
            if (_log.Changed("relief-release", CampaignLog.ArmyId(army), $"{assignment.Target.StringId}:{reason}:{blockers}"))
                _log.Event($"Relief release deferred: {ReliefId(army, assignment)} reason={reason} blockers=[{blockers}]; no forced release.");
        }

        private string ObjectiveEnd(Settlement target, Kingdom kingdom, string expectedOwner) =>
            _fallenReliefTargets.Contains(target) && target.MapFaction?.StringId != expectedOwner
                ? "settlement-fell-by-native-siege" : MilitaryObjective.ReliefBlocker(target, kingdom, expectedOwner) ?? "objective-changed";

        private void EndRelief(Army army, ReliefAssignment assignment, string reason)
        {
            assignment.State.End = reason;
            _log.Event($"Relief assignment ending: {ReliefId(army, assignment)} reason={reason} " +
                $"phase={(assignment.State.Departed ? "departed" : "muster")} order={army.LeaderParty.DefaultBehavior} " +
                $"owner={assignment.Target.MapFaction?.StringId ?? "-"}; safe release or reassignment.");
        }

        private static string FailureKey(MilitaryObjective job, MobileParty leader, Settlement? muster) =>
            $"{job.Target.StringId}|{job.Kingdom.StringId}|{leader.StringId}|{muster?.StringId ?? "*"}";

        private static string ReliefInputs(KingdomForces forces)
        {
            var inputs = string.Join(";", forces.Parties.Where(row => row.Army == null && row.Exclusion == null)
                .Select(row => $"{row.Party.StringId}:{(int)(row.Strength / 25f)}:{(int)row.Party.LeaderHero.Clan.Influence}:" +
                    $"{(int)(row.Position.X / 12f)}:{(int)(row.Position.Y / 12f)}"));
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(inputs)));
        }

        private static bool OwnsReliefOrder(Army army, ReliefAssignment assignment)
        {
            var leader = army.LeaderParty;
            var expected = assignment.State.Departed ? assignment.Target : assignment.Muster;
            return army.AiBehaviorObject == expected &&
                ((leader.TargetSettlement == expected && leader.DefaultBehavior ==
                    (assignment.State.Departed ? AiBehavior.DefendSettlement : AiBehavior.GoToSettlement)) ||
                    ClearedReliefOrder(leader));
        }

        private static bool ClearedReliefOrder(MobileParty leader) => leader.DefaultBehavior == AiBehavior.Hold &&
            leader.TargetSettlement == null && leader.TargetParty == null;

        private static bool IdleReliefArmy(Army army) => army.AiBehaviorObject == null &&
            army.LeaderParty.TargetSettlement == null && army.LeaderParty.TargetParty == null &&
            army.LeaderParty.DefaultBehavior is AiBehavior.Hold or AiBehavior.None;

        private void ReconcileReliefSummons(Army army, ReliefAssignment assignment, bool registered)
        {
            if (registered && ArmyOrders.ReleaseBlocker(army.LeaderParty) != null)
                return;
            for (int i = assignment.Surplus.Count - 1; i >= 0; --i)
                if (ArmyOrders.CancelSurplus(army, assignment.Surplus[i], registered, _log) == null)
                    assignment.Surplus.RemoveAt(i);
        }

        internal bool TryReliefTraceSubject(MobileParty leader, string target, out SiegeOrderTrace.Subject subject)
        {
            if (!_fixtureOnly && !leader.IsMainParty && leader.Army is Army army && army.LeaderParty == leader &&
                _relief.TryGetValue(army, out var assignment) && assignment.Target.StringId == target &&
                assignment.State.Departed && assignment.State.End == null)
            {
                subject = ReliefTraceSubject(army, assignment);
                return true;
            }
            subject = default;
            return false;
        }

        private static SiegeOrderTrace.Subject ReliefTraceSubject(Army army, ReliefAssignment assignment) =>
            new(army, assignment.Target, assignment.Target, "-", march: false, reissues: assignment.State.Reissues, relief: true);

        private void TraceReliefRecovery(Army army, ReliefAssignment assignment, string decision, double now,
            double? eta = null, string? visitLock = null, bool memberLock = false)
        {
            if (assignment.State.Departed && SiegeOrderTrace.WatchesRelief(this, army.LeaderParty, assignment.Target))
                SiegeOrderTrace.ReliefReview(this, ReliefTraceSubject(army, assignment), decision, now,
                    assignment.State.ReissueAfter, eta, visitLock, memberLock);
        }

        private void ReviewRelief(Army army, ReliefAssignment assignment, MilitaryObjective job, double now)
        {
            var leader = army.LeaderParty;
            var members = ArmyOrders.Members(army);
            var state = assignment.State;
            if (members.Any(party => party.IsMainParty))
            {
                // Resume only after the player's involvement ends.
                ObserveRelief(army, job, "player-protected", 0, 0);
                TraceReliefRecovery(army, assignment, "player-protected", now);
                return;
            }
            bool tactical = ReliefFormationLocked(army, members);
            float attached = ReliefAttached(army, job.Kingdom, assignment);
            if (state.Departed)
                TraceDefendControl(army, assignment, members);
            if (state.Departed && ArmyOrders.HasDefendOrder(army, job.Target))
            {
                bool combat = job.InReliefBattle(leader);
                float local = combat || (job.IsLocal(leader) && !tactical && ReadyForReliefCredit(army, job)) ? attached : 0f;
                bool engaging = leader.ShortTermBehavior == AiBehavior.EngageParty &&
                    leader.ShortTermTargetParty?.BesiegerCamp == job.Siege.BesiegerCamp;
                string phase = combat ? "relief-battle" : local <= 0 ? (tactical || ArmyOrders.VisitLock(leader) != null ? "native-event-held" : "en-route") :
                    engaging ? "local-engaging" :
                    !leader.IsMoving || leader.ShortTermBehavior == AiBehavior.Hold ? "local-waiting" : "local-approach";
                var displacement = leader.Position.ToVec2() - new Vec2(state.DepartureX, state.DepartureY);
                bool nativeWait = leader.ShortTermBehavior == AiBehavior.Hold ||
                    (leader.PartyMoveMode == MoveModeType.Point && leader.MoveTargetPoint != leader.TargetPosition &&
                        leader.MoveTargetPoint.DistanceSquared(job.CampPosition.ToVec2()) <= job.LocalRadius * job.LocalRadius);
                bool physical = local > 0 && (combat || (now > state.DepartureHour &&
                    (engaging || nativeWait || displacement.LengthSquared > 0.25f)));
                state.Reached |= physical;
                ObserveRelief(army, job, phase, attached, local, physical: physical);
            }
            if (tactical)
            {
                // A cleared order can leave native flee circling its own camp.
                // Restore the existing assignment only; never unlock release, upkeep,
                // or orders during a real encounter or for a different threat.
                if (state.Departed && army.AiBehaviorObject == job.Target && ClearedReliefOrder(leader) &&
                    leader.IsFleeing() && leader.ShortTermTargetParty?.BesiegerCamp == job.Siege.BesiegerCamp &&
                    attached > 0f && ArmyOrders.CanDirect(leader, job.Kingdom) &&
                    !ReliefFormationLocked(army, members, ignoreLeaderFleeing: true) &&
                    ArmyOrders.VisitLock(leader, ignoreFleeing: true) == null)
                    ReviewDepartedRelief(army, assignment, job, now, restoringDuringCampFlee: true);
                else
                    TraceReliefRecovery(army, assignment, "formation-locked", now, memberLock: true);
                return;
            }
            if (!ArmyOrders.CanDirect(leader, job.Kingdom) || attached <= 0f)
            {
                TraceReliefRecovery(army, assignment, "no-eligible-command-or-strength", now);
                EndRelief(army, assignment, "no-eligible-command-or-strength");
                return;
            }
            if (!OwnsReliefOrder(army, assignment) && !(army.AiBehaviorObject == null && ClearedReliefOrder(leader)))
            {
                TraceReliefRecovery(army, assignment, "external-order", now);
                _log.Event($"Relief lost order authority: {ReliefId(army, assignment)} actual={leader.DefaultBehavior} " +
                    $"target={leader.TargetSettlement?.StringId ?? "-"}; not overriding external order.");
                _relief.Remove(army);
                return;
            }
            ReconcileReliefSummons(army, assignment, registered: true);
            foreach (var party in members.Where(party => party != leader && !ArmyOrders.CanDirect(party, job.Kingdom)))
                ArmyOrders.ReleaseMember(army, party, _log, "home-relief-member-ineligible");
            if (ArmyOrders.VisitLock(leader) is string visitLock)
            {
                TraceReliefRecovery(army, assignment, "visit-locked", now, visitLock: visitLock);
                return;
            }
            if (state.Departed)
            {
                ReviewDepartedRelief(army, assignment, job, now);
                return;
            }
            var muster = assignment.Muster!;
            if (!KingdomCommander.OpenFortification(muster, job.Kingdom))
            {
                EndRelief(army, assignment, "muster-no-longer-available");
                return;
            }
            double march = KingdomCommander.ReliefMarchFromMuster(leader, muster, job);
            double earliest = KingdomCommander.ReliefMusterHours(leader, muster) + march;
            if (!KingdomCommander.WithinReliefRange(earliest))
            {
                EndReliefForTravel(army, assignment, earliest);
                return;
            }
            if (leader.CurrentSettlement == muster)
                foreach (var party in members)
                    ArmyOrders.MergeAtMuster(army, party, muster);
            else if (leader.DefaultBehavior == AiBehavior.Hold && now >= state.ReissueAfter)
            {
                if (state.Reissues >= 2 || !ArmyOrders.CanPathVisit(leader, muster))
                {
                    EndRelief(army, assignment, "cleared-muster-order-not-recoverable");
                    return;
                }
                state.Reissues++;
                state.ReissueAfter = now + 1d;
                ArmyOrders.Visit(army, muster);
                _log.Event($"Relief native muster reissued: {ReliefId(army, assignment)} retry={state.Reissues}.");
            }
            var arriving = KingdomCommander.TimelyReliefArrivals(army, job, muster, march).ToArray();
            attached = ReliefAttached(army, job.Kingdom, assignment);
            ObserveRelief(army, job, "mustering", attached, 0f, arriving.Length);
            if (leader.CurrentSettlement != muster)
            {
                if (leader.DefaultBehavior == AiBehavior.GoToSettlement && leader.TargetSettlement == muster)
                    SustainRelief(army, assignment, now);
                return;
            }
            var other = Coverage(job, army);
            if (!job.Sufficient(attached + other.committed + other.support) && arriving.Length != 0)
            {
                SustainRelief(army, assignment, now);
                return;
            }
            if (!ArmyOrders.CanRouteToRelief(leader, muster.GatePosition, job.CampPosition))
            {
                EndRelief(army, assignment, "no-current-land-path-from-muster");
                return;
            }
            DepartRelief(army, assignment, job, now, attached, attached + other.committed + other.support);
        }

        private void ReviewDepartedRelief(Army army, ReliefAssignment assignment, MilitaryObjective job, double now,
            bool restoringDuringCampFlee = false)
        {
            var leader = army.LeaderParty;
            var state = assignment.State;
            double hours = KingdomCommander.ReliefHours(leader, job);
            if (!job.IsLocal(leader) && !KingdomCommander.WithinReliefRange(hours))
            {
                TraceReliefRecovery(army, assignment, "relief-travel-limit", now, eta: hours);
                if (!restoringDuringCampFlee)
                    EndReliefForTravel(army, assignment, hours);
                return;
            }
            if (!ArmyOrders.HasDefendOrder(army, job.Target))
            {
                if (now < state.ReissueAfter)
                {
                    TraceReliefRecovery(army, assignment, "reissue-cooldown", now, eta: hours);
                    return;
                }
                bool exhausted = state.Reissues >= 2;
                if (exhausted || !ArmyOrders.CanRouteToRelief(leader, KingdomCommander.Origin(leader), job.CampPosition))
                {
                    TraceReliefRecovery(army, assignment, exhausted ? "reissue-budget-exhausted" : "reissue-path-unavailable", now, eta: hours);
                    if (!restoringDuringCampFlee)
                        EndRelief(army, assignment, "cleared-defend-order-not-recoverable");
                    return;
                }
                state.Reissues++;
                state.ReissueAfter = now + 1d;
                ArmyOrders.Defend(army, job.Target);
                _log.Event($"Relief native defend reissued: {ReliefId(army, assignment)} retry={state.Reissues} " +
                    $"restored={ArmyOrders.HasDefendOrder(army, job.Target)}" + (restoringDuringCampFlee ? " reason=own-camp-flee." : "."));
                TraceReliefRecovery(army, assignment, "reissue-attempted", now, eta: hours);
            }
            else
                TraceReliefRecovery(army, assignment, "defend-order-intact", now, eta: hours);
            if (!restoringDuringCampFlee)
                SustainRelief(army, assignment, now);
        }

        private void EndReliefForTravel(Army army, ReliefAssignment assignment, double hours)
        {
            _log.Event($"Relief travel limit: {ReliefId(army, assignment)} eta={CampaignLog.Number(hours)}h " +
                $"limit={KingdomCommander.MaxReliefHours}h; reconsider intact or release.");
            EndRelief(army, assignment, "relief-travel-limit");
            _reliefPlan = true;
        }

        private static float ReliefAttached(Army army, Kingdom kingdom, ReliefAssignment? assignment = null) => ArmyOrders.Members(army).Where(party =>
            party.Army == army && (party == army.LeaderParty || party.AttachedTo == army.LeaderParty) &&
            (assignment == null || !assignment.Surplus.Contains(party)) && ArmyOrders.CanDirect(party, kingdom))
            .Sum(party => MilitaryObjective.Power(party.Party));

        private void ObserveRelief(Army army, MilitaryObjective job, string phase, float attached, float local, int travelers = 0, bool physical = false)
        {
            _reliefObservations.Add(new ReliefObservation(army, job, phase, attached, local, physical));
            string detail = $"{phase}:{travelers}:{(int)(attached / 25f)}:{local > 0}:{physical}";
            if (_log.Changed("relief-execution", CampaignLog.ArmyId(army), detail))
                _log.Event($"Relief execution: kingdom={job.Kingdom.StringId} target={job.Target.StringId} army={CampaignLog.ArmyId(army)} " +
                    $"phase={phase} attached={CampaignLog.Number(attached)} local={CampaignLog.Number(local)} travelers={travelers} physical={physical} " +
                    $"camp={CampaignLog.Number(job.CampPower)} radius={CampaignLog.Number(job.LocalRadius)} " +
                    $"position={Position(army.LeaderParty.Position)} shortTerm={army.LeaderParty.ShortTermBehavior}.");
        }

        private void SustainRelief(Army army, ReliefAssignment assignment, double now)
        {
            if (army.Cohesion >= 50 || now < assignment.State.UpkeepAfter ||
                ReliefFormationLocked(army, ArmyOrders.Members(army)))
                return;
            assignment.State.UpkeepAfter = now + 1d;
            ArmyOrders.Sustain(army, "relief:" + assignment.Target.StringId, _log);
        }

        private static bool ReliefFormationLocked(Army army, IEnumerable<MobileParty> members, bool ignoreLeaderFleeing = false) =>
            members.Any(party => party.IsMainParty || ((party == army.LeaderParty || party.AttachedTo == army.LeaderParty) &&
                ArmyOrders.ReleaseBlocker(party, ignoreFleeing: ignoreLeaderFleeing && party == army.LeaderParty) != null));

        private void TraceDefendControl(Army army, ReliefAssignment assignment, MobileParty[] members)
        {
            var leader = army.LeaderParty;
            var held = members.Where(p => p == leader || p.AttachedTo == leader || p.IsMainParty)
                .Select(p => (party: p, reason: ArmyOrders.ReleaseBlocker(p))).FirstOrDefault(row => row.reason != null);
            string moveLock = held.reason ?? ArmyOrders.VisitLock(leader) ?? "-";
            string state = $"assigned={assignment.Target.StringId} order={leader.DefaultBehavior} target={leader.TargetSettlement?.StringId ?? leader.TargetParty?.StringId ?? "-"} " +
                $"armyObjective={CampaignLog.PointId(army.AiBehaviorObject)} shortTerm={leader.ShortTermBehavior} " +
                $"contact={leader.ShortTermTargetParty?.StringId ?? "-"} lock={moveLock} heldParty={held.party?.StringId ?? "-"}";
            if (_log.Changed("relief-control", CampaignLog.ArmyId(army), state))
                _log.Event($"Relief order state: {ReliefId(army, assignment)} {state}.");
        }

        // Tactical waiting on an intact defend order is useful; a cleared order,
        // pursuit of an unrelated party, or a locked formation is not ready cover.
        private static bool ReadyForReliefCredit(Army army, MilitaryObjective job)
        {
            var leader = army.LeaderParty;
            return ArmyOrders.VisitLock(leader) == null || (leader.ShortTermBehavior is AiBehavior.EngageParty or AiBehavior.JoinParty &&
                leader.ShortTermTargetParty?.BesiegerCamp == job.Siege.BesiegerCamp && job.IsLocal(leader));
        }

        private (float committed, float local) FormationCoverage(MilitaryObjective job, Army army, ReliefAssignment assignment)
        {
            if (assignment.Target != job.Target || AssignmentOwner(assignment) != job.Owner.StringId ||
                assignment.State.End != null || army.Kingdom != job.Kingdom ||
                !Registered(army) || !OwnsReliefOrder(army, assignment))
                return (0, 0);
            var leader = army.LeaderParty;
            bool fighting = job.InReliefBattle(leader);
            var members = ArmyOrders.Members(army);
            if (members.Any(party => party.IsMainParty) || (!fighting && ReliefFormationLocked(army, members)))
                return (0, 0);
            float present = ReliefAttached(army, job.Kingdom, assignment);
            if (assignment.State.Departed)
            {
                if (fighting)
                    return (present, present);
                if (!ArmyOrders.HasDefendOrder(army, job.Target) || !ReadyForReliefCredit(army, job))
                    return (0, 0);
                return job.IsLocal(leader) ? (present, present) :
                    KingdomCommander.WithinReliefRange(KingdomCommander.ReliefHours(leader, job)) ? (present, 0) : (0, 0);
            }
            var muster = assignment.Muster;
            if (muster == null || !KingdomCommander.OpenFortification(muster, job.Kingdom))
                return (0, 0);
            double march = KingdomCommander.ReliefMarchFromMuster(leader, muster, job);
            return KingdomCommander.WithinReliefRange(KingdomCommander.ReliefMusterHours(leader, muster) + march) ?
                (present + KingdomCommander.TimelyReliefArrivals(army, job, muster, march).Sum(arrival => arrival.power), 0) : (0, 0);
        }

        private (float committed, float local, float support) Coverage(MilitaryObjective job, Army? exclude = null)
        {
            float committed = 0, local = 0, support = 0;
            foreach (var pair in _relief.Where(pair => pair.Key != exclude))
            {
                var share = FormationCoverage(job, pair.Key, pair.Value);
                committed += share.committed;
                local += share.local;
            }
            // Only observed, physically local parties already reacting to
            // this camp count as outside support. Never predict allied arrivals.
            var counted = new HashSet<Army>();
            var countedParties = new HashSet<MobileParty>();
            foreach (var kingdom in Kingdom.All.Where(kingdom => kingdom == job.Kingdom ||
                (!kingdom.IsAtWarWith(job.Kingdom) && !kingdom.IsAtWarWith(job.Owner) && kingdom.IsAtWarWith(job.Besieger))))
            foreach (var party in kingdom.WarPartyComponents.Select(component => component.MobileParty).Distinct())
            {
                if (party == null || party.MapFaction != kingdom || !countedParties.Add(party) || party.IsMainParty ||
                    party.AttachedTo != null || !(job.IsLocal(party) || job.InReliefBattle(party)) ||
                    !job.Responding(party) || party.SiegeEvent != null || (party.MapEvent != null && !job.InReliefBattle(party)))
                    continue;
                var other = party.Army;
                if (other != null)
                {
                    if (other.LeaderParty != party || !counted.Add(other))
                        continue;
                    if (!job.InReliefBattle(party) && ReliefFormationLocked(other, ArmyOrders.Members(other)))
                        continue;
                    if (_relief.TryGetValue(other, out var active))
                    {
                        // Own relief was counted above. A different kingdom's
                        // force counts only once it is observed in this fight.
                        if (kingdom == job.Kingdom || active.State.End != null || !active.State.Departed ||
                            active.Target != job.Target || AssignmentOwner(active) != job.Owner.StringId ||
                            MilitaryObjective.ReliefBlocker(job.Target, kingdom, job.Owner.StringId) != null ||
                            !ArmyOrders.HasDefendOrder(other, job.Target))
                            continue;
                        support += ReliefAttached(other, kingdom, active);
                        continue;
                    }
                    support += ReliefAttached(other, kingdom);
                }
                else if (ArmyOrders.CanDirect(party, kingdom) && (job.InReliefBattle(party) || ArmyOrders.ReleaseBlocker(party) == null))
                    support += MilitaryObjective.Power(party.Party);
            }
            return (committed, local, support);
        }

        private void AllocateRelief(MilitaryObjective[] jobs, double now)
        {
            using var timing = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.ReliefPlan);
            var kingdom = jobs[0].Kingdom;
            var priority = jobs.Select((job, index) => (job.Target, index)).ToDictionary(pair => pair.Target, pair => pair.index);
            Dictionary<Army, Settlement> localClaims = new();
            HashSet<Settlement> adequate = new();
            for (int rank = 0; rank < jobs.Length; rank++)
            {
                var job = jobs[rank];
                if (rank == 0 || jobs[rank - 1].IsHome != job.IsHome)
                    localClaims = MatchNearbyRelief(kingdom, job.IsHome, jobs.Skip(rank).TakeWhile(other => other.IsHome == job.IsHome)
                        .Where(other => other.HasLandCamp && other.StillEligible).ToArray(), now, out adequate);
                if (!job.HasLandCamp || !job.StillEligible)
                    continue;
                var coverage = Coverage(job);
                var current = coverage;
                float committed = coverage.committed + coverage.support;
                bool expectsResponder = false;
                if (localClaims.Count != 0)
                {
                    expectsResponder = localClaims.Any(p => p.Value == job.Target &&
                        (!_relief.TryGetValue(p.Key, out var a) || a.Target != job.Target || a.State.End != null));
                    foreach (var p in _relief.Where(p => p.Value.Target == job.Target &&
                        localClaims.TryGetValue(p.Key, out var destination) && destination != job.Target))
                        committed -= FormationCoverage(job, p.Key, p.Value).committed;
                    committed = Math.Max(0f, committed);
                }
                var blockers = new Dictionary<string, int>(StringComparer.Ordinal);
                void Count(string reason) => blockers[reason] = blockers.TryGetValue(reason, out int total) ? total + 1 : 1;
                if (!job.Sufficient(committed) || expectsResponder)
                {
                    var inventory = KingdomForces.Read(kingdom);
                    string? Unavailable(Army army)
                    {
                        if (localClaims.TryGetValue(army, out var near) && near != job.Target)
                            return adequate.Contains(near) ? "needed-at-viable-siege" : "needed-at-nearer-siege";
                        if (OwnsControlledFixture(army))
                            return "controlled-fixture";
                        if (ArmyTakeoverBehavior.Current?.IsPending(army) == true)
                            return "pending-legacy-release";
                        if (!Registered(army))
                            return "native-or-player-lock";
                        if (_sieges.TryGetValue(army, out var siege))
                            return SiegeDiversionBlocker(army, siege);
                        if (!ArmyOrders.CanRelease(army))
                            return "native-or-player-lock";
                        if (_relief.TryGetValue(army, out var assignment))
                        {
                            if (assignment.Surplus.Count != 0)
                                return "cancellation-pending";
                            bool available = assignment.State.End != null ? OwnsReliefOrder(army, assignment) || IdleReliefArmy(army) :
                                OwnsReliefOrder(army, assignment) && assignment.Target != job.Target &&
                                (near == job.Target || priority.TryGetValue(assignment.Target, out int lower) && lower > rank);
                            return available ? null :
                                priority.TryGetValue(assignment.Target, out int claimRank) && claimRank < rank ?
                                    "committed-higher-priority" : "committed";
                        }
                        return IdleReliefArmy(army) ? null : "external-order";
                    }
                    bool Available(Army army) => Unavailable(army) == null;
                    var ready = new List<KingdomCommander.ReliefForce>();
                    foreach (var army in inventory.Armies)
                    {
                        if (Unavailable(army) is string veto)
                        {
                            Count(veto);
                            continue;
                        }
                        var gathering = _relief.TryGetValue(army, out var currentAssignment) &&
                            currentAssignment.State.End == null && !currentAssignment.State.Departed ? currentAssignment.Muster : null;
                        if (gathering == null && _sieges.TryGetValue(army, out var offense) &&
                            !offense.State.Issued && offense.State.End == null)
                            gathering = offense.Muster;
                        var force = (gathering == null ? null : KingdomCommander.RedirectMuster(army, job, gathering)) ??
                            KingdomCommander.ReadyRelief(army, job, inventory, _sieges.ContainsKey(army));
                        if (force != null)
                            ready.Add(force);
                        else
                            Count("ready-army-not-routable-or-ineligible");
                    }
                    var ordered = ready.OrderBy(force => force.Ready).ThenBy(force => _sieges.ContainsKey(force.Army!))
                        .ThenBy(force => CampaignLog.ArmyId(force.Army!), StringComparer.Ordinal).ToArray();
                    var nearby = ordered.Where(f => localClaims.TryGetValue(f.Army!, out var dest) && dest == job.Target).ToArray();
                    string comparison = adequate.Contains(job.Target) ? "adequate-route-match" : "nearest-ready-to-uncovered-siege";
                    var selected = nearby.Length != 0 || job.Sufficient(committed) ? nearby : KingdomCommander.ChooseReliefFormations(job, ordered, committed,
                        jobs.Skip(rank + 1).Where(other => other.HasLandCamp).ToArray(),
                        other => { var current = Coverage(other); return current.committed + current.support; },
                        (army, other) => _relief.TryGetValue(army, out var assignment) ? FormationCoverage(other, army, assignment).committed : 0f,
                        out comparison);
                    float readyPower = selected.Sum(force => force.Power);
                    // Capture before dispatch; the existing failure fingerprint
                    // includes live influence and must not see a partial payment.
                    string? inputs = _failedRelief.Count == 0 ? null : ReliefInputs(inventory);
                    bool Failed(MobileParty leader, Settlement muster) =>
                        inputs != null &&
                        ((_failedRelief.TryGetValue(FailureKey(job, leader, muster), out var old) && old == inputs) ||
                        (_failedRelief.TryGetValue(FailureKey(job, leader, null), out old) && old == inputs));
                    bool Reserved(MobileParty party) => jobs.Take(rank + 1).Any(other => other.IsLocal(party) && other.Responding(party));
                    // Compare a fresh free-party muster with the ready response;
                    // in particular don't redirect a useful second army if neither
                    // can match a faster sufficient muster from unclaimed troops.
                    var quotes = new KingdomCommander.ReliefMusterQuotes(job);
                    float quotedCoverage = committed;
                    var freeMuster = job.Sufficient(committed) ? null :
                        KingdomCommander.MusterRelief(job, inventory, committed, quotes, null, Failed, Reserved, Count);
                    var fresh = freeMuster;
                    bool readyEnough = job.Sufficient(committed + readyPower);
                    bool freshEnough = fresh != null && job.Sufficient(committed + fresh.Power);
                    bool useFreshFirst = freshEnough && (selected.Length == 0 || (!readyEnough && nearby.Length == 0) ||
                        fresh!.Ready < (nearby.Length != 0 ? selected[0].Ready : selected[selected.Length - 1].Ready));
                    if (!readyEnough && !freshEnough && !job.Sufficient(committed + readyPower + (fresh?.Power ?? 0f)))
                    {
                        var readyArmies = new HashSet<Army>(ordered.Select(force => force.Army!));
                        var remuster = KingdomCommander.MusterRelief(job, inventory, committed, quotes,
                            army => !ProtectReliefRoster(army) && Available(army) &&
                                (!readyArmies.Contains(army) || _sieges.TryGetValue(army, out var siege) && siege.State.Issued) &&
                                (!_sieges.TryGetValue(army, out var offense) || offense.State.Issued || offense.State.End != null),
                            Failed, Reserved, Count);
                        if (remuster != null && remuster.Releases.Length != 0)
                        {
                            float retained = selected.Where(force => !remuster.Releases.Contains(force.Army!)).Sum(force => force.Power);
                            // An understrength response can use an intact army or
                            // free parties, but is not a reason to break an army up.
                            if (job.Sufficient(committed + retained + remuster.Power))
                            {
                                fresh = remuster;
                                useFreshFirst = retained == 0 || remuster.Releases.Any(readyArmies.Contains) ||
                                    job.Sufficient(committed + remuster.Power);
                            }
                            else
                                Count("remuster-still-understrength");
                        }
                    }
                    var priorOffense = selected.Any(force => _sieges.ContainsKey(force.Army!)) ||
                        (fresh?.Releases.Any(_sieges.ContainsKey) ?? false) ?
                        _sieges.Keys.Where(army => army.Kingdom == kingdom).ToArray() : Array.Empty<Army>();
                    if (useFreshFirst && StartRelief(job, fresh!, now, committed +
                        selected.Where(force => !fresh!.Releases.Contains(force.Army!)).Sum(force => force.Power)))
                    {
                        committed += fresh!.Power;
                        if (!job.Sufficient(committed) && fresh.Releases.Length != 0)
                            foreach (var force in selected.Where(force => !fresh.Releases.Contains(force.Army!)))
                            {
                                if (DispatchExisting(job, force, now))
                                    committed += force.Power;
                                if (job.Sufficient(committed))
                                    break;
                            }
                        if (jobs.Length > 1 || priorOffense.Length > 0)
                            LogCoordination(job, fresh.Releases.Length == 0 ? "fresh-muster" : "replacement-muster", selected,
                                priorOffense, new[] { fresh.Muster!.Leader.Party.Army! }.Where(army => army != null),
                                fresh.First, fresh.Ready);
                        current = Coverage(job);
                    }
                    else
                    {
                        if (selected.Length != 0)
                            _log.Event($"Relief response selected: kingdom={kingdom.StringId} target={job.Target.StringId} " +
                                $"formations=[{string.Join(", ", selected.Select(force => CampaignLog.ArmyId(force.Army!)))}] " +
                                $"firstETA=+{CampaignLog.Number(selected.Min(force => force.First))}h combinedETA=+{CampaignLog.Number(selected.Max(force => force.Ready))}h " +
                                $"existingCoverage={CampaignLog.Number(committed)} selectedPower={CampaignLog.Number(readyPower)} " +
                                $"selectedPresent={CampaignLog.Number(selected.Sum(force => force.Present))} " +
                                $"why={comparison}.");
#if CAMPAIGN_ACCEPTANCE
                        ObserveCoordinationContest(job, jobs.Skip(rank + 1), ordered, selected, committed, now);
#endif
                        var dispatched = selected.Length != 0 && (jobs.Length > 1 || priorOffense.Length > 0) ?
                            new List<Army>(selected.Length) : null;
                        foreach (var response in selected)
                            if (DispatchExisting(job, response, now))
                                dispatched?.Add(response.Army!);
                        if (dispatched is { Count: > 0 })
                            LogCoordination(job, dispatched.Count > 1 ? "joint-response" : "single-response",
                                selected.Where(force => dispatched.Contains(force.Army!)).ToArray(), priorOffense, dispatched);
                        // A failed order can still have changed the world (e.g.
                        // a replacement released one army before another failed).
                        bool attemptedOrders = useFreshFirst || selected.Length != 0;
                        current = attemptedOrders ? Coverage(job) : coverage;
                        committed = current.committed + current.support;
                        if (!job.Sufficient(committed))
                        {
                            if (attemptedOrders)
                            {
                                inventory = KingdomForces.Read(kingdom);
                                quotes = new KingdomCommander.ReliefMusterQuotes(job);
                            }
                            // The unadjusted coverage can differ from the first
                            // quote's reserved coverage even without a dispatch.
                            var extra = !attemptedOrders && committed == quotedCoverage ? freeMuster :
                                KingdomCommander.MusterRelief(job, inventory, committed, quotes, null, Failed, Reserved, Count);
                            if (fresh?.Releases.Length > 0 && job.Sufficient(committed + fresh.Power) &&
                                (extra == null || fresh.Power > extra.Power))
                                extra = fresh;
                            if (extra != null)
                            {
                                StartRelief(job, extra, now, committed);
                                current = Coverage(job);
                            }
                        }
                    }
                }
                string reason = job.Sufficient(current.committed + current.support) ? "covered-in-plan" :
                    current.committed > 0 ? "understrength-response-open" : "no-eligible-response-yet";
                string shape = $"{reason}:{(int)(current.committed / 25)}:{(int)(current.local / 25)}:{(int)(current.support / 25)}:" +
                    $"{(int)(job.CampPower / 25)}:{rank}:{string.Join(",", blockers.Keys.OrderBy(key => key))}";
                if (_log.Changed("relief-allocation", job.Key, shape))
                    _log.Event($"Relief review: kingdom={kingdom.StringId} target={job.Target.StringId} rank={rank + 1}/{jobs.Length} " +
                        $"scope={job.Scope} owner={job.Owner.StringId} type={(job.Target.IsTown ? "town" : "castle")} nativeValue={CampaignLog.Number(job.Value)} why={reason} " +
                        $"campObserved={CampaignLog.Number(job.CampPower)} committed={CampaignLog.Number(current.committed)} " +
                        $"localOwned={CampaignLog.Number(current.local)} localOther={CampaignLog.Number(current.support)} " +
                        $"localOver120={job.Sufficient(current.local + current.support)} " +
                        $"blockers=[{string.Join(", ", blockers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}]; " +
                        $"armyTravelLimit={KingdomCommander.MaxReliefHours}h; objective stays open.");
            }
        }

        private bool TryReadyDefense(Army army, double now, SiegeAssignment? withdrawn = null)
        {
            var kingdom = army.Kingdom;
            KingdomForces? inventory = null;
            var options = _activeRelief.Values.Where(j => j.Kingdom == kingdom && j.HasLandCamp && j.StillEligible)
                .Select(job => (job, force: KingdomCommander.ReadyRelief(army, job, inventory ??= KingdomForces.Read(kingdom)))).Where(row => row.force != null)
                .Select(row => (row.job, row.force, covered: Coverage(row.job)))
                .Where(row => !row.job.Sufficient(row.covered.committed + row.covered.support))
                .OrderByDescending(row => row.job.IsHome)
                .ThenByDescending(row => row.job.Sufficient(row.covered.local + row.covered.support + row.force!.Power))
                .ThenBy(row => row.force!.Ready).ThenByDescending(row => row.job.Target.IsTown)
                .ThenByDescending(row => row.job.Value).ThenBy(row => row.job.Target.StringId, StringComparer.Ordinal);
            foreach (var (defense, force, _) in options)
            {
                if (withdrawn != null)
                    _sieges.Remove(army);
                if (DispatchExisting(defense, force!, now))
                    return true;
                if (withdrawn != null)
                    _sieges[army] = withdrawn;
            }
            return false;
        }

        private bool DispatchExisting(MilitaryObjective job, KingdomCommander.ReliefForce force, double now)
        {
            var army = force.Army!;
            _sieges.TryGetValue(army, out var offense);
            if (!Registered(army) || OwnsControlledFixture(army) || ArmyTakeoverBehavior.Current?.IsPending(army) == true ||
                (offense == null ? !ArmyOrders.CanRelease(army) : SiegeDiversionBlocker(army, offense) != null) ||
                !job.StillEligible || !KingdomCommander.WithinReliefRange(force.Ready))
                return false;
            var prior = _relief.TryGetValue(army, out var old) ? old.Target.StringId : offense?.Target.StringId ?? "idle";
            if (old != null && (!OwnsReliefOrder(army, old) && !(old.State.End != null && IdleReliefArmy(army))))
                return false;
            if (force.RetainedMuster is Settlement site)
            {
                if ((old == null ? offense == null || offense.State.Issued || offense.State.End != null :
                        old.State.End != null || old.State.Departed) ||
                    !KingdomCommander.OpenFortification(site, job.Kingdom) ||
                    !ArmyOrders.CanPathVisit(army.LeaderParty, site) ||
                    !ArmyOrders.CanRouteToRelief(army.LeaderParty, site.GatePosition, job.CampPosition))
                    return false;
                if (old == null)
                {
                    old = new ReliefAssignment(job.Target, offense!.Muster, new ReliefState
                    {
                        Kingdom = job.Kingdom.StringId, UpkeepAfter = offense.State.UpkeepAfter
                    });
                    _relief[army] = old;
                    _sieges.Remove(army);
                    _log.Event($"Siege muster diverted: kingdom={job.Kingdom.StringId} previous={prior} defense={job.Target.StringId}; retained paid summons.");
                }
                bool changedSite = old.Muster != site;
                if (changedSite)
                {
                    ArmyOrders.Visit(army, site);
                    old.State.Reissues = 0;
                    old.State.ReissueAfter = 0;
                }
                old.Target = job.Target;
                old.Muster = site;
                old.State.Owner = job.Owner.StringId;
                _log.Event($"Relief muster redirected: {ReliefId(army, old)} previous={prior} muster={site.StringId} " +
                    $"musterChanged={changedSite} present={CampaignLog.Number(force.Present)} projected={CampaignLog.Number(force.Power)} " +
                    $"combinedETA=+{CampaignLog.Number(force.Ready)}h; retained native membership and escort orders; no new summons or payment.");
                ReviewRelief(army, old, job, now);
                return true;
            }
            if (offense != null)
            {
                if (!LeaveSiege(army, offense, $"higher-priority-{job.Scope}-defense({job.Target.StringId})", now))
                    return false;
                _sieges.Remove(army);
                if (!job.StillEligible)
                {
                    if (!BeginIdle(army, now, "defense-no-longer-eligible-after-diversion"))
                        ArmyOrders.Release(army, _log, "defense-no-longer-eligible-after-diversion");
                    return false;
                }
            }
            var assignment = new ReliefAssignment(job.Target, null, new ReliefState
            {
                Kingdom = job.Kingdom.StringId, Owner = job.Owner.StringId, UpkeepAfter = old?.State.UpkeepAfter ?? offense?.State.UpkeepAfter ?? 0d
            });
            _relief[army] = assignment;
            EndIdle(army, "relief-assigned");
            _log.Event($"Relief reuses formation: {ReliefId(army, assignment)} previous={prior} " +
                $"present={CampaignLog.Number(force.Power)} firstETA=+{CampaignLog.Number(force.First)}h " +
                $"type={(job.Target.IsTown ? "town" : "castle")} nativeValue={CampaignLog.Number(job.Value)}.");
            DepartRelief(army, assignment, job, now, force.Present, force.Present);
            return true;
        }

        // A relief army can be redirected intact. A tactical interruption can
        // remove its ready strength, but doesn't make its roster free to remuster.
        private bool ProtectReliefRoster(Army army) =>
            _relief.TryGetValue(army, out var assignment) && assignment.State.End is null or "relief-travel-limit";

        private bool StartRelief(MilitaryObjective job, KingdomCommander.ReliefForce force, double now, float otherCoverage)
        {
            var plan = force.Muster!;
            if (!job.StillEligible ||
                !KingdomCommander.OpenFortification(plan.Muster, job.Kingdom) ||
                !KingdomCommander.WithinReliefRange(force.Ready) ||
                (force.Releases.Length != 0 && !job.Sufficient(otherCoverage + force.Power)) ||
                force.Releases.Any(army => ProtectReliefRoster(army) || OwnsControlledFixture(army) || !Registered(army) ||
                    ArmyTakeoverBehavior.Current?.IsPending(army) == true ||
                    (_sieges.TryGetValue(army, out var siege) ? SiegeDiversionBlocker(army, siege) != null : !ArmyOrders.CanRelease(army))))
                return false;
            foreach (var old in force.Releases)
            {
                if (_sieges.TryGetValue(old, out var siege) &&
                    !LeaveSiege(old, siege, $"higher-priority-{job.Scope}-defense-remuster({job.Target.StringId})", now))
                    return false;
                if (!ArmyOrders.Release(old, _log, "home-relief-remuster:" + job.Target.StringId))
                    return false;
                EndIdle(old, "home-relief-remuster");
                _relief.Remove(old);
                _sieges.Remove(old);
            }
            var leader = plan.Leader.Party;
            if (!job.StillEligible || ArmyOrders.FreePartyBlocker(leader, job.Kingdom, true) != null || ArmyOrders.VisitLock(leader) != null ||
                !ArmyOrders.CanPathVisit(leader, plan.Muster) || !ArmyOrders.CanRouteToRelief(leader, plan.Muster.GatePosition, job.CampPosition))
                return false;
            var army = ArmyOrders.Create(job.Kingdom, leader, plan.Muster, _log, out bool formed);
            var assignment = new ReliefAssignment(job.Target, plan.Muster, new ReliefState
            {
                Kingdom = job.Kingdom.StringId, Owner = job.Owner.StringId
            });
            _relief[army] = assignment;
            if (!formed)
            {
                EndRelief(army, assignment, "native-formation-failed");
                return false;
            }
            var called = new List<string>();
            foreach (var call in plan.Calls)
            {
                if (!job.StillEligible)
                    break;
                if (KingdomCommander.WithinCallRange(leader, call.Party) && ArmyOrders.Summon(army, call.Party, _log))
                    called.Add(call.Party.StringId);
            }
            _log.Event($"Relief muster issued: {ReliefId(army, assignment)} muster={plan.Muster.StringId} " +
                $"present={CampaignLog.Number(MilitaryObjective.Power(leader.Party))} planned={CampaignLog.Number(force.Power)} " +
                $"firstETA=+{CampaignLog.Number(force.First)}h combinedETA=+{CampaignLog.Number(force.Ready)}h " +
                $"calls=[{string.Join(", ", called)}] remustered=[{string.Join(", ", force.Releases.Select(CampaignLog.ArmyId))}].");
            if (job.StillEligible)
                ReviewRelief(army, assignment, job, now);
            else
                EndRelief(army, assignment, ObjectiveEnd(job.Target, job.Kingdom, job.Owner.StringId));
            return true;
        }

        private void DepartRelief(Army army, ReliefAssignment assignment, MilitaryObjective job, double now, float present, float projected)
        {
            ArmyOrders.Defend(army, job.Target);
            var state = assignment.State;
            state.Departed = true;
            state.DepartureHour = now;
            state.DepartureX = army.LeaderParty.Position.X;
            state.DepartureY = army.LeaderParty.Position.Y;
            state.Reissues = 0;
            state.ReissueAfter = 0;
            _log.Event($"Relief departure: {ReliefId(army, assignment)} order={army.LeaderParty.DefaultBehavior} " +
                $"orderTarget={army.LeaderParty.TargetSettlement?.StringId ?? "-"} attached={CampaignLog.Number(present)} " +
                $"projectedCombined={CampaignLog.Number(projected)} camp={CampaignLog.Number(job.CampPower)} " +
                $"response={(job.Sufficient(projected) ? "combined-benchmark" : "send-available")} " +
                $"routeETA=+{CampaignLog.Number(KingdomCommander.ReliefHours(army.LeaderParty, job))}h " +
                $"armyTravelLimit={KingdomCommander.MaxReliefHours}h.");
        }
    }
}
