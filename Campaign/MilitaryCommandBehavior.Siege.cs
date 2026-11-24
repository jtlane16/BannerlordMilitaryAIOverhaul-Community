using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
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
using TaleWorlds.Core;
using TaleWorlds.Library;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        private const double RepulseHours = 7 * 24;
        private sealed class SiegeState
        {
            public string Owner = "", Kingdom = "";
            public string? End;
            public bool Issued, FreshDeparture, Arrived, Assault, AssaultSelected, Captured, OwnerEpochEnded;
            public double DepartureHour, CampHour = -1, UpkeepAfter, ReissueAfter, MusterProgressAt, MusterBest = -1, NextMarchCheck;
            public float MusterPeak, CommittedPower;
            public int Reissues;
            public bool ReissueOrderConfirmed;
            public double ReissueProgressAt = -1, ReissueBestDistance = -1;
            public float ReissueBestX, ReissueBestY;

            public string? AttemptId;
            public bool PaidRecovery, PaidFinalized;
            public float PaidSummons;
            public HashSet<string>? PaidEscorts;
            public HashSet<string>? PreviousTargets;
            public double? ConfirmStrengthAt;
            public float RegroupPresentFloor;
            public double MarchProgressAt = -1, MarchBestRoute = -1;
            public float MarchBestX, MarchBestY;
            public double RecoveryDeadline;
            public PreCampFailure? ObservedStrengthFailure;
        }

        private sealed class PreCampFailure
        {
            public string Owner = "";
            public float CommittedPower, FailedPower, Defenders;
            public int Wall;
            public float Food;
        }

        private sealed class SiegeAssignment
        {
            internal readonly Settlement Target;
            internal readonly Settlement? Muster;
            internal readonly SiegeState State;
            internal bool CommittingSummons;
            internal string? DispersalDuringSummons;
            internal SiegeAssignment(Settlement target, Settlement? muster, SiegeState state) =>
                (Target, Muster, State) = (target, muster, state);
        }

        private readonly Dictionary<Army, SiegeAssignment> _sieges = new();
        private Dictionary<string, double> _siegeRetry = new(StringComparer.Ordinal);
        private Dictionary<string, float[]> _repulseSites = new(StringComparer.Ordinal);
        private readonly Dictionary<SiegeEvent, (string[] kingdoms, CampaignVec2 position)> _reliefVictories = new();
        private readonly Dictionary<Kingdom, double> _nextSiegeSearch = new();
        private Dictionary<string, string> _failedSiegeMusters = new(StringComparer.Ordinal);
        private Dictionary<string, PreCampFailure> _preCampFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<(Army army, Settlement target), string> _siegeClosures = new();
        private readonly HashSet<MobileParty> _releasedOffenseThisTick = new();
        private double _offenseReleaseHour = double.NaN;
        private double _nextSiegeReview, _nextSiegeSearchSlot;
        private bool _siegeReview = true;
        private string? _siegeSearchSummary;
        private IEnumerator<int>? _offensiveSearch;
        private Action<double>? _offensiveCommit;
        private bool _offensiveDue;
        private int _offensiveSlices, _offensiveWork;
        private const int SiegeSliceQueries = 8;
        private static readonly long SiegeSliceBudget = Math.Max(1, Stopwatch.Frequency / 500); // 2 ms, soft (native calls cannot yield)

        // Immutable evidence of one real production assignment for the
        // opt-in observer; no observer can drive the assignment.
        internal IEnumerable<(Army Army, Settlement Target, double DepartureHour, bool Fresh, bool Camp, bool Assault)> SiegeObservations =>
            _sieges.Where(pair => pair.Value.State.Issued).Select(pair =>
                (pair.Key, pair.Value.Target, pair.Value.State.DepartureHour, pair.Value.State.FreshDeparture,
                    pair.Value.State.CampHour >= 0 && InCamp(pair.Key, pair.Value.Target) != null, pair.Value.State.Assault));
        internal string SiegeSearchSummary => _siegeSearchSummary ?? "no production search yet";
        internal string? SiegeClosure(Army? army, Settlement? target) =>
            army != null && target != null && _siegeClosures.TryGetValue((army, target), out var reason) ? reason : null;

        private void RegisterSiegeEvents()
        {
            CampaignEvents.QuarterHourlyTickEvent.AddNonSerializedListener(this, () => _siegeReview = true);
            CampaignEvents.AfterSettlementEntered.AddNonSerializedListener(this, (party, _, _) => RequestSiegeReview(party));
            CampaignEvents.PartyAttachedAnotherParty.AddNonSerializedListener(this, RequestSiegeReview);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, siege =>
            {
                _siegeReview = true;
                if (siege?.BesiegerCamp?.LeaderParty?.Army is Army army && _sieges.TryGetValue(army, out var job) &&
                    job.Target == siege.BesiegedSettlement && job.State.Issued && Registered(army) &&
                    InCamp(army, job.Target) == siege.BesiegerCamp)
                    ObserveNativeCamp(army, job, siege.BesiegerCamp);
            });
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, siege =>
            {
                if (siege != null && _reliefVictories.TryGetValue(siege, out var defeat))
                {
                    foreach (var kingdom in defeat.kingdoms)
                        RecordRepulse(kingdom, siege.BesiegedSettlement, "native-relief-battle-defeat", CampaignTime.Now.ToHours, defeat.position);
                    _reliefVictories.Remove(siege);
                }
                _siegeReview = true;
            });
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, ObserveSiegeBattleEnded);
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, (battle, attacker, _) =>
            {
                if (battle?.Component != null && battle.IsSiegeAssault && attacker?.MobileParty?.Army is Army army &&
                    _sieges.TryGetValue(army, out var job) && job.State.Issued && job.Target == battle.MapEventSettlement &&
                    InCamp(army, job.Target) is { } camp)
                {
                    ObserveNativeCamp(army, job, camp);
                    ObserveNativeAssault(army, job);
                }
                _siegeReview |= _sieges.Count != 0;
            });
            CampaignEvents.MakePeace.AddNonSerializedListener(this, (_, _, _) => _siegeReview = true);
            CampaignEvents.OnAllianceStartedEvent.AddNonSerializedListener(this, (_, _) => _siegeReview = true);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, (target, _, _, _, _, detail) =>
            {
                ClearPaidSiegeOnOwnerChange(target);
                string suffix = "/" + target.StringId;
                foreach (var key in _preCampFailures.Keys.Where(key => key.EndsWith(suffix, StringComparison.Ordinal) &&
                    _preCampFailures[key].Owner != target.MapFaction?.StringId).ToArray())
                    _preCampFailures.Remove(key);
                foreach (var pair in _sieges.Where(pair => pair.Value.Target == target))
                {
                    pair.Value.State.OwnerEpochEnded |= pair.Value.State.Owner != target.MapFaction?.StringId;
                    pair.Value.State.Captured |= detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege &&
                        target.MapFaction?.StringId == pair.Value.State.Kingdom && pair.Value.State.Issued &&
                        (pair.Value.State.CampHour >= 0 || InCamp(pair.Key, target) != null);
                }
                _siegeReview = true;
            });
            CampaignEvents.ArmyDispersed.AddNonSerializedListener(this, (army, reason, _) =>
            {
                if (_sieges.TryGetValue(army, out var assignment) && ArmyOrders.Releasing != army)
                {
                    string end = assignment.State.End ?? "native-dispersal:" + reason;
                    if (assignment.CommittingSummons)
                        assignment.DispersalDuringSummons = end;
                    else
                        FinishPaidSiege(army, assignment, CampaignTime.Now.ToHours, end);
                    _log.Event($"Siege native dispersal: {SiegeId(army, assignment)} reason={reason} cohesion={CampaignLog.Number(army.Cohesion)}.");
                }
                _siegeReview |= _sieges.ContainsKey(army);
            });
        }

        private void RequestSiegeReview(MobileParty? party) =>
            _siegeReview |= party?.Army is Army army && _sieges.ContainsKey(army);

        private void ObserveSiegeBattleEnded(MapEvent battle)
        {
            _siegeReview = true;
            // In the installed SiegeOutside component the relievers are the
            // *attackers*. MapEventEnded precedes native camp finalization.
            if (_sieges.Count == 0 || battle?.Component == null || !battle.IsSiegeOutside || battle.BattleState != BattleState.AttackerVictory ||
                battle.MapEventSettlement?.SiegeEvent is not { } siege)
                return;
            var losers = new HashSet<PartyBase>(battle.DefenderSide.Parties.Select(row => row.Party));
            var kingdoms = _sieges.Where(pair => pair.Value.State.Issued && pair.Value.Target == siege.BesiegedSettlement &&
                losers.Contains(pair.Key.LeaderParty.Party) &&
                (InCamp(pair.Key, pair.Value.Target) == siege.BesiegerCamp ||
                 pair.Value.State.CampHour == siege.SiegeStartTime.ToHours))
                .Select(pair => pair.Value.State.Kingdom).Distinct().ToArray();
            if (kingdoms.Length > 0)
                _reliefVictories[siege] = (kingdoms, siege.BesiegerCamp.LeaderParty?.Position ?? siege.BesiegedSettlement.GatePosition);
        }

        private void SyncSieges(IDataStore store)
        {
            if (store.IsSaving)
                foreach (var pair in _sieges)
                    if (pair.Value.State.PaidSummons > 0f && !pair.Value.State.PaidFinalized && !Registered(pair.Key))
                        FinishPaidSiege(pair.Key, pair.Value, CampaignTime.Now.ToHours,
                            pair.Value.State.End ?? "formation-missing-at-save");
            if (store.IsSaving && _sieges.Count == 0 && _repulseSites.Count == 0 && _siegeRetry.Count == 0 &&
                _failedSiegeMusters.Count == 0 && _preCampFailures.Count == 0 && _paidSiegeFailures.Count == 0 &&
                _crossTargetRebuys.Count == 0)
                return;
            var entries = store.IsSaving ? _sieges.Where(pair => Registered(pair.Key)).ToArray() :
                Array.Empty<KeyValuePair<Army, SiegeAssignment>>();
            var armies = entries.Select(pair => pair.Key).ToList();
            var targets = entries.Select(pair => pair.Value.Target).ToList();
            var musters = entries.Select(pair => pair.Value.Muster ?? pair.Value.Target).ToList();
            string? states = store.IsSaving ? JsonConvert.SerializeObject(entries.Select(pair => pair.Value.State)) : null;
            string? retries = store.IsSaving ? JsonConvert.SerializeObject(_siegeRetry) : null;
            string? sites = store.IsSaving ? JsonConvert.SerializeObject(_repulseSites) : null;
            string? failures = store.IsSaving ? JsonConvert.SerializeObject(_failedSiegeMusters) : null;
            string? preCampFailures = store.IsSaving ? JsonConvert.SerializeObject(_preCampFailures) : null;
            store.SyncData("siegeArmies", ref armies);
            store.SyncData("siegeTargets", ref targets);
            store.SyncData("siegeMusters", ref musters);
            store.SyncData("siegeStates", ref states);
            store.SyncData("siegeRetryExpiry", ref retries);
            store.SyncData("siegeRetrySites", ref sites);
            // The older key mixed strength losses with operational failures.
            // Do not carry over strength vetoes that lack the new baseline.
            store.SyncData("siegeOperationalMusterFailures", ref failures);
            store.SyncData("siegePreCampFailures", ref preCampFailures);
            SyncPaidSiegeFailures(store);
            SyncCrossTargetRebuys(store);
            if (!store.IsLoading)
                return;
            _sieges.Clear();
            try
            {
                var records = states == null ? null : JsonConvert.DeserializeObject<SiegeState[]>(states);
                _siegeRetry = retries == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, double>>(retries) ?? new(StringComparer.Ordinal);
                _repulseSites = sites == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, float[]>>(sites) ?? new(StringComparer.Ordinal);
                _failedSiegeMusters = failures == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, string>>(failures) ?? new(StringComparer.Ordinal);
                _preCampFailures = preCampFailures == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, PreCampFailure>>(preCampFailures) ?? new(StringComparer.Ordinal);
                if (records != null && armies?.Count == records.Length && targets?.Count == records.Length && musters?.Count == records.Length)
                    for (int i = 0; i < records.Length; ++i)
                        if (armies[i] != null && targets[i] != null && records[i] != null && !_relief.ContainsKey(armies[i]))
                            _sieges[armies[i]] = new SiegeAssignment(targets[i], musters[i] == targets[i] ? null : musters[i], records[i]);
                if (_sieges.Count + _siegeRetry.Count + _preCampFailures.Count + _paidSiegeFailures.Count != 0)
                    _log.Event($"Siege load: formations={_sieges.Count} retryDates={_siegeRetry.Count} preCampFailures={_preCampFailures.Count} " +
                        $"paidEpisodes={_paidSiegeFailures.Count}; no summons or payments replayed.");
            }
            catch (JsonException error)
            {
                _log.Event($"Siege save metadata could not load: {error.Message}.");
            }
        }

        private static string SiegeKey(string kingdom, Settlement target) => $"{kingdom}/{target.StringId}";
        private static string SiegeId(Army army, SiegeAssignment job) =>
            $"kingdom={job.State.Kingdom} target={job.Target.StringId} army={CampaignLog.ArmyId(army)}";
        private static BesiegerCamp? InCamp(Army army, Settlement target) =>
            army.LeaderParty.BesiegerCamp is { } camp && target.SiegeEvent == camp.SiegeEvent &&
            camp.HasInvolvedPartyForEventType(army.LeaderParty.Party) ? camp : null;

        private bool OnCooldown(Kingdom kingdom, Settlement target, double now) =>
            _siegeRetry.TryGetValue(SiegeKey(kingdom.StringId, target), out double expiry) && now < expiry;

        private bool FixedFollowOnCooldown(Kingdom kingdom, Settlement target, double now) => OnCooldown(kingdom, target, now) ||
            (_fixtureOnly && NativeCampaign.Current.GetCampaignBehaviors<MilitaryCommandBehavior>()
                .Any(command => !command._fixtureOnly && command.OnCooldown(kingdom, target, now)));

        private CampaignVec2? PriorCamp(Kingdom kingdom, Settlement target) =>
            _repulseSites.TryGetValue(SiegeKey(kingdom.StringId, target), out var site) && site?.Length == 2 ?
                new CampaignVec2(new Vec2(site[0], site[1]), true) : null;

        private bool KnownSiegeThreat(MobileParty leader, Settlement target, float present) =>
            KingdomCommander.KnownSiegeThreat(leader, target, present, leader.MapFaction is Kingdom kingdom ? PriorCamp(kingdom, target) : null);

        private float SiegeFieldThreat(MobileParty leader, Settlement target) =>
            KingdomCommander.KnownSiegeThreatPower(leader, target, PriorCamp(leader.Army.Kingdom, target), includeObserver: true);

        private static float StrongerRetryPower(float power) => power + Math.Max(25f, power * 0.10f);

        private static bool WeakerSiegeDefenses(PreCampFailure failure, float defenders, int wall, float food) =>
            // Observable improvement, not a substitute for the live native
            // score. A different scorer, route or routine movement isn't one.
            defenders <= failure.Defenders - Math.Max(25f, failure.Defenders * 0.10f) ||
                (defenders <= failure.Defenders && (wall < failure.Wall ||
                    (wall > 0 && food <= failure.Food - 25f)));

        private void RememberPreCampFailure(Army army, SiegeAssignment job, float failedPower,
            (float garrison, float militia, float mobile) defenders)
        {
            if (job.State.CampHour >= 0 || job.State.OwnerEpochEnded || job.Target.SiegeEvent != null || !(failedPower > 0f) ||
                job.Target.MapFaction?.StringId != job.State.Owner)
                return;
            float power = Math.Max(job.State.CommittedPower, failedPower);
            string key = SiegeKey(job.State.Kingdom, job.Target);
            float defensePower = defenders.garrison + defenders.militia + defenders.mobile;
            int wall = job.Target.Town.GetWallLevel();
            float food = Math.Min(100f, job.Target.Town.FoodStocks);
            var failure = new PreCampFailure
            {
                Owner = job.State.Owner, CommittedPower = power, FailedPower = failedPower,
                Defenders = defensePower, Wall = wall, Food = food
            };
            job.State.ObservedStrengthFailure = failure;
            // A weaker existing army can try without paying, but its failure
            // cannot make a previously rejected paid roster eligible again.
            if (_preCampFailures.TryGetValue(key, out var prior) && prior.Owner == job.State.Owner &&
                power <= prior.CommittedPower && !WeakerSiegeDefenses(prior, defensePower, wall, food))
                return;
            _preCampFailures[key] = failure;
            _log.Event($"Siege fresh retry guarded: {SiegeId(army, job)} failedPower={CampaignLog.Number(failedPower)} " +
                $"priorCommitment={CampaignLog.Number(power)} retryPower={CampaignLog.Number(StrongerRetryPower(power))}; " +
                "materially weaker defenses or a stronger roster required before new summons.");
        }

        private void RecordRepulse(string kingdom, Settlement target, string reason, double now, CampaignVec2 position)
        {
            var key = SiegeKey(kingdom, target);
            if (_siegeRetry.TryGetValue(key, out double existing) && now < existing)
                return;
            double expiry = now + RepulseHours;
            _siegeRetry[key] = expiry;
            if (position.IsOnLand)
                _repulseSites[key] = new[] { position.X, position.Y };
            _log.Event($"Siege repulsed: kingdom={kingdom} target={target.StringId} cause={reason} siegeBroken=True " +
                $"brokenAt={now:R} retryAt={expiry:R} (seven full days).");
        }

        private string? SiegeTargetBlocker(Kingdom kingdom, Settlement target, double now, Army? self = null) =>
            SiegeTargetBlocker(kingdom, target, now, self, out _, out _);

        private string? SiegeTargetBlocker(Kingdom kingdom, Settlement target, double now, Army? self,
            out Settlement? foothold, out int links) =>
            SiegeAssignmentBlocker(kingdom, target, now, self, out foothold, out links) ??
                (SiegeWarStance.BlocksNewTarget(kingdom, target.MapFaction) ? "defensive-war-stance" : null);

        private string? SiegeAssignmentBlocker(Kingdom kingdom, Settlement target, double now, Army? self,
            out Settlement? foothold, out int links)
        {
            foothold = null;
            links = 0;
            if (KingdomCommander.HasHomeSiege(kingdom))
                return "home-siege-defense-only";
            if (!MilitaryObjective.EnemyFortification(target, kingdom))
                return "not-an-enemy-fortification-at-war";
            if (OnCooldown(kingdom, target, now))
                return "repulse-cooldown";
            if (target.IsUnderSiege || target.Party.MapEvent != null)
                return "already-engaged";
            foothold = KingdomCommander.SiegeFoothold(kingdom, target, out links);
            if (foothold == null)
                return KingdomCommander.OutsideSiegeFrontier;
            return _sieges.Any(pair => pair.Key != self && pair.Value.State.End == null && pair.Value.Target == target) ?
                "commander-target-committed" : null;
        }

        private static bool OwnsSiegeMovement(Army army, SiegeAssignment job)
        {
            var leader = army.LeaderParty;
            if (HasAssignedSiegeOrder(army, job))
                return true;
            return ClearedReliefOrder(leader) && (army.AiBehaviorObject == null ||
                army.AiBehaviorObject == (job.State.Issued ? job.Target : job.Muster));
        }

        private static bool HasAssignedSiegeOrder(Army army, SiegeAssignment job) =>
            job.State.Issued ? ArmyOrders.HasSiegeOrder(army, job.Target) :
                army.AiBehaviorObject == job.Muster && army.LeaderParty.TargetSettlement == job.Muster &&
                army.LeaderParty.DefaultBehavior == AiBehavior.GoToSettlement;

        internal bool TrySiegeTraceSubject(MobileParty party, out SiegeOrderTrace.Subject subject)
        {
            if (!_fixtureOnly && !party.IsMainParty && party.Army is Army army && army.LeaderParty == party &&
                _sieges.TryGetValue(army, out var job) && job.State.End == null && job.State.CampHour < 0 &&
                party.BesiegerCamp == null && !army.Parties.Contains(MobileParty.MainParty))
            {
                subject = TraceSubject(army, job);
                return true;
            }
            subject = default;
            return false;
        }

        private static SiegeOrderTrace.Subject TraceSubject(Army army, SiegeAssignment job) =>
            new(army, job.Target, job.State.Issued ? job.Target : job.Muster ?? job.Target,
                job.State.AttemptId ?? "-", job.State.Issued, job.State.Reissues);

        private void TraceSiegeReview(Army army, SiegeAssignment job, string decision, bool? route = null,
            bool? musterPath = null, string? visitLock = null, bool memberLock = false)
        {
            if (SiegeOrderTrace.Watches(this))
                SiegeOrderTrace.Review(this, TraceSubject(army, job), decision, route, musterPath, visitLock, memberLock);
        }

        private static string? SiegeDiversionBlocker(Army army, SiegeAssignment job)
        {
            if (!OwnsSiegeMovement(army, job))
                return "external-siege-order";
            return ArmyOrders.SiegeWithdrawalBlocker(army, job.Target);
        }

        private bool LeaveSiege(Army army, SiegeAssignment job, string reason, double now)
        {
            var camp = InCamp(army, job.Target);
            var siege = camp?.SiegeEvent;
            var position = camp?.LeaderParty?.Position ?? army.LeaderParty.Position;
            string? blocked = SiegeDiversionBlocker(army, job) ?? ArmyOrders.WithdrawCancelledSiege(army, job.Target, _log);
            if (blocked != null)
            {
                if (_log.Changed("siege-withdraw-lock", CampaignLog.ArmyId(army), blocked))
                    _log.Event($"Siege withdrawal deferred: {SiegeId(army, job)} reason={reason} lock={blocked}.");
                return false;
            }
            _log.Changed("siege-withdraw-lock", CampaignLog.ArmyId(army), null);
            if (reason == "visible-local-relief" && siege != null && job.Target.SiegeEvent != siege)
                RecordRepulse(job.State.Kingdom, job.Target, reason, now, position);
            _log.Event($"Siege closes: {SiegeId(army, job)} reason={reason} campRemoved={siege != null} " +
                $"siegeBroken={siege != null && job.Target.SiegeEvent != siege}; currentOwner={job.Target.MapFaction?.StringId ?? "-"}.");
            if (_siegeClosures.Count >= 32)
                _siegeClosures.Remove(_siegeClosures.Keys.First());
            _siegeClosures[(army, job.Target)] = reason;
            return true;
        }

        private void TickSieges(double now)
        {
            if (_siegeReview || now >= _nextSiegeReview)
            {
                using var review = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.SiegeReview);
                _siegeReview = false;
                _nextSiegeReview = now + 0.25d;
                foreach (var pair in _sieges.ToArray())
                    ReviewSiege(pair.Key, pair.Value, now);
                foreach (var key in _siegeRetry.Keys.Where(key => now >= _siegeRetry[key]).ToArray())
                {
                    _log.Event($"Siege retry eligible for fresh evaluation: {key} expiry={_siegeRetry[key]:R}; no automatic order.");
                    _siegeRetry.Remove(key);
                }
                foreach (var key in _paidSiegeFailures.Where(pair => now >= pair.Value.Until).Select(pair => pair.Key).ToArray())
                    _paidSiegeFailures.Remove(key);
                _crossTargetRebuys.RemoveAll(record => now >= record.Until);
                ReviewIdle(now);
            }
            _offensiveDue = now >= _nextSiegeSearchSlot;
        }

        // Called once per real application callback, after native campaign
        // updates and relief. Never drive the sweep from CampaignEvents.Tick.
        internal void OffensiveFrame(double now)
        {
            if (!_offensiveDue && _offensiveSearch == null && _offensiveCommit == null)
                return;
            using var planning = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.SiegePlan);
            if (_offensiveCommit is { } commit)
            {
                // A native event may request defense after the last campaign
                // TickEvent in this application callback. Let it run first.
                if (_reliefReview || _reliefPlan)
                    return;
                _offensiveCommit = null;
                using var committing = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.SiegeCommit);
                commit(now);
                return;
            }
            if (_offensiveSearch == null)
            {
                if (now < _nextSiegeSearchSlot)
                    return;
                // Oldest due first: a long sweep must not let the first
                // kingdom in native order jump ahead of still-waiting kingdoms.
                var kingdom = Kingdom.All.Where(item => !item.IsEliminated)
                    .Select(item => (item, due: _nextSiegeSearch.TryGetValue(item, out double next) ? next : double.NegativeInfinity))
                    .Where(row => now >= row.due).OrderBy(row => row.due).Select(row => row.item).FirstOrDefault();
                _offensiveDue = false;
                _nextSiegeSearchSlot = now + 0.25d;
                if (kingdom == null)
                    return;
                _nextSiegeSearch[kingdom] = now + 8d;
                _offensiveWork = _offensiveSlices = 0;
                _offensiveSearch = AllocateSiege(kingdom, now).GetEnumerator();
            }
            long start = Stopwatch.GetTimestamp();
            int queries = 0, steps = 0;
            _offensiveSlices++;
            do
            {
                bool found;
                try
                {
                    found = _offensiveSearch.MoveNext();
                }
                catch (NullReferenceException error)
                {
                    // Only the read-only scan may be discarded. Never suppress
                    // an error while committing an order or spending influence.
                    _offensiveSearch.Dispose();
                    _offensiveSearch = null;
                    _offensiveCommit = null;
                    var frame = new StackTrace(error, true).GetFrame(0);
                    _siegeSearchSummary = "offensive sweep aborted on missing campaign data; no order or debit";
                    _log.Event($"Siege search: {_siegeSearchSummary}; slices={_offensiveSlices} work={_offensiveWork} " +
                        $"ilOffset={frame?.GetILOffset() ?? -1} exception={error.ToString().Replace(Environment.NewLine, " | ")}");
                    return;
                }
                if (!found)
                {
                    _offensiveSearch.Dispose();
                    _offensiveSearch = null;
                    break;
                }
                int work = _offensiveSearch.Current;
                queries += work;
                _offensiveWork += work;
            } while (++steps < 64 && queries < SiegeSliceQueries && Stopwatch.GetTimestamp() - start < SiegeSliceBudget);
        }

        private void ObserveNativeCamp(Army army, SiegeAssignment job, BesiegerCamp camp)
        {
            if (job.State.CampHour >= 0)
                return;
            ObserveSiegeArrival(army, job, "native-camp-entry");
            job.State.CampHour = camp.SiegeEvent.SiegeStartTime.ToHours;
            _repulseSites.Remove(SiegeKey(job.State.Kingdom, job.Target));
            _preCampFailures.Remove(SiegeKey(job.State.Kingdom, job.Target));
            _paidSiegeFailures.Remove(SiegeKey(job.State.Kingdom, job.Target));
            _log.Event($"Siege native camp: {SiegeId(army, job)} phase={(camp.LeaderParty == army.LeaderParty && job.State.CampHour >= job.State.DepartureHour ? "started" : "joined")} " +
                $"campLeader={camp.LeaderParty?.StringId ?? "-"} position={Position(army.LeaderParty.Position)} attempt={job.State.AttemptId ?? "-"}.");
        }

        private void ObserveSiegeArrival(Army army, SiegeAssignment job, string evidence)
        {
            if (job.State.Arrived)
                return;
            job.State.Arrived = true;
            _log.Event($"Siege arrival: {SiegeId(army, job)} evidence={evidence} position={Position(army.LeaderParty.Position)}.");
        }

        private void ObserveNativeAssault(Army army, SiegeAssignment job)
        {
            if (job.State.Assault)
                return;
            job.State.Assault = true;
            _log.Event($"Siege native assault started: {SiegeId(army, job)}; no commander assault order.");
        }

        private void ReviewSiege(Army army, SiegeAssignment job, double now)
        {
            var state = job.State;
            if (!Registered(army) || army.Kingdom.StringId != state.Kingdom || army.LeaderParty.IsMainParty ||
                ArmyTakeoverBehavior.Current?.IsPending(army) == true)
            {
                FinishPaidSiege(army, job, now, state.End ?? "formation-gone-or-authority-changed");
                _log.Event($"Siege assignment reconciled: {SiegeId(army, job)} formation gone or authority changed; no release.");
                _sieges.Remove(army);
                return;
            }
            var leader = army.LeaderParty;
            var kingdom = army.Kingdom;
            var members = ArmyOrders.Members(army);
            if (members.Any(party => party.IsMainParty))
                return;
            var camp = InCamp(army, job.Target);
            if (camp != null && state.Issued)
            {
                ObserveNativeCamp(army, job, camp);
                if (!state.AssaultSelected && leader.ShortTermBehavior == AiBehavior.AssaultSettlement)
                {
                    state.AssaultSelected = true;
                    _log.Event($"Siege native assault selected: {SiegeId(army, job)}; no commander assault order.");
                }
                if (!state.Assault && leader.MapEvent is { IsSiegeAssault: true } assault && assault.MapEventSettlement == job.Target)
                    ObserveNativeAssault(army, job);
            }
            if (!OwnsSiegeMovement(army, job))
            {
                TraceSiegeReview(army, job, "external-siege-order");
                FinishPaidSiege(army, job, now, state.End ?? "external-siege-order");
                _log.Event($"Siege order authority lost: {SiegeId(army, job)} actual={leader.DefaultBehavior}; no override.");
                _sieges.Remove(army);
                return;
            }
            if (camp != null && ArmyOrders.SiegeWithdrawalBlocker(army, job.Target) is string campLock)
            {
                if (_log.Changed("siege-camp-lock", CampaignLog.ArmyId(army), campLock))
                    _log.Event($"Siege native camp protected: {SiegeId(army, job)} lock={campLock}; no withdrawal or diversion.");
                return; // no extraction (or strength decisions based on underway combat)
            }
            _log.Changed("siege-camp-lock", CampaignLog.ArmyId(army), null);
            if (camp == null && !ArmyOrders.CanRelease(army))
            {
                state.MarchProgressAt = -1; // time in native combat is not inactivity
                TraceSiegeReview(army, job, "release-locked", memberLock: true);
                return;
            }

            if (camp == null)
                foreach (var party in members.Where(party => party != leader && !ArmyOrders.CanDirect(party, kingdom)))
                    ArmyOrders.ReleaseMember(army, party, _log, "siege-member-ineligible");
            float present = KingdomCommander.SiegePresent(army, kingdom);
            var presentParties = members.Where(party => party == leader || party.AttachedTo == leader).ToArray();
            if (state.End == null)
            {
                string? reason = state.Captured ? "native-capture" :
                    state.OwnerEpochEnded || job.Target.MapFaction?.StringId != state.Owner ? "settlement-owner-changed" :
                    !MilitaryObjective.EnemyFortification(job.Target, kingdom) ? "no-longer-at-war-or-eligible" :
                    KingdomCommander.HasHomeSiege(kingdom) ? "home-siege-defense-only" :
                    OnCooldown(kingdom, job.Target, now) ? "repulse-cooldown" :
                    !ArmyOrders.CanDirect(leader, kingdom) || present <= 0 ? "no-eligible-command-or-strength" :
                    presentParties.Count(party => party.Party.IsStarving) * 2 > presentParties.Length ? "actual-starvation" :
                    camp == null && state.CampHour < 0 && KingdomCommander.SiegeFoothold(kingdom, job.Target, out _) == null ?
                        KingdomCommander.OutsideSiegeFrontier : null;
                if (reason != null)
                    state.End = reason;
                else if (camp != null && camp.LeaderParty is { } observer)
                {
                    float radius = KingdomCommander.SiegeRadius;
                    float power = KingdomCommander.CampFieldPower(camp, radius);
                    float sight = observer.SeeingRange;
                    var contacts = KingdomCommander.VisibleThreat(observer, observer.Position, radius, sight);
                    float hostile = contacts.Sum(row => row.power);
                    if (KingdomCommander.Overwhelmed(hostile, power))
                    {
                        _log.Event($"Siege withdrawal decision: {SiegeId(army, job)} radius={CampaignLog.Number(radius)} " +
                            $"observer={observer.StringId} sight={CampaignLog.Number(sight)} campPower={CampaignLog.Number(power)} " +
                            $"hostile={CampaignLog.Number(hostile)} ratio={CampaignLog.Number(hostile / power)} contacts=[" +
                            string.Join(", ", contacts.Select(row => $"{row.party.StringId}:d={CampaignLog.Number(row.distance)}:power={CampaignLog.Number(row.power)}")) +
                            "]; strictly over 120%.");
                        if (LeaveSiege(army, job, "visible-local-relief", now))
                        {
                            state.End = "visible-local-relief";
                            ReleaseSiege(army, job, now);
                        }
                        return;
                    }
                    if (_log.Changed("siege-field-balance", CampaignLog.ArmyId(army),
                        $"{job.Target.StringId}:{(int)(power / 100)}:{hostile > 0}:{(int)(hostile / 100)}"))
                        _log.Event($"Siege continues: {SiegeId(army, job)} campPower={CampaignLog.Number(power)} " +
                            $"visibleHostile={CampaignLog.Number(hostile)} radius={CampaignLog.Number(radius)}; " +
                            "sustainable native camp; new-siege check does not apply.");
                    SustainSiege(army, job, now);
                    return; // sustainable camp: never rescore it as a new siege
                }
                else if (state.CampHour >= 0)
                    state.End = "native-camp-ended-without-capture";
                else if (state.Issued)
                    ReviewSiegeMarch(army, job, present, now);
                else
                    ReviewSiegeMuster(army, job, members, present, now);
            }
            if (state.End != null && LeaveSiege(army, job, state.End, now))
            {
                TraceSiegeReview(army, job, state.End);
                FinishPaidSiege(army, job, now, state.End);
                ReleaseSiege(army, job, now);
            }
        }

        private void ReviewSiegeMarch(Army army, SiegeAssignment job, float present, double now)
        {
            var target = job.Target;
            var leader = army.LeaderParty;
            var state = job.State;
            bool? route = null;
            double routeDistance = double.PositiveInfinity;
            string decision = "march-review";
            var otherCamp = target.SiegeEvent?.BesiegerCamp;
            float arrivalRadius = NativeCampaign.Current.Models.EncounterModel.GetEncounterJoiningRadius;
            bool orderPresent = HasAssignedSiegeOrder(army, job);
            if (now > state.DepartureHour && orderPresent && leader.Position.IsOnLand &&
                leader.Position.DistanceSquared(target.GatePosition.ToVec2()) <= arrivalRadius * arrivalRadius)
                ObserveSiegeArrival(army, job, "within-native-encounter-radius; camp-not-yet-confirmed");
            bool dueReissue = !orderPresent && now >= state.ReissueAfter;
            string? visitLock = dueReissue || HasSiegeRecovery(state) ? ArmyOrders.VisitLock(leader) : null;
            bool canReissue = dueReissue && visitLock == null;
            bool hourly = now >= state.NextMarchCheck;
            float fieldThreat = SiegeFieldThreat(leader, target);
            if (otherCamp != null && (otherCamp.MapFaction != army.Kingdom ||
                otherCamp.LeaderParty?.Army?.LeaderParty.IsMainParty == true || otherCamp.LeaderParty?.IsMainParty == true))
                state.End = "target-engaged-by-other-force";
            else if (KingdomCommander.Overwhelmed(fieldThreat, present))
            {
                if (TryRegroupForFieldThreat(army, job, present, fieldThreat, now))
                    return;
                state.End = "visible-overwhelming-threat-before-camp";
            }
            else if (hourly || canReissue)
            {
                if (hourly)
                    state.NextMarchCheck = now + 1d;
                float score = 0f;
                bool retain = false;
                float roster = KingdomCommander.SiegeRoster(army);
                bool strengthLost = otherCamp == null && target.Party.MapEvent == null &&
                    !MeetsSiegeContinuation(leader, job, roster, out score, out retain);
                if (strengthLost)
                {
                    if (!ConfirmedStrengthLoss(army, job, now, present, roster, score))
                        return;
                    state.End = "march-no-longer-meets-native-strength";
                    LogSiegeFeasibilityLoss(army, job, present, roster, score, "native-strength");
                }
                else if (!(route = KingdomCommander.LiveSiegeRoute(leader, KingdomCommander.Origin(leader), target, out routeDistance)).Value)
                {
                    state.End = hourly ? "march-land-path-lost" : "cleared-siege-order-not-recoverable";
                    decision = hourly ? state.End : "reissue-route-failed";
                }
                else
                {
                    if (orderPresent && ArmyOrders.VisitLock(leader) == null && !HasSiegeRecovery(state))
                    {
                        var point = leader.Position;
                        float dx = point.X - state.MarchBestX, dy = point.Y - state.MarchBestY;
                        if (state.MarchProgressAt < 0 || (state.MarchBestRoute - routeDistance >= SiegeReissueProgressDistance &&
                            dx * dx + dy * dy >= SiegeReissuePhysicalStep * SiegeReissuePhysicalStep))
                        {
                            state.MarchProgressAt = now;
                            state.MarchBestRoute = routeDistance;
                            state.MarchBestX = point.X;
                            state.MarchBestY = point.Y;
                        }
                        else if (now - state.MarchProgressAt >= SiegeReissueStallHours)
                            state.End = "march-no-observed-progress";
                    }
                    else
                        state.MarchProgressAt = -1;
                    if (_log.Changed("siege-march-strength-retention", CampaignLog.ArmyId(army), retain ? target.StringId : null) && retain)
                        LogSiegeStrengthRetention(army, job, "march", roster);
                }
                if (!strengthLost)
                    state.ConfirmStrengthAt = null;
            }
            if (state.End == null && visitLock == null && HasSiegeRecovery(state))
            {
                if (route == true)
                    ObserveSiegeRecoveryProgress(army, job, now, routeDistance, orderPresent);
                if (SiegeRecoveryStalled(state, now))
                {
                    state.End = "cleared-siege-order-not-recoverable";
                    decision = "reissue-no-progress";
                    LogStalledSiegeRecovery(army, job);
                }
            }
            if (state.End == null && dueReissue)
            {
                if (visitLock != null)
                    decision = "visit-locked";
                else if (state.Reissues >= SiegeReissueLimit)
                {
                    state.End = "cleared-siege-order-not-recoverable";
                    decision = "reissue-budget-exhausted";
                }
                else
                {
                    ReissueSiegeTravel(army, job, now, routeDistance);
                    decision = "reissue-attempted";
                }
            }
            TraceSiegeReview(army, job, decision == "march-review" ? state.End ?? decision : decision, route, visitLock: visitLock);
            if (state.End == null)
                SustainSiege(army, job, now);
        }

        private void ReviewSiegeMuster(Army army, SiegeAssignment job, MobileParty[] members, float present, double now)
        {
            var leader = army.LeaderParty;
            var site = job.Muster!;
            var state = job.State;
            string? blocker = SiegeAssignmentBlocker(army.Kingdom, job.Target, now, army, out _, out _);
            bool? route = null, musterPath = null;
            if (blocker != null || !KingdomCommander.OpenFortification(site, army.Kingdom) ||
                !(route = KingdomCommander.LiveSiegeRoute(leader, site.GatePosition, job.Target)).Value ||
                !(musterPath = ArmyOrders.CanPathVisit(leader, site)).Value)
            {
                state.End = blocker ?? "muster-or-march-route-lost";
                TraceSiegeReview(army, job, state.End, route, musterPath);
                return;
            }
            if (ArmyOrders.VisitLock(leader) is string visitLock)
            {
                TraceSiegeReview(army, job, "visit-locked", route, musterPath, visitLock);
                return;
            }
            if (state.RecoveryDeadline > 0d && now >= state.RecoveryDeadline)
            {
                state.End = "paid-retry-assembly-deadline";
                return;
            }
            double? musterDistance = null;
            if (leader.CurrentSettlement == site)
                foreach (var party in members.Where(party => ArmyOrders.CanDirect(party, army.Kingdom)))
                    ArmyOrders.MergeAtMuster(army, party, site);
            else
            {
                bool dueReissue = ClearedReliefOrder(leader) && now >= state.ReissueAfter;
                if (dueReissue || HasSiegeRecovery(state))
                    musterDistance = KingdomCommander.LandDistance(KingdomCommander.Origin(leader), site.GatePosition, unrestricted: true);
                if (HasSiegeRecovery(state))
                {
                    ObserveSiegeRecoveryProgress(army, job, now, musterDistance!.Value, HasAssignedSiegeOrder(army, job));
                    if (SiegeRecoveryStalled(state, now))
                    {
                        state.End = "cleared-muster-order-not-recoverable";
                        LogStalledSiegeRecovery(army, job);
                        TraceSiegeReview(army, job, "reissue-no-progress", route, musterPath);
                        return;
                    }
                }
                if (dueReissue)
                {
                    if (state.Reissues >= SiegeReissueLimit)
                    {
                        state.End = "cleared-muster-order-not-recoverable";
                        TraceSiegeReview(army, job, "reissue-budget-exhausted", route, musterPath);
                        return;
                    }
                    ReissueSiegeTravel(army, job, now, musterDistance!.Value);
                    TraceSiegeReview(army, job, "reissue-attempted", route, musterPath);
                }
                else
                    TraceSiegeReview(army, job, "muster-review", route, musterPath);
            }
            var travelers = KingdomCommander.ReliefArrivals(army, army.Kingdom, site, siegeEstimate: true).ToArray();
            present = KingdomCommander.SiegePresent(army, army.Kingdom);
            float projected = KingdomCommander.SiegeRoster(army);
            string shape = $"{leader.CurrentSettlement == site}:{travelers.Length}:{(int)(present / 25)}";
            if (_log.Changed("siege-muster-arrivals", CampaignLog.ArmyId(army), shape))
                _log.Event($"Siege muster progress: {SiegeId(army, job)} site={site.StringId} " +
                    $"leaderArrived={leader.CurrentSettlement == site} present={CampaignLog.Number(present)} " +
                    $"projected={CampaignLog.Number(projected)} traveling={travelers.Length} " +
                    "departurePolicy=live-roster.");
            if (leader.CurrentSettlement == site && CanIssueSiege(army, job, now, present, projected, out float departureScore, out float checkedPower))
            {
                IssueSiege(army, job, now, present, projected, departureScore, checkedPower);
                return;
            }
            double leaderHours = musterDistance.HasValue ? KingdomCommander.TravelHours(musterDistance.Value, leader.Speed) :
                KingdomCommander.ReliefMusterHours(leader, site);
            double remaining = Math.Max(leaderHours,
                travelers.Length == 0 ? 0 : travelers.Max(row => row.hours));
            if (state.MusterBest < 0 || remaining + 0.25d < state.MusterBest || present > state.MusterPeak + 1f)
            {
                state.MusterProgressAt = now;
                state.MusterBest = remaining;
                state.MusterPeak = present;
            }
            bool enough = MeetsSiegeContinuation(leader, job, projected, out float score, out bool retain);
            bool threat = KingdomCommander.Overwhelmed(SiegeFieldThreat(leader, job.Target), present);
            if (enough)
                state.ConfirmStrengthAt = null;
            // Stay at/return to the safe muster while reinforcements are actually
            // approaching. Strength on paper does not cover a local field threat.
            if (threat && enough && travelers.Length > 0 && now - state.MusterProgressAt < SiegeMusterStallHours)
                return;
            if (!enough || threat)
            {
                if (!threat && !ConfirmedStrengthLoss(army, job, now, present, projected, score))
                    return;
                state.End = "muster-projected-force-no-longer-viable";
                LogSiegeFeasibilityLoss(army, job, present, projected, score, threat ? "visible-local-threat" : "native-strength");
            }
            else if (now - state.MusterProgressAt >= SiegeMusterStallHours)
                state.End = "muster-no-observed-progress";
            else
            {
                if (_log.Changed("siege-muster-strength-retention", CampaignLog.ArmyId(army), retain ? job.Target.StringId : null) && retain)
                    LogSiegeStrengthRetention(army, job, "muster", projected);
                if (!retain)
                    SustainSiege(army, job, now);
            }
        }

        private bool ConfirmedStrengthLoss(Army army, SiegeAssignment job, double now, float present, float roster, float score)
        {
            if (job.State.ConfirmStrengthAt is double after)
                return now >= after;
            job.State.ConfirmStrengthAt = now + 0.25d;
            LogSiegeFeasibilityLoss(army, job, present, roster, score, "native-strength-awaiting-confirmation");
            return false;
        }

        private bool TryRegroupForFieldThreat(Army army, SiegeAssignment job, float present, float hostile, double now)
        {
            var leader = army.LeaderParty;
            if (job.Muster is not { } site || ArmyOrders.VisitLock(leader) != null ||
                !KingdomCommander.OpenFortification(site, army.Kingdom) || !ArmyOrders.CanPathVisit(leader, site) ||
                !KingdomCommander.ReliefArrivals(army, army.Kingdom, site, siegeEstimate: true).Any())
                return false;
            float roster = KingdomCommander.SiegeRoster(army);
            if (roster <= present || KingdomCommander.Overwhelmed(hostile, roster) ||
                !MeetsSiegeContinuation(leader, job, roster, out _, out _))
                return false;
            ArmyOrders.Visit(army, site);
            var state = job.State;
            state.Issued = false;
            state.RegroupPresentFloor = hostile / 1.2f;
            state.MarchProgressAt = -1;
            state.ConfirmStrengthAt = null;
            state.MusterProgressAt = now;
            state.MusterBest = state.ReissueProgressAt = state.ReissueBestDistance = -1d;
            state.MusterPeak = present;
            state.Reissues = 0;
            state.ReissueAfter = 0;
            state.ReissueOrderConfirmed = false;
            _log.Event($"Siege regroup: {SiegeId(army, job)} muster={site.StringId} roster={CampaignLog.Number(roster)} present={CampaignLog.Number(present)}; local threat, same formation.");
            return true;
        }

        private static bool WasPaidSiegeEscort(SiegeState state, MobileParty party) =>
            state.PaidSummons > 0f &&
            // Old saves can record a positive debit without individual payees.
            (state.PaidEscorts is not { Count: > 0 } paid || paid.Contains(party.StringId));

        private static bool MeetsSiegeContinuation(MobileParty leader, SiegeAssignment job, float power, out float score, out bool retained)
        {
            retained = false;
            if (KingdomCommander.SiegeStrength(leader, job.Target, power, out score))
                return true;
            if (!KingdomCommander.SiegeStrength(leader, job.Target, KingdomCommander.SiegeContinuationProbePower(power), out float probeScore))
                return false;
            score = probeScore;
            retained = true;
            return true;
        }

        private void LogSiegeStrengthRetention(Army army, SiegeAssignment job, string phase, float power) =>
            _log.Event($"Siege retained below new-siege strength: {SiegeId(army, job)} phase={phase} " +
                $"power={CampaignLog.Number(power)} continuationProbePower={CampaignLog.Number(KingdomCommander.SiegeContinuationProbePower(power))} " +
                "standard native strength check failed.");

        private void LogSiegeFeasibilityLoss(Army army, SiegeAssignment job, float present, float projected, float score, string reason)
        {
            var leader = army.LeaderParty;
            var defenders = SiegeDefenders(job.Target);
            _log.Event($"Siege feasibility lost: {SiegeId(army, job)} check={reason} " +
                $"present={CampaignLog.Number(present)} projected={CampaignLog.Number(projected)} nativeScore={CampaignLog.Number(score)} " +
                $"scorer={leader.StringId} order={leader.DefaultBehavior} position={Position(leader.Position)} " +
                $"site={leader.CurrentSettlement?.StringId ?? "-"} {ScorerState(leader, job.Target, defenders)}.");
            if (reason == "native-strength")
                RememberPreCampFailure(army, job, projected, defenders);
        }

        private static (float garrison, float militia, float mobile) SiegeDefenders(Settlement target)
        {
            float garrison = 0, militia = 0, mobile = 0;
            foreach (var party in target.Parties)
                if (party.IsGarrison) garrison += party.Party.EstimatedStrength;
                else if (party.IsMilitia) militia += party.Party.EstimatedStrength;
                else if (party.Aggressiveness > 0.01f) mobile += party.Party.EstimatedStrength;
            return (garrison, militia, mobile);
        }

        private static string ScorerState(MobileParty leader, Settlement target, (float garrison, float militia, float mobile) defenders)
        {
            var (garrison, militia, mobile) = defenders;
            return $"targetFood={CampaignLog.Number(target.Town.FoodStocks)} wall={target.Town.GetWallLevel()} " +
                $"scorerAggression={CampaignLog.Number(leader.Aggressiveness)} defenseFactionPower={CampaignLog.Number(target.MapFaction.CurrentTotalStrength)} " +
                $"rawGarrison/Militia/MobilePower={CampaignLog.Number(garrison)}/{CampaignLog.Number(militia)}/{CampaignLog.Number(mobile)} " +
                $"occupants={target.Parties.Count} scorerTarget={leader.TargetSettlement?.StringId ?? "-"}";
        }

        private bool CanIssueSiege(Army army, SiegeAssignment job, double now, float present, float roster, out float score, out float checkedPower)
        {
            score = 0;
            checkedPower = roster;
            var leader = army.LeaderParty;
            if (present < job.State.RegroupPresentFloor || !Registered(army) || !ArmyOrders.CanRelease(army) || ArmyOrders.VisitLock(leader) != null ||
                !MilitaryObjective.EnemyFortification(job.Target, army.Kingdom) || job.Target.MapFaction?.StringId != job.State.Owner ||
                SiegeAssignmentBlocker(army.Kingdom, job.Target, now, army, out _, out _) != null ||
                !KingdomCommander.LiveSiegeRoute(leader, KingdomCommander.Origin(leader), job.Target))
                return false;
            if (!MeetsSiegeContinuation(leader, job, roster, out score, out bool retained))
                return false;
            if (retained)
                checkedPower = KingdomCommander.SiegeContinuationProbePower(roster);
            return !KingdomCommander.Overwhelmed(SiegeFieldThreat(leader, job.Target), present);
        }

        private void IssueSiege(Army army, SiegeAssignment job, double now, float present, float roster, float score, float checkedPower)
        {
            bool fresh = job.Target.SiegeEvent == null;
            ArmyOrders.Besiege(army, job.Target);
            _siegeClosures.Remove((army, job.Target));
            job.State.Issued = ArmyOrders.HasSiegeOrder(army, job.Target);
            job.State.FreshDeparture = fresh && job.Target.SiegeEvent == null;
            job.State.DepartureHour = now;
            job.State.RegroupPresentFloor = 0;
            job.State.MarchProgressAt = -1;
            job.State.ConfirmStrengthAt = null;
            job.State.NextMarchCheck = now + 1d;
            job.State.Reissues = 0;
            job.State.ReissueAfter = 0;
            job.State.ReissueOrderConfirmed = false;
            job.State.ReissueProgressAt = job.State.ReissueBestDistance = -1d;
            if (!job.State.Issued)
                job.State.End = "native-besiege-order-not-issued";
            var travelingPaid = job.Muster != null && job.State.PaidSummons > 0f ?
                ArmyOrders.Members(army).Where(party => party != army.LeaderParty && party.AttachedTo != army.LeaderParty &&
                    ArmyOrders.CanDirect(party, army.Kingdom) && WasPaidSiegeEscort(job.State, party)).ToArray() :
                Array.Empty<MobileParty>();
            _log.Event($"Siege departure: {SiegeId(army, job)} order={army.LeaderParty.DefaultBehavior} nativeIssued={job.State.Issued} " +
                $"campAbsentAtDeparture={job.State.FreshDeparture} present={CampaignLog.Number(present)} " +
                $"position={Position(army.LeaderParty.Position)} nativeScoreAtDeparture={CampaignLog.Number(score)} " +
                $"roster={CampaignLog.Number(roster)} checkedPower={CampaignLog.Number(checkedPower)} continuationOverride={checkedPower > roster} " +
                $"attempt={job.State.AttemptId ?? "-"} retainedPaidTravelers=[{string.Join(", ", travelingPaid.Select(party => party.StringId))}].");
        }

        private void SustainSiege(Army army, SiegeAssignment job, double now)
        {
            if (army.Cohesion >= 50 || now < job.State.UpkeepAfter)
                return;
            job.State.UpkeepAfter = now + 1d;
            ArmyOrders.Sustain(army, "siege:" + job.Target.StringId, _log);
        }

        private void ReleaseSiege(Army army, SiegeAssignment job, double now)
        {
            if (TryReassignSiegeArmy(army, job, now))
                return;
            var kingdom = army.Kingdom;
            bool viabilityLost = job.State.End is "muster-projected-force-no-longer-viable" or "march-no-longer-meets-native-strength";
            var members = viabilityLost ? ArmyOrders.Members(army) : Array.Empty<MobileParty>();
            string? failureKey = job.State.PaidSummons == 0f && !job.State.Issued && job.Muster != null && job.State.End is
                ("cleared-muster-order-not-recoverable" or "native-formation-failed" or "muster-no-observed-progress")
                ? FailedSiegeKey(army.LeaderParty, job.Muster, job.Target) : null;
            if (CanReuseAfterSiege(job))
            {
                var abandoned = job.State.PreviousTargets ?? new HashSet<string>(StringComparer.Ordinal);
                if (job.State.End != KingdomCommander.OutsideSiegeFrontier && !job.State.Captured && !job.State.OwnerEpochEnded &&
                    MilitaryObjective.EnemyFortification(job.Target, kingdom))
                    abandoned.Add(job.Target.StringId);
                if (job.State.End != "home-siege-defense-only" &&
                    BeginIdle(army, now, job.State.End!, abandoned, failedMusterKey: failureKey))
                    return;
            }
            _log.Event($"Siege stand-down: {SiegeId(army, job)} ended={job.State.End} " +
                $"disposition={(CanReuseAfterSiege(job) ? "no-viable-reassignment" : "formation-unavailable-or-unsustainable")}.");
            if (!ArmyOrders.Release(army, _log, "siege:" + job.State.End))
                return;
            _sieges.Remove(army);
            if (viabilityLost)
            {
                if (_offenseReleaseHour != now)
                {
                    _releasedOffenseThisTick.Clear();
                    _offenseReleaseHour = now;
                }
                _releasedOffenseThisTick.UnionWith(members);
            }
            if (failureKey != null)
                RememberFailedMuster(kingdom, failureKey);
        }

        private void RememberFailedMuster(Kingdom kingdom, string key)
        {
            if (_failedSiegeMusters.Count >= 256 && !_failedSiegeMusters.ContainsKey(key))
                _failedSiegeMusters.Remove(_failedSiegeMusters.Keys.First());
            _failedSiegeMusters[key] = ReliefInputs(KingdomForces.Read(kingdom));
        }

        private static bool CanReuseAfterSiege(SiegeAssignment job) =>
            job.State.End is not ("actual-starvation" or "no-eligible-command-or-strength" or "native-formation-failed");

        private bool TryReassignSiegeArmy(Army army, SiegeAssignment old, double now)
        {
            if (!CanReuseAfterSiege(old) || !EligibleSiegeArmy(army))
                return false;
            var kingdom = army.Kingdom;
            if (TryReadyDefense(army, now, old))
                return true;
            if (KingdomCommander.HasHomeSiege(kingdom))
                return false;
            if (old.State.Captured)
                return TrySiegeFollowOn(army, old.Target, now, old.State.UpkeepAfter);
            var visited = old.State.PreviousTargets ?? new HashSet<string>(StringComparer.Ordinal);
            var targets = kingdom.FactionsAtWarWith.Where(f => !f.IsEliminated)
                .SelectMany(f => f.Fiefs).Select(t => t.Settlement)
                .Where(t => t != old.Target && !visited.Contains(t.StringId));
            var choice = KingdomCommander.ChooseSiege(kingdom, targets, new[] { army }, null,
                target => SiegeTargetBlocker(kingdom, target, now, army), _ => false, _ => { }, _ => { },
                (_, _, _) => false, priorCamp: target => PriorCamp(kingdom, target));
            if (choice == null || !StartSiege(kingdom, choice, now, $"reuse-after-{old.State.End}", old.State.UpkeepAfter))
                return false;
            visited.Add(old.Target.StringId);
            _sieges[army].State.PreviousTargets = visited;
            _log.Event($"Siege reassigned intact: {SiegeId(army, _sieges[army])} previous={old.Target.StringId} no new summons or payment.");
            return true;
        }

        private bool TrySiegeFollowOn(Army army, Settlement from, double now, double upkeepAfter)
        {
            if (!EligibleSiegeArmy(army) || !from.IsFortification || KingdomCommander.HasHomeSiege(army.Kingdom))
                return false;
            var kingdom = army.Kingdom;
            var choice = KingdomCommander.ChooseSiege(kingdom, from.Town.GetNeighborFortifications(MobileParty.NavigationType.All),
                new[] { army }, null, target => target == from ? "completed-fief" : SiegeTargetBlocker(kingdom, target, now, army),
                _ => false, _ => { }, _ => { }, (_, _, _) => false, priorCamp: target => PriorCamp(kingdom, target));
            return choice != null && StartSiege(kingdom, choice, now, $"one-ring-after-{from.StringId}", upkeepAfter);
        }

        private static bool EligibleSiegeArmy(Army army) => Registered(army) && !OwnsControlledFixture(army) &&
            ArmyTakeoverBehavior.Current?.IsPending(army) != true && ArmyOrders.CanRelease(army) &&
            ArmyOrders.VisitLock(army.LeaderParty) == null && ArmyOrders.Members(army).All(party => ArmyOrders.CanDirect(party, army.Kingdom));

        private static string FailedSiegeKey(MobileParty leader, Settlement site, Settlement target) => $"{leader.StringId}/{site.StringId}/{target.StringId}";

        private IEnumerable<int> AllocateSiege(Kingdom kingdom, double now)
        {
            if (KingdomCommander.HasHomeSiege(kingdom))
            {
                if (_log.Changed("siege-allocation", kingdom.StringId, "home-siege-defense-only"))
                    _log.Event($"Siege search: kingdom={kingdom.StringId} blocked=home-siege-defense-only; no offensive formation or order.");
                yield break;
            }
            var enemies = kingdom.FactionsAtWarWith.Where(faction => faction != null && faction != kingdom && !faction.IsEliminated).ToArray();
            if (enemies.Length == 0)
                yield break;
            var inventory = KingdomForces.Read(kingdom);
            bool Ready(Army army) => !_relief.ContainsKey(army) && !_sieges.ContainsKey(army) &&
                IdleReliefArmy(army) && EligibleSiegeArmy(army);
            var ready = inventory.Armies.Where(Ready).ToArray();
            var readyOrigins = ready.ToDictionary(army => army, army => KingdomCommander.Origin(army.LeaderParty));
            var openMusters = new HashSet<Settlement>(kingdom.Fiefs.Select(fief => fief.Settlement)
                .Where(site => KingdomCommander.OpenFortification(site, kingdom)));
            var contacts = _activeRelief.Values.Where(job => job.Kingdom == kingdom).ToArray();
            string? inputs = null;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var viable = new List<KingdomCommander.SiegeChoice>();
            var farther = new List<string>();
            bool JustReleased(MobileParty party) => _offenseReleaseHour == now && _releasedOffenseThisTick.Contains(party);
            if (_offenseReleaseHour == now)
            {
                int released = inventory.Parties.Count(row => row.Army == null && row.Exclusion == null && JustReleased(row.Party));
                if (released != 0)
                    counts["same-pass-failed-formation-release"] = released;
            }
            yield return 0;
            KingdomCommander.SiegeChoice? choice = null;
            var targets = enemies.SelectMany(faction => faction.Fiefs).Select(fief => fief.Settlement).Distinct().ToArray();
            // Remember the competitive set's owner/availability, not mutable
            // enumerators. If an unquoted objective becomes eligible, do not
            // spend on a result ranked against the wrong target set.
            var available = targets.Where(target => SiegeTargetBlocker(kingdom, target, now) == null)
                .ToDictionary(target => target, target => target.MapFaction);
            var recentPaid = ActiveCrossTargetRebuys(kingdom, now);
            Func<Settlement, IEnumerable<string>, bool>? blockedRebuy = recentPaid.Length == 0 ? null :
                (target, escorts) => MatchingRebuy(recentPaid, target, escorts) != null;
            if (ready.Length > 0)
                foreach (var step in KingdomCommander.SweepSiege(kingdom, targets, ready, null,
                    target => SiegeTargetBlocker(kingdom, target, now), _ => false,
                    reason => counts[reason] = counts.TryGetValue(reason, out int count) ? count + 1 : 1,
                    viable.Add, (_, _, _) => false, result => choice = result, priorCamp: target => PriorCamp(kingdom, target),
                    allowExisting: IdleTargetAllowed))
                    yield return step;
            if (choice == null)
            {
                foreach (var step in KingdomCommander.SweepSiege(kingdom, targets,
                    Array.Empty<Army>(), inventory, target => SiegeTargetBlocker(kingdom, target, now),
                    party => JustReleased(party) || contacts.Any(job => job.IsLocal(party) && job.Responding(party)),
                    reason => counts[reason] = counts.TryGetValue(reason, out var count) ? count + 1 : 1, viable.Add,
                    (leader, site, target) => _failedSiegeMusters.TryGetValue(FailedSiegeKey(leader, site, target), out string prior) &&
                        prior == (inputs ??= ReliefInputs(inventory)), result => choice = result, line => { if (farther.Count < 3) farther.Add(line); },
                    target => PriorCamp(kingdom, target), target =>
                    {
                        var retry = ReadFreshSiegePolicy(kingdom, target, now);
                        return (retry.rules, retry.work);
                    }, blockedRebuy))
                    yield return step;
            }
            string blockers = string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
            if (choice == null)
            {
                _siegeSearchSummary = $"kingdom={kingdom.StringId} no viable production siege; ready={ready.Length} " +
                    $"sweepSlices={_offensiveSlices} sweepWork={_offensiveWork} blockers=[{blockers}]";
                if (_log.Changed("siege-allocation", kingdom.StringId, string.Join(",", counts.Keys.OrderBy(key => key))))
                    _log.Event($"Siege search: {_siegeSearchSummary}.");
                yield break;
            }
            _log.Changed("siege-allocation", kingdom.StringId, null);
            string alternatives = string.Join(", ", viable.Where(other => other != choice).OrderBy(other => other.Distance).Take(3)
                .Select(other => $"{other.Target.StringId}:route={CampaignLog.Number(other.Distance)}:value={CampaignLog.Number(other.Value)}"));
            _siegeSearchSummary = $"kingdom={kingdom.StringId} selected={choice.Target.StringId} route={CampaignLog.Number(choice.Distance)}";
            var selected = choice;
            _offensiveCommit = liveNow =>
            {
                var target = selected.Target;
                var liveTargets = kingdom.FactionsAtWarWith.Where(faction => faction != null && faction != kingdom && !faction.IsEliminated)
                    .SelectMany(faction => faction.Fiefs).Select(fief => fief.Settlement).Distinct()
                    .Where(site => SiegeTargetBlocker(kingdom, site, liveNow) == null).ToArray();
                bool Reserved(MobileParty party) =>
                    _offenseReleaseHour == liveNow && _releasedOffenseThisTick.Contains(party) ||
                    _activeRelief.Values.Any(job => job.Kingdom == kingdom && job.IsLocal(party) && job.Responding(party));
                double lowerBoundLimit = (selected.Distance + 1d) * (selected.Distance + 1d);
                bool CanCompete(CampaignVec2 point, Settlement site) => point.DistanceSquared(site.GatePosition.ToVec2()) <= lowerBoundLimit;
                var liveMusters = kingdom.Fiefs.Select(fief => fief.Settlement)
                    .Where(site => KingdomCommander.OpenFortification(site, kingdom)).ToArray();
                var liveReady = kingdom.Armies.Where(Ready)
                    .Select(army => (army, point: KingdomCommander.Origin(army.LeaderParty))).ToArray();
                if (kingdom.IsEliminated || !available.TryGetValue(target, out var owner) || owner != target.MapFaction ||
                    liveTargets.Any(site => (!available.TryGetValue(site, out var priorOwner) || priorOwner != site.MapFaction) &&
                        (liveMusters.Any(muster => CanCompete(muster.GatePosition, site)) || liveReady.Any(row => CanCompete(row.point, site)))) ||
                    (selected.Army == null && liveMusters.Any(muster => !openMusters.Contains(muster) &&
                        liveTargets.Any(site => CanCompete(muster.GatePosition, site)))) ||
                    liveReady.Any(row => (!readyOrigins.TryGetValue(row.army, out var prior) || prior != row.point) &&
                        (selected.Army == null || liveTargets.Any(site => CanCompete(row.point, site)))) ||
                    selected.Army is { } current && (!Ready(current) || KingdomCommander.Origin(current.LeaderParty) != selected.Origin) ||
                    !StartSiege(kingdom, selected, liveNow, $"after-defense; sweepSlices={_offensiveSlices} sweepWork={_offensiveWork} " +
                        $"comparedReady={ready.Length} evaluatedViable=[{alternatives}] tooFarToOutrank=[{string.Join(", ", farther)}] blockers=[{blockers}]",
                        liveReserved: Reserved))
                {
                    _siegeSearchSummary = $"kingdom={kingdom.StringId} target={target.StringId} stale offensive proposal; no new order";
                    if (_log.Changed("siege-stale-proposal", kingdom.StringId, target.StringId))
                        _log.Event($"Siege search: {_siegeSearchSummary}; next ordinary visit.");
                }
                else
                    _log.Changed("siege-stale-proposal", kingdom.StringId, null);
            };
        }

        private bool StartSiege(Kingdom kingdom, KingdomCommander.SiegeChoice choice, double now, string reason, double upkeepAfter = 0,
            Func<MobileParty, bool>? liveReserved = null)
        {
            var target = choice.Target;
            if (SiegeTargetBlocker(kingdom, target, now, choice.Army, out var foothold, out int links) != null)
                return false;
            string frontier = $"frontierAnchor={foothold!.StringId} frontierLinks={links}";
            var state = new SiegeState { Kingdom = kingdom.StringId, Owner = target.MapFaction.StringId, UpkeepAfter = upkeepAfter };
            var job = new SiegeAssignment(target, choice.Muster?.Muster, state);
            var plan = choice.Muster;
            var army = choice.Army;
            if (army != null)
            {
                float actual = KingdomCommander.SiegePresent(army, kingdom);
                float roster = KingdomCommander.SiegeRoster(army);
                if (!EligibleSiegeArmy(army) || !IdleTargetAllowed(army, target) || !CanIssueSiege(army, job, now, actual, roster, out float score, out float checkedPower))
                    return false;
                if (_idle.TryGetValue(army, out var idle))
                    state.PreviousTargets = idle.Abandoned;
                EndIdle(army, "siege-assigned");
                state.CommittedPower = actual;
                _sieges[army] = job;
                _log.Event($"Siege selected: {SiegeId(army, job)} formation=existing route={CampaignLog.Number(choice.Distance)} " +
                    $"nativeValue={CampaignLog.Number(choice.Value)} present={CampaignLog.Number(choice.Strength)} score={CampaignLog.Number(choice.Score)} {frontier} why={reason}.");
                IssueSiege(army, job, now, actual, roster, score, checkedPower);
                return true;
            }
            var leader = plan!.Leader.Party;
            if (ArmyOrders.FreePartyBlocker(leader, kingdom, true) != null || ArmyOrders.VisitLock(leader) != null ||
                leader.Party.IsStarving || liveReserved?.Invoke(leader) == true ||
                !KingdomCommander.OpenFortification(plan.Muster, kingdom) || !ArmyOrders.CanPathVisit(leader, plan.Muster) ||
                !KingdomCommander.LiveSiegeRoute(leader, plan.Muster.GatePosition, target))
                return false;
            // Validate the winning prefix before making even the zero-cost
            // army. One linear pass over the chosen calls; no candidate search
            // or substitutes here. Summon still enforces the native debit.
            var retry = ReadFreshSiegePolicy(kingdom, target, now);
            float projected = KingdomCommander.SiegeEstimate(leader);
            int cost = 0;
            var paidQuotes = new HashSet<string>(StringComparer.Ordinal);
            double assembly = KingdomCommander.ReliefMusterHours(leader, plan.Muster);
            if (double.IsInfinity(assembly))
                return false;
            bool readyMuster = !retry.rules.ReadyMuster || leader.CurrentSettlement == plan.Muster;
            foreach (var call in plan.Calls)
            {
                var member = call.Party;
                double hours = KingdomCommander.ReliefMusterHours(member, plan.Muster);
                if (ArmyOrders.FreePartyBlocker(member, kingdom, false) != null || liveReserved?.Invoke(member) == true ||
                    member.Party.IsStarving || !KingdomCommander.WithinCallRange(leader, member) ||
                    double.IsInfinity(hours))
                    return false;
                int price = ArmyOrders.CallCost(leader, member);
                if (price < 0 || !ArmyOrders.Affordable(leader.LeaderHero.Clan.Influence, cost += price))
                    return false;
                if (price > 0)
                    paidQuotes.Add(member.StringId);
                if (readyMuster && retry.rules.ReadyMuster &&
                    (hours > SiegeMusterStallHours || !ArmyOrders.CanPathVisit(member, plan.Muster)))
                    readyMuster = false;
                assembly = Math.Max(assembly, hours);
                projected += KingdomCommander.SiegeEstimate(member);
            }
            bool allowPaid = paidQuotes.Count > 0;
            if (allowPaid && (retry.rules.PaidBlock != null || !readyMuster))
                return false;
            var rebuyRecords = allowPaid ? ActiveCrossTargetRebuys(kingdom, now) : Array.Empty<CrossTargetRebuy>();
            if (MatchingRebuy(rebuyRecords, target, paidQuotes) is { } blockedPurchase)
            {
                LogBlockedCrossTargetRebuy(kingdom, target, blockedPurchase, "commit");
                return false;
            }
            float conservativePower = KingdomCommander.ConservativeSiegePower(projected);
            if (projected < retry.rules.RequiredPower(allowPaid) ||
                !KingdomCommander.SiegeStrength(leader, target, conservativePower, out float liveScore) ||
                KnownSiegeThreat(leader, target, projected))
                return false;
            string quoteContext = $"scorer={leader.StringId} quoteOrder={leader.DefaultBehavior} quotePosition={Position(leader.Position)} " +
                $"quoteSite={leader.CurrentSettlement?.StringId ?? "-"} {ScorerState(leader, target, SiegeDefenders(target))}";
            state.CommittedPower = projected;
            state.AttemptId = Guid.NewGuid().ToString("N");
            state.PaidRecovery = allowPaid && retry.episode != null;
            army = ArmyOrders.Create(kingdom, leader, plan.Muster, _log, out bool formed);
            _sieges[army] = job;
            if (!formed)
            {
                state.End = "native-formation-failed";
                return false;
            }
            var calls = new List<string>();
            job.CommittingSummons = true;
            try
            {
                // Present the same selected calls, but charge genuinely new
                // escorts first. An overlap cannot be justified by an unpaid
                // pledge that a later native call may refuse or price at zero.
                var recentCrossTarget = rebuyRecords.Where(record => record.Target != target.StringId).ToArray();
                IEnumerable<KingdomCommander.Candidate> summons = recentCrossTarget.Length == 0 ? plan.Calls :
                    plan.Calls.OrderBy(call => paidQuotes.Contains(call.Party.StringId) &&
                        recentCrossTarget.Any(record => record.Escorts.Contains(call.Party.StringId)));
                foreach (var call in summons)
                {
                    if (!Registered(army) || job.DispersalDuringSummons != null || SiegeAssignmentBlocker(kingdom, target, now, army, out _, out _) != null ||
                        target.MapFaction?.StringId != state.Owner || state.OwnerEpochEnded)
                        break;
                    if (KingdomCommander.WithinCallRange(leader, call.Party) &&
                        ArmyOrders.Summon(army, call.Party, _log, paidQuotes.Contains(call.Party.StringId), amount =>
                        {
                            if (state.PaidSummons == 0f && (state.PaidRecovery || retry.hasFailedStrength))
                                state.RecoveryDeadline = now + assembly + SiegeMusterStallHours;
                            RecordPaidSummons(army, job, call.Party, amount);
                        }, recentCrossTarget.Length == 0 ? null : () =>
                        {
                            IEnumerable<string> wouldPay = state.PaidEscorts?.Append(call.Party.StringId) ?? new[] { call.Party.StringId };
                            if (MatchingRebuy(recentCrossTarget, target, wouldPay) is not { } blockedCharge)
                                return true;
                            LogBlockedCrossTargetRebuy(kingdom, target, blockedCharge, "debit");
                            return false;
                        }))
                        calls.Add($"{call.Party.StringId}:eta=+{CampaignLog.Number(call.Hours)}h:cost={call.Cost}");
                }
            }
            finally
            {
                job.CommittingSummons = false;
                if (job.DispersalDuringSummons is string end)
                    FinishPaidSiege(army, job, now, end);
                else if (!Registered(army))
                    FinishPaidSiege(army, job, now, state.End ?? "formation-lost-during-summons");
            }
            _log.Event($"Siege selected: {SiegeId(army, job)} formation=muster site={plan.Muster.StringId} " +
                $"route={CampaignLog.Number(choice.Distance)} nativeValue={CampaignLog.Number(choice.Value)} " +
                $"projected={CampaignLog.Number(choice.Strength)} affordablePrefixPower={CampaignLog.Number(choice.AffordablePrefixPower)} " +
                $"conservativeScore={CampaignLog.Number(choice.Score)} {quoteContext} costQuoted={plan.Cost} " +
                $"commitPower={CampaignLog.Number(projected)} conservativePower={CampaignLog.Number(conservativePower)} " +
                $"commitScore={CampaignLog.Number(liveScore)} retryFloor={CampaignLog.Number(retry.rules.Floor)} " +
                $"retry={(retry.weakerDefenses ? "weaker-defenses" : retry.rules.Floor > 0f ? "stronger-roster" : retry.rules.ReadyMuster ? "ready-muster" : "-")} " +
                $"paidRetryFloor={CampaignLog.Number(retry.rules.PaidFloor)} commitCost={cost} " +
                $"actualSummonsDebit={CampaignLog.Number(state.PaidSummons)} attempt={state.AttemptId} {frontier} " +
                $"calls=[{string.Join(", ", calls)}] why={reason}.");
            _siegeReview = true;
            return true;
        }
    }
}
