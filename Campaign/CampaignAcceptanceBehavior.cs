using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed class CampaignAcceptanceBehavior : CampaignBehaviorBase
    {
        internal const string Marker = "acceptance-reuse-v1.armed";
        internal const string ReliefMarker = "acceptance-relief-v1.armed";
        internal const string AlliedReliefMarker = "acceptance-allied-relief-v1.armed";
        internal const string SiegeMarker = "acceptance-siege-v1.armed";
        internal const string CoordinationMarker = "acceptance-coordination-v1.armed";
        private const double RunHours = 360d, GuardHours = 1d, MaxQuotedSiegeHours = 48d, TravelMargin = 12d;
        // Solo quotes can be much faster than a merged army moving at night.
        // These are admission allowances, not changes to production movement.
        private const double SetupSlowdown = 2d, SiegeSlowdown = 3d;
        private const int MaxAttempts = 3, BatchSize = 4;
        private readonly MilitaryCommandBehavior _command;
        private readonly MilitaryCommandBehavior _controlledAttack;
        private readonly CampaignLog _log;
        private readonly string _directory;
        private string? _id, _result, _verdict;
        private long _wallDeadline, _cleanupDeadline;
        private double _deadline, _attemptDeadline, _nextSearch;
        private int _attempts, _cursor;
        private string? _triedPairs;
        private double _armedAt, _nextDiagnostic;
        private readonly Dictionary<string, int> _rejections = new();
        private string? _searchSummary, _lastAttempt, _searchWait;
        private bool _mayRearm;
        private Settlement? _guard, _target;
        private Army? _original;
        private bool _shown, _loaded, _handoff, _clockSet;
        private bool _guardSeen, _campContact;
        private bool _terminalCleanupRequested;
        private string? _armyId;
        private bool _homeRelief;
        private bool _alliedRelief;
        private bool _siegeObserver;
        private bool _coordinationObserver;
        private string? _reliefEvidence, _lastObservedSiege;
        private double _watchNaturalUntil;

        internal CampaignAcceptanceBehavior(MilitaryCommandBehavior command, CampaignLog log, string directory) :
            this(command, log, directory, command) { }

        internal CampaignAcceptanceBehavior(MilitaryCommandBehavior command, CampaignLog log, string directory, MilitaryCommandBehavior controlledAttack)
            : base("BannerlordMilitaryAIOverhaul.CampaignAcceptance.ReuseV1")
            => (_command, _log, _directory, _controlledAttack) = (command, log, directory, controlledAttack);

        private bool ReliefMode => _homeRelief || _alliedRelief;
        private bool ObserverOnly => _siegeObserver || _coordinationObserver;
        private string ReceiptScenario => _coordinationObserver ? "coordination-v1" : _siegeObserver ? "siege-v1" :
            _alliedRelief ? "allied-relief-v1" : _homeRelief ? "relief-v1" : "reuse-v1";
        private MilitaryCommandBehavior Attacker => ReliefMode ? _controlledAttack : _command;

        public override void RegisterEvents()
        {
            CampaignEvents.OnAfterSessionLaunchedEvent.AddNonSerializedListener(this, _ => OnLoaded());
            CampaignEvents.TickEvent.AddNonSerializedListener(this, _ => Tick(CampaignTime.Now.ToHours, DateTime.UtcNow.Ticks));
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, ObserveCamp);
        }

        public override void SyncData(IDataStore store)
        {
            if (store.IsSaving && _id == null)
                return;
            store.SyncData("run", ref _id);
            store.SyncData("result", ref _result);
            store.SyncData("verdict", ref _verdict);
            store.SyncData("wallDeadline", ref _wallDeadline);
            store.SyncData("cleanupDeadline", ref _cleanupDeadline);
            store.SyncData("deadline", ref _deadline);
            store.SyncData("attemptDeadline", ref _attemptDeadline);
            store.SyncData("nextSearch", ref _nextSearch);
            store.SyncData("attempts", ref _attempts);
            store.SyncData("cursor", ref _cursor);
            store.SyncData("triedPairs", ref _triedPairs);
            store.SyncData("armedAt", ref _armedAt);
            store.SyncData("searchSummary", ref _searchSummary);
            store.SyncData("lastAttempt", ref _lastAttempt);
            store.SyncData("guard", ref _guard);
            store.SyncData("target", ref _target);
            store.SyncData("army", ref _original);
            store.SyncData("armyId", ref _armyId);
            store.SyncData("handoff", ref _handoff);
            store.SyncData("guardSeen", ref _guardSeen);
            store.SyncData("campContact", ref _campContact);
            store.SyncData("shown", ref _shown);
            store.SyncData("homeRelief", ref _homeRelief);
            store.SyncData("alliedRelief", ref _alliedRelief);
            store.SyncData("siegeObserver", ref _siegeObserver);
            store.SyncData("coordinationObserver", ref _coordinationObserver);
            store.SyncData("reliefEvidence", ref _reliefEvidence);
            store.SyncData("lastObservedSiege", ref _lastObservedSiege);
            store.SyncData("watchNaturalUntil", ref _watchNaturalUntil);
        }

        private void OnLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            if (_id == null)
                ArmIfPending();
            else
            {
                Write($"resumed attempts={_attempts} army={_armyId ?? "-"} handoff={_handoff} contact={_campContact}.");
                if (_result == null)
                    _result = ReadReceipt("result");
                _shown |= File.Exists(Receipt("shown"));
                _mayRearm = _result != null && _shown;
                if (_mayRearm && !(Owns && Attacker.FixtureBusy))
                    ArmIfPending();
            }
            _command.TrackCoordinationContests = _coordinationObserver && _id != null && _result == null;
        }

        private void ArmIfPending()
        {
            _mayRearm = false;
            string? next = ClaimScenario(_directory, CoordinationMarker, "coordination-v1");
            bool coordination = next != null;
            bool siege = !coordination && (next = ClaimScenario(_directory, SiegeMarker, "siege-v1")) != null;
            bool allied = !coordination && !siege && (next = ClaimScenario(_directory, AlliedReliefMarker, "allied-relief-v1")) != null;
            bool relief = !coordination && !siege && !allied && (next = ClaimScenario(_directory, ReliefMarker, "relief-v1")) != null;
            next ??= Claim(_directory);
            if (next == null)
                return;
            _homeRelief = relief;
            _alliedRelief = allied;
            _siegeObserver = siege;
            _coordinationObserver = coordination;
            _command.TrackCoordinationContests = coordination;
            _id = next;
            _result = _verdict = _armyId = _searchSummary = _lastAttempt = _searchWait = null;
            _guard = _target = null;
            _original = null;
            _shown = _guardSeen = _handoff = _campContact = _clockSet = _terminalCleanupRequested = false;
            _attempts = _cursor = 0;
            _triedPairs = null;
            _nextSearch = _nextDiagnostic = _attemptDeadline = 0;
            _cleanupDeadline = 0;
            _watchNaturalUntil = 0;
            _reliefEvidence = _lastObservedSiege = null;
            _rejections.Clear();
            _armedAt = CampaignTime.Now.ToHours;
            _wallDeadline = DateTime.UtcNow.AddMinutes(15).Ticks;
            _deadline = _armedAt + RunHours;
            Write($"armed scenario={ReceiptScenario}; deadline={CampaignLog.Number(_deadline)} " + (ObserverOnly ?
                "read-only production observer; no test orders." :
                $"guardHours={GuardHours} quotedSiegeLimit={MaxQuotedSiegeHours} attemptLimit={MaxAttempts}."));
        }

        // A claim is a same-directory rename; a stale save does not restore the arm.
        internal static string? Claim(string directory)
            => ClaimScenario(directory, Marker, "reuse-v1");

        private static string? ClaimScenario(string directory, string name, string scenario)
        {
            string marker = Path.Combine(directory, name);
            try
            {
                var words = File.ReadAllText(marker).Trim().Split(' ');
                if (words.Length != 2 || words[0] != scenario || !Guid.TryParseExact(words[1], "N", out var run))
                    return null;
                string id = run.ToString("N");
                File.Move(marker, Path.Combine(directory, $"acceptance-{scenario}.{id}.claimed"));
                return id;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private string Receipt(string kind) => Path.Combine(_directory, $"acceptance-{ReceiptScenario}.{_id}.{kind}");
        private string? ReadReceipt(string kind)
        {
            try { return File.ReadAllText(Receipt(kind)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private bool SaveReceipt(string kind, string text)
        {
            try
            {
                using var stream = new FileStream(Receipt(kind), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(text);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal void Frame(NativeCampaign campaign)
        {
            if (!_loaded || _id == null)
                return;
            using var timing = _result == null ? CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.Acceptance) : default;
            bool safeMap = CanUseMap(campaign);
            long wall = DateTime.UtcNow.Ticks;
            if (_result == null && _verdict == null && wall >= _wallDeadline)
                End(_coordinationObserver ? CoordinationInconclusive("wall-clock limit") : _siegeObserver ? SiegeInconclusive("wall-clock limit") :
                    "INCONCLUSIVE: wall-clock limit reached (map or scenario did not progress)", wall);
            if (_verdict != null)
                Settle(wall);
            if (_result != null)
            {
                if (!_terminalCleanupRequested && Owns)
                {
                    Attacker.CancelAcceptance(_id);
                    _terminalCleanupRequested = true;
                }
                // A new marker can wait behind a previous run's owned cleanup.
                // Do not consume it or overwrite the preceding receipts early.
                if (safeMap && _mayRearm && !(Owns && Attacker.FixtureBusy))
                {
                    ArmIfPending();
                    if (_result == null)
                        return;
                }
                if (safeMap && InformationManager.IsAnyInquiryActiveInternal != null)
                    Notify();
                return;
            }
            if (safeMap && !_clockSet && !campaign.TimeControlModeLock)
            {
                _clockSet = true;
                if (campaign.TimeControlMode is CampaignTimeControlMode.Stop or CampaignTimeControlMode.FastForwardStop)
                    campaign.TimeControlMode = CampaignTimeControlMode.StoppableFastForward;
            }
        }

        private static bool CanUseMap(NativeCampaign campaign) =>
            Game.Current?.GameStateManager?.ActiveState is MapState { AtMenu: false } &&
            campaign.PlayerEncounter == null && !InformationManager.IsAnyInquiryActive();

        internal void Notify()
        {
            if (_shown || _result == null)
                return;
            _shown = true;
            if (!SaveReceipt("shown", _result) && File.Exists(Receipt("shown")))
                return;
            InformationManager.ShowInquiry(new InquiryData("MAIO campaign acceptance", _result +
                "\n\nReturn to the coordinator with this result and run ID. Details are in the MAIO Desktop log.",
                true, false, "OK", "", () => { }, null), true);
        }

        private bool Owns => !ObserverOnly && Attacker.Fixture.Owner == _id;

        private void Tick(double now, long wall)
        {
            if (!_loaded || _id == null || _result != null)
                return;
            using var timing = CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.Acceptance);
            if (_verdict != null)
            {
                Settle(wall);
                return;
            }
            try { Advance(now, wall); }
            catch (Exception error)
            {
                Write($"defect: {error}");
                End($"FAIL: runner exception ({error.GetType().Name})", wall);
            }
        }

        private void Advance(double now, long wall)
        {
            if (_coordinationObserver)
            {
                ObserveCoordination(now, wall);
                return;
            }
            if (_siegeObserver)
            {
                ObserveProductionSiege(now, wall);
                return;
            }
            if (ReliefMode && ObserveDefenders(wall))
                return;
            if (_target != null && Owns)
            {
                var fixture = Attacker.Fixture;
                if (fixture.Army != null && fixture.Army != _original)
                {
                    End("FAIL: commander replaced the original army", wall);
                    return;
                }
                if (fixture.Phase == MilitaryCommandBehavior.FixturePhase.Guarding && !_guardSeen)
                {
                    var leader = fixture.Army?.LeaderParty;
                    if (leader?.DefaultBehavior != AiBehavior.DefendSettlement || fixture.Target != _guard ||
                        leader.TargetSettlement != _guard || fixture.Army?.AiBehaviorObject != _guard ||
                        leader.CurrentSettlement != _guard)
                    {
                        End("FAIL: original army did not receive the nominated native guard order", wall);
                        return;
                    }
                    _guardSeen = true;
                    Write($"guard originalArmy={_armyId} target={_guard?.StringId} order=DefendSettlement.");
                }
                if (fixture.Phase == MilitaryCommandBehavior.FixturePhase.Besieging)
                {
                    var army = fixture.Army;
                    if (!_guardSeen || army == null || fixture.Target != _target || army.AiBehaviorObject != _target ||
                        army.LeaderParty.TargetSettlement != _target || army.LeaderParty.DefaultBehavior != AiBehavior.BesiegeSettlement)
                    {
                        End("FAIL: follow-on did not issue the nominated native siege order on the original army", wall);
                        return;
                    }
                    if (!_handoff)
                        Write($"handoff originalArmy={_armyId} guard={_guard?.StringId} target={_target.StringId} order=BesiegeSettlement.");
                    _handoff = true;
                    if (army.LeaderParty.SiegeEvent is SiegeEvent siege)
                        ObserveCamp(siege);
                }
                if (_handoff && _campContact)
                {
                    if (!ReliefMode)
                    {
                        End($"PASS: same army {_armyId} guarded {_guard?.StringId}, took the siege order and joined a native camp at {_target.StringId}", wall);
                        return;
                    }
                    // In relief mode an attacker in camp is setup only.
                    // Leave it in native execution while the independent
                    // production defenders respond, through the overall limit.
                }
                if (!Attacker.FixtureBusy)
                {
                    if (_lastAttempt is not ("attempt-contact-timeout" or "test-triad-no-longer-eligible"))
                        _lastAttempt = fixture.Outcome ?? "unknown";
                    Write($"attempt={_attempts} ended without contact: {_lastAttempt}.");
                    if (fixture.Outcome is string outcome &&
                        (outcome.Contains("native-formation-failed") || outcome.Contains("order-no-longer-active") || outcome.Contains("commander-move-no-longer-active")))
                    {
                        End($"FAIL: {outcome}", wall);
                        return;
                    }
                    _target = null;
                    _original = null;
                    _nextSearch = now;
                }
                else if (_alliedRelief && fixture.Phase != MilitaryCommandBehavior.FixturePhase.Closing &&
                    fixture.Army?.Kingdom is Kingdom attacker && (_target.MapFaction is not Kingdom owner ||
                        !owner.IsAtWarWith(attacker) || !EligibleRelievers(owner, attacker).Any()))
                {
                    _lastAttempt = "test-triad-no-longer-eligible";
                    Write($"test siege no longer has an eligible alliance/war triad at {_target.StringId}; requested safe cleanup.");
                    Attacker.CancelAcceptance(_id!);
                    return;
                }
                else if (now >= _attemptDeadline && !(ReliefMode && _campContact))
                {
                    _lastAttempt = "attempt-contact-timeout";
                    Attacker.CancelAcceptance(_id!);
                }
            }
            else if (_target != null)
            {
                End("FAIL: tracked assignment ownership was lost", wall);
                return;
            }

            if (now >= _deadline || wall >= _wallDeadline || (_target == null && _attempts >= MaxAttempts))
            {
                End(ReliefMode ? $"INCONCLUSIVE: no physical {(_alliedRelief ? "allied" : "home")} production relief confirmed; " +
                    $"last siege={_lastObservedSiege ?? "none"}, last setup={_lastAttempt ?? (_campContact ? "native camp reached" : "none or pending")}, " +
                    $"defender={_reliefEvidence ?? "not observed"}" :
                    _attempts == 0 ? "INCONCLUSIVE: no feasible army and border handoff found within limits" :
                    $"INCONCLUSIVE: no successful contact within limits; last attempt={_lastAttempt ?? "contact pending"}", wall);
                return;
            }
            if (_target != null || now < _nextSearch)
                return;
            if (!_command.FixtureReady || !Attacker.FixtureReady || !CanUseMap(NativeCampaign.Current))
            {
                _searchWait = !_command.FixtureReady ? "commander not ready" : "map UI not ready";
                return;
            }
            if (ReliefMode && WaitForNativeSiege(now))
            {
                _nextSearch = now + 2d;
                return;
            }
            if (Attacker.FixtureBusy)
            {
                _searchWait = "prior fixture owns slot";
                _nextSearch = now + 2d;
                if (!ReliefMode && _command.ClearIdleManualFixture())
                    Write("requested safe release of the old manual fixture; no overlapping assignment.");
                else if (now >= (_armedAt > 0 ? _armedAt : _deadline - 96d) + 8d)
                    End("INCONCLUSIVE: prior assignment still owns the commander slot; left its protected activity alone", wall);
                return;
            }
            _nextSearch = now + 2d;
            Search(now);
        }

        private string SiegeInconclusive(string limit) =>
            $"INCONCLUSIVE: {limit}; " + (_campContact ? $"saw same-army native camp ({_armyId}/{_target?.StringId}) but no native assault" :
                _handoff ? $"saw production departure ({_armyId}/{_target?.StringId}) but no same-army native camp" :
                "no qualifying fresh production departure") + (_lastAttempt == null ? "" : $"; observed closure: {_lastAttempt}");

        private string CoordinationInconclusive(string limit) =>
            $"INCONCLUSIVE: {limit}; no contemporaneous same-kingdom competing plan with two distinct production armies " +
            "executing the plan under native orders";

        private void ObserveCoordination(double now, long wall)
        {
            if (now >= _deadline || wall >= _wallDeadline)
            {
                End(CoordinationInconclusive(now >= _deadline ? "campaign-hour limit" : "wall-clock limit"), wall);
                return;
            }
            if (now < _nextDiagnostic)
                return;
            _nextDiagnostic = now + 0.25d;
            var all = _command.ReadCommitments(null);
            var contenders = _command.CoordinationContests.Where(note => note.Hour >= _armedAt).ToArray();
            var multi = all.Where(claim => !claim.Closing && claim.Army.Kingdom != null)
                .GroupBy(claim => claim.Army.Kingdom.StringId).Where(group => group.Select(claim => claim.Army).Distinct().Count() > 1)
                .Select(group => $"{group.Key}={group.Count(claim => claim.NativeOrder)} ordered/{group.Count()} committed").ToArray();
            string observed = $"recent competing choices={contenders.Length}; same-kingdom multiple formations=[{string.Join(", ", multi)}]";
            _searchSummary = $"{observed}; {_lastAttempt ?? "no comparable split, joint defense, or diversion observed"}";
            if (MilitaryCommandBehavior.ConflictingClaims(all) is { Length: > 0 } duplicates)
            {
                End($"FAIL: simultaneous production assignments name the same parties: {string.Join(", ", duplicates.Select(party => party.StringId))}", wall);
                return;
            }
            bool Executing(MilitaryCommandBehavior.Commitment claim) =>
                !claim.Closing && claim.NativeOrder && claim.Army.LeaderParty.IsActive && claim.Present.Length != 0 &&
                !claim.Members.Any(party => party.IsMainParty) &&
                claim.Members.All(party => party.Army == claim.Army);
            string? mostRecent = null;
            foreach (var note in contenders.OrderByDescending(item => item.Hour))
            {
                var king = note.Higher.Kingdom;
                if (!note.Higher.StillEligible)
                    continue;
                var live = all.Where(claim => claim.Army.Kingdom == king && Executing(claim)).ToArray();
                var higher = live.Where(claim => claim.Job != "siege" && claim.Target == note.Higher.Target &&
                    claim.DepartureHour >= note.Hour && note.Chosen.Contains(claim.Army) &&
                    (claim.Army.LeaderParty.MapEvent == null || note.Higher.InReliefBattle(claim.Army.LeaderParty))).ToArray();
                string label = $"{king.StringId}/{note.Higher.Target.StringId}";
                if (higher.Length == 0)
                {
                    Progress("competing decision observed; awaiting a native defense departure");
                    continue;
                }
                foreach (var (job, routes, understrengthTransfer) in note.Lower.Where(pair => pair.job.StillEligible))
                {
                    var split = live.FirstOrDefault(claim => claim.Job != "siege" && claim.Target == job.Target && routes.Contains(claim.Army));
                    if (split != null && higher.All(claim => claim.Army != split.Army))
                    {
                        Pass("split defenses", higher.Append(split), $"routable competition at {job.Target.StringId}");
                        return;
                    }
                    if ((note.NeedsBoth || understrengthTransfer) && higher.Length == note.Chosen.Length && !all.Any(claim =>
                        !claim.Closing && claim.Army.Kingdom == king && claim.Target == job.Target))
                    {
                        Pass(understrengthTransfer ? "joint understrength defense transfer" : "joint defense", higher,
                            $"lower defense {job.Target.StringId} remains eligible and unassigned");
                        return;
                    }
                }
                var diverted = higher.Where(claim => note.Offense.Any(prior => prior.army == claim.Army)).ToArray();
                var continuing = live.FirstOrDefault(claim => claim.Job == "siege" && claim.Target != note.Higher.Target &&
                    note.Offense.Any(prior => prior.army == claim.Army && prior.target == claim.Target) &&
                    MilitaryObjective.EnemyFortification(claim.Target, king));
                if (diverted.Length > 0 && continuing != null)
                {
                    Pass("defense diversion with continuing offense", diverted.Append(continuing), "retained the other commissioned siege");
                    return;
                }
                if (note.NeedsBoth && diverted.Length == note.Chosen.Length && diverted.Length >= 2)
                {
                    Pass("joint defense diversion", diverted, "both previously ordered sieges yielded to the same defense");
                    return;
                }
                Progress("native defense ordered; no matching concurrent second outcome yet");

                void Progress(string reason)
                {
                    string message = $"{label}: {reason}";
                    mostRecent ??= message;
                    if (_log.Changed("acceptance-coordination", $"{_id}/{label}", reason))
                        Write(message);
                }
                void Pass(string mode, IEnumerable<MilitaryCommandBehavior.Commitment> claims, string detail)
                {
                    var sample = claims.ToArray();
                    var orders = sample.Select(claim => $"{CampaignLog.ArmyId(claim.Army)}:{claim.Job}/{claim.Target.StringId}:" +
                        $"{claim.Army.LeaderParty.DefaultBehavior} present=[{string.Join(",", claim.Present.Select(party => party.StringId))}] " +
                        $"traveling=[{string.Join(",", claim.Traveling.Select(party => party.StringId))}]");
                    Write($"coordination proof: kingdom={king.StringId} choiceHour={note.Hour:R} observedHour={now:R} mode={mode} " +
                        $"actual=[{string.Join("; ", orders)}] duplicateClaims=0; {detail}.");
                    _searchSummary = $"{observed}; qualified {label}";
                    End($"PASS: {king.StringId} {mode}; {sample.Length} distinct production armies executing together under native orders, " +
                        $"with no duplicate party claims; {detail}. Exact headless geometries are separate evidence", wall);
                }
            }
            if (mostRecent != null)
            {
                _lastAttempt = mostRecent;
                _searchSummary = $"{observed}; {mostRecent}";
            }
        }

        private void ObserveProductionSiege(double now, long wall)
        {
            _searchSummary = _command.SiegeSearchSummary;
            var current = _command.SiegeObservations
                .Where(item => item.DepartureHour >= _armedAt && item.Fresh && item.Army.Kingdom != null &&
                    ArmyOrders.HasSiegeOrder(item.Army, item.Target) && !ArmyOrders.Members(item.Army).Any(p => p.IsMainParty))
                .OrderByDescending(item => item.Assault && item.Camp).ThenByDescending(item => item.Camp).ToArray();
            if (_original != null && _command.SiegeClosure(_original, _target) is string close &&
                _log.Changed("acceptance-siege-end", _id!, $"{_armyId}/{_target?.StringId}/{close}"))
            {
                _lastAttempt = $"{_armyId}/{_target?.StringId} {close}";
                Write($"production continuation ended: army={_armyId} target={_target?.StringId} reason={close}; observer did not change orders.");
            }
            foreach (var item in current)
            {
                var army = item.Army;
                string key = $"{CampaignLog.ArmyId(army)}/{item.Target.StringId}/{item.DepartureHour:R}";
                if (_log.Changed("acceptance-siege-evidence-" + _id, key, item.Camp ? item.Assault ? "assault" : "camp" : "departed"))
                    Write($"production siege evidence: army={CampaignLog.ArmyId(army)} kingdom={army.Kingdom?.StringId} " +
                        $"target={item.Target.StringId} nativeOrder={army.LeaderParty.DefaultBehavior} campAbsentAtDeparture=True " +
                        $"departureHour={item.DepartureHour:R} sameArmyNativeCamp={item.Camp} nativeAssault={item.Assault}.");
                if (!_handoff || (item.Camp && !_campContact) || item.Assault && item.Camp)
                {
                    _handoff = true;
                    _campContact |= item.Camp;
                    (_original, _armyId, _target) = (army, CampaignLog.ArmyId(army), item.Target);
                }
                if (item.Camp && item.Assault)
                {
                    End($"PASS: production army {_armyId} of {army.Kingdom?.StringId} selected and departed for {item.Target.StringId} " +
                        "on the native besiege order with no camp; the same army reached a native camp and began a native assault; " +
                        "capture and subsequent withdrawal were not required or inferred", wall);
                    return;
                }
            }
            if (now >= _deadline || wall >= _wallDeadline)
                End(SiegeInconclusive(now >= _deadline ? "campaign-hour limit" : "wall-clock limit"), wall);
        }

        private bool ObserveDefenders(long wall)
        {
            foreach (var response in _command.ReliefObservations)
            {
                var job = response.Job;
                var army = response.Army;
                if (job.IsHome == _alliedRelief || army == _original || army.Kingdom != job.Kingdom || !job.Kingdom.Armies.Contains(army) ||
                    army.LeaderParty.Army != army || job.Target.SiegeEvent != job.Siege ||
                    !job.StillEligible)
                    continue;
                string observation = $"{job.Kingdom.StringId}/{job.Target.StringId}/{CampaignLog.ArmyId(army)}/{response.Phase}";
                if (_log.Changed("acceptance-relief-" + _id, CampaignLog.ArmyId(army), observation))
                {
                    _reliefEvidence = observation;
                    Write($"production defense observed {observation} attached={CampaignLog.Number(response.Attached)} " +
                        $"local={CampaignLog.Number(response.Local)} campPower={CampaignLog.Number(job.CampPower)}.");
                }
                if (!response.Physical || !ArmyOrders.HasDefendOrder(army, job.Target) ||
                    ArmyOrders.Members(army).Any(party => party.IsMainParty))
                    continue;
                // For a generated camp the original attacker's order/contact
                // must also be proven. Other natural sieges are independent.
                if (_target == job.Target && _original?.LeaderParty.BesiegerCamp == job.Siege.BesiegerCamp && !_campContact)
                    continue;
                End($"PASS: production army {CampaignLog.ArmyId(army)} of {job.Kingdom.StringId} defended {job.Scope} {job.Target.StringId} " +
                    $"owned by {job.Owner.StringId} against {job.Besieger.StringId}; " +
                    $"physical response={response.Phase}, local strength={CampaignLog.Number(response.Local)}, " +
                    $"siege active at observation; settlement saved was not established", wall);
                return true;
            }
            return false;
        }

        private bool WaitForNativeSiege(double now)
        {
            var besieged = KingdomCommander.BesiegedFortifications(Kingdom.All);
            var jobs = Kingdom.All.SelectMany(kingdom => KingdomCommander.ReliefObjectives(kingdom, besieged))
                .Where(job => job.IsHome != _alliedRelief).ToArray();
            _rejections["navalEligibleSieges"] = jobs.Count(job => !job.HasLandCamp);
            var active = jobs.Select(job => $"{job.Kingdom.StringId}->{job.Owner.StringId}/{job.Target.StringId}{(job.HasLandCamp ? "" : "(naval)")}")
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (active.Length == 0)
                return now < _watchNaturalUntil;
            string sites = string.Join(", ", active.Take(8)) + (active.Length > 8 ? $" (+{active.Length - 8})" : "");
            if (_lastObservedSiege != sites)
            {
                _lastObservedSiege = sites;
                Write($"watching eligible live siege(s): {sites}; only {(_alliedRelief ? "allied" : "home")} production defense can pass.");
            }
            if (_alliedRelief && jobs.All(job => !job.HasLandCamp))
                return false; // an unsupported naval siege cannot exercise the land scenario
            _watchNaturalUntil = now + 2d;
            _searchWait = "eligible live siege present; no test attack needed";
            if (_searchSummary == null)
                _searchSummary = $"naturalEligibleSieges={jobs.Length}, navalEligibleSieges={_rejections["navalEligibleSieges"]}; no test attack needed";
            return true;
        }

        private static IEnumerable<Kingdom> EligibleRelievers(Kingdom owner, IFaction attacker) =>
            Kingdom.All.Where(kingdom => MilitaryObjective.AidBlocker(kingdom, owner, attacker) == null);

        private void ObserveCamp(SiegeEvent? siege)
        {
            if (ObserverOnly)
                return;
            if (siege?.BesiegerCamp is not { } camp || _result != null || _verdict != null ||
                !_guardSeen || _target == null || !Owns || _original == null ||
                Attacker.Fixture.Phase != MilitaryCommandBehavior.FixturePhase.Besieging ||
                Attacker.Fixture.Army != _original || siege.BesiegedSettlement != _target ||
                _original.LeaderParty.Army != _original || _original.Kingdom?.Armies.Contains(_original) != true ||
                _target.SiegeEvent != siege || _original.LeaderParty.BesiegerCamp != camp ||
                !camp.HasInvolvedPartyForEventType(_original.LeaderParty.Party))
                return;
            if (_original.AiBehaviorObject == _target && _original.LeaderParty.TargetSettlement == _target &&
                _original.LeaderParty.DefaultBehavior == AiBehavior.BesiegeSettlement)
            {
                if (!_handoff)
                    Write($"handoff originalArmy={_armyId} guard={_guard?.StringId} target={_target.StringId} order=BesiegeSettlement.");
                _handoff = true;
                if (!_campContact)
                    Write($"native contact army={_armyId} target={_target.StringId} campLeader={camp.LeaderParty?.StringId ?? "-"}.");
                _campContact = true;
            }
        }

        private void Search(double now)
        {
            var tried = new HashSet<string>((_triedPairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            string PairId(Settlement guard, Settlement target) => $"{guard.StringId}>{target.StringId}";
            var inventories = new Dictionary<Kingdom, KingdomForces>();
            var localLeaders = new Dictionary<Kingdom, MobileParty[]>();
            var proximity = new Dictionary<Settlement, float>();
            KingdomForces Inventory(Kingdom kingdom)
            {
                if (!inventories.TryGetValue(kingdom, out var inventory))
                    inventories.Add(kingdom, inventory = KingdomForces.Read(kingdom));
                return inventory;
            }
            float LeaderProximity(Kingdom kingdom, Settlement guard)
            {
                if (proximity.TryGetValue(guard, out var near))
                    return near;
                if (!localLeaders.TryGetValue(kingdom, out var leaders))
                {
                    leaders = Inventory(kingdom).Parties.Where(row => row.Army == null && row.Exclusion == null &&
                        ArmyOrders.FreePartyBlocker(row.Party, kingdom, asLeader: true) == null && ArmyOrders.VisitLock(row.Party) == null)
                        .Select(row => row.Party).ToArray();
                    localLeaders.Add(kingdom, leaders);
                }
                // Cheap ordering only; actual route, speed, roster and native
                // score decide admission. An in-fort leader ranks first.
                return proximity[guard] = leaders.Select(leader => leader.CurrentSettlement == guard ? -1f :
                    guard.GatePosition.DistanceSquared(KingdomCommander.Origin(leader).ToVec2()))
                    .DefaultIfEmpty(float.PositiveInfinity).Min();
            }
            var defenders = new Dictionary<Kingdom, MobileParty[]>();
            var triads = new Dictionary<(Kingdom owner, Kingdom attacker), Kingdom[]>();
            if (_alliedRelief)
            {
                var kingdoms = Kingdom.All.Where(kingdom => !kingdom.IsEliminated).ToArray();
                _rejections["formalAlliancePairs"] = kingdoms.Sum(owner => kingdoms.Count(kingdom => MilitaryObjective.FormallyAllied(kingdom, owner))) / 2;
                foreach (var owner in kingdoms)
                foreach (var attacker in kingdoms.Where(other => other != owner && owner.IsAtWarWith(other) && other.IsAtWarWith(owner)))
                {
                    var relievers = EligibleRelievers(owner, attacker).ToArray();
                    if (relievers.Length != 0)
                        triads.Add((owner, attacker), relievers);
                }
                _rejections["eligibleAllianceWarTriads"] = triads.Values.Sum(relievers => relievers.Length);
            }
            bool CanExerciseDefense(Settlement target, Kingdom attacker)
            {
                if (!ReliefMode)
                    return true;
                if (target.MapFaction is not Kingdom owner || owner.IsEliminated)
                    return false;
                if (_alliedRelief)
                    return triads.TryGetValue((owner, attacker), out var relievers) && relievers.Any(reliever => HasResponder(reliever, target));
                return HasResponder(owner, target);
            }
            bool HasResponder(Kingdom defending, Settlement target)
            {
                var inventory = Inventory(defending);
                if (_alliedRelief && inventory.Armies.Any(army => army.LeaderParty.Army == army &&
                    army.AiBehaviorObject == null && army.LeaderParty.TargetSettlement == null &&
                    army.LeaderParty.DefaultBehavior is AiBehavior.Hold or AiBehavior.None &&
                    ArmyTakeoverBehavior.Current?.IsPending(army) != true &&
                    ArmyOrders.CanRelease(army) && ArmyOrders.VisitLock(army.LeaderParty) == null &&
                    ArmyOrders.Members(army).All(party => ArmyOrders.CanDirect(party, defending)) &&
                    ArmyOrders.CanRouteToRelief(army.LeaderParty, KingdomCommander.Origin(army.LeaderParty), target.GatePosition)))
                    return true;
                if (!defenders.TryGetValue(defending, out var leaders))
                {
                    leaders = inventory.Parties.Where(row => row.Exclusion == null &&
                        ArmyOrders.FreePartyBlocker(row.Party, defending, true) == null)
                        .Select(row => row.Party).ToArray();
                    defenders.Add(defending, leaders);
                }
                if (leaders.Length == 0)
                    return false;
                return defending.Fiefs.Select(fief => fief.Settlement).Where(site => site != target &&
                    KingdomCommander.OpenFortification(site, defending))
                    .OrderBy(site => site.GatePosition.DistanceSquared(target.GatePosition.ToVec2()))
                    .Any(site => !double.IsInfinity(KingdomCommander.LandDistance(site.GatePosition, target.GatePosition, unrestricted: true)) &&
                        leaders.Any(leader => !double.IsInfinity(KingdomCommander.ReliefMusterHours(leader, site)) &&
                            ArmyOrders.CanRouteToRelief(leader, site.GatePosition, target.GatePosition)));
            }
            var pairs = (from kingdom in Kingdom.All
                         where !kingdom.IsEliminated && kingdom.FactionsAtWarWith.Count > 0
                         from town in kingdom.Fiefs
                         let guard = town.Settlement
                         where KingdomCommander.OpenFortification(guard, kingdom)
                         from target in town.GetNeighborFortifications(MobileParty.NavigationType.All)
                         where target.IsFortification && target.MapFaction is IFaction owner && kingdom.IsAtWarWith(owner) &&
                                !target.IsUnderSiege && target.Party.MapEvent == null
                         where !_alliedRelief || target.MapFaction is Kingdom host && triads.ContainsKey((host, kingdom))
                         where !tried.Contains(PairId(guard, target))
                         orderby LeaderProximity(kingdom, guard), guard.GatePosition.DistanceSquared(target.GatePosition.ToVec2()), target.IsCastle descending,
                             kingdom.StringId, guard.StringId, target.StringId
                         select (kingdom, guard, target)).ToArray();
            _rejections["borderPairsRemaining"] = pairs.Length;
            double remaining = _deadline - now;
            double retryReserve = _attempts + 1 < MaxAttempts ? Math.Min(48d, remaining * 0.20d) : 0d;
            double attemptBy = _deadline - retryReserve;
            (Kingdom kingdom, Settlement guard, Settlement target, KingdomCommander.Plan plan, double siegeEta,
                double setupBy, double setupAllowance, double estimate)? best = null;
            for (int i = 0; i < Math.Min(pairs.Length, BatchSize); ++i)
            {
                if (_cursor >= pairs.Length)
                    _cursor = 0;
                var (kingdom, guard, target) = pairs[_cursor++];
                Count("pairsChecked");
                if (!CanExerciseDefense(target, kingdom))
                {
                    Count("noDefenderMusterOrLeader");
                    continue;
                }
                double route = KingdomCommander.LandDistance(guard.GatePosition, target.GatePosition);
                if (double.IsInfinity(route))
                {
                    Count("siegeRoute");
                    continue;
                }
                var windows = new Dictionary<MobileParty, (double siegeEta, double setupBy, double rawSetupBy)>();
                var quotes = new Dictionary<(MobileParty, float), bool>();
                double LatestArrival(MobileParty leader)
                {
                    if (windows.TryGetValue(leader, out var previous))
                        return previous.rawSetupBy;
                    double siegeEta = KingdomCommander.TravelHours(route, leader.Speed);
                    double setupBy = attemptBy - GuardHours - SiegeAllowance(siegeEta);
                    double rawSetupBy = double.NegativeInfinity;
                    if (siegeEta > MaxQuotedSiegeHours || now >= setupBy)
                        Count("siegeTime");
                    else if (KingdomCommander.HigherPriorityDefense(kingdom, leader) != null)
                        Count("defensePriority");
                    else if (!ArmyOrders.CanPathLand(leader, guard.GatePosition, target.GatePosition))
                        Count("nativeSiegePath");
                    else
                        rawSetupBy = now + Math.Max(0d, (setupBy - now - TravelMargin) / SetupSlowdown);
                    windows.Add(leader, (siegeEta, setupBy, rawSetupBy));
                    return rawSetupBy;
                }
                bool StrongEnough(MobileParty leader, float power)
                {
                    if (!quotes.TryGetValue((leader, power), out bool feasible))
                    {
                        float score = KingdomCommander.SiegeScore(leader, target, power * 0.9f);
                        quotes.Add((leader, power), feasible = score > 0f && !float.IsInfinity(score));
                    }
                    return feasible;
                }
                var search = new KingdomCommander.FrontierSearch(Count, LatestArrival, StrongEnough);
                var plan = KingdomCommander.Preflight(kingdom, guard, 1f, now, attemptBy, null, search, Inventory(kingdom));
                if (plan == null)
                    continue;
                var (siege, setup, _) = windows[plan.Leader.Party];
                double setupAllowance = SetupAllowance(plan);
                double estimate = setupAllowance + GuardHours + SiegeAllowance(siege);
                if (best == null || estimate < best.Value.estimate ||
                    (estimate == best.Value.estimate && plan.Cost < best.Value.plan.Cost))
                    best = (kingdom, guard, target, plan, siege, setup, setupAllowance, estimate);
            }
            if (best is { } choice)
            {
                var (kingdom, guard, target, plan, siege, setup, setupAllowance, _) = choice;
                float required = (plan.Leader.Strength + plan.Calls.Sum(call => call.Strength)) * 0.9f;
                if (Attacker.TryStartAcceptance(_id!, kingdom, guard, target, required, setup - now, GuardHours, now, plan))
                {
                    if (_alliedRelief && target.MapFaction is Kingdom host && triads.TryGetValue((host, kingdom), out var relievers))
                        Write($"test setup triad: owner={host.StringId} attacker={kingdom.StringId} " +
                            $"formally-allied-and-at-war-relievers=[{string.Join(", ", relievers.Select(reliever => reliever.StringId))}].");
                    Write($"attempt={_attempts + 1} kingdom={kingdom.StringId} muster={guard.StringId} guard={guard.StringId} " +
                        $"target={target.StringId} leader={plan.Leader.Party.StringId} required={CampaignLog.Number(required)} " +
                        $"readyETA={CampaignLog.Number(plan.ArrivalHours)} setupAllowance={CampaignLog.Number(setupAllowance)} " +
                        $"siegeETA={CampaignLog.Number(siege)} siegeAllowance={CampaignLog.Number(SiegeAllowance(siege))} " +
                        $"setupDeadline={CampaignLog.Number(setup)} attemptDeadline={CampaignLog.Number(attemptBy)} " +
                        $"overallRemaining={CampaignLog.Number(remaining)} retryReserve={CampaignLog.Number(retryReserve)} " +
                        $"calls=[{string.Join(", ", plan.Calls.Select(c => $"{c.Party.StringId}:{c.Cost}"))}].");
                    _triedPairs += PairId(guard, target) + ";";
                    _cursor = 0; // rank fresh pairs after an actual attempt
                    _attempts++;
                    _lastAttempt = null;
                    _guard = guard;
                    _target = target;
                    _original = Attacker.Fixture.Army;
                    _armyId = _original == null ? null : CampaignLog.ArmyId(_original);
                    _guardSeen = _handoff = _campContact = false;
                    _attemptDeadline = attemptBy;
                }
                else
                    Count("startRecheck");
            }
            _searchSummary = string.Join(", ", _rejections.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            if (now >= _nextDiagnostic)
            {
                Write($"search (cumulative checks, not unique parties; borderPairsRemaining=current): {_searchSummary}.");
                _nextDiagnostic = now + 24d;
            }
        }

        private static double SetupAllowance(KingdomCommander.Plan plan) =>
            plan.Leader.Party.CurrentSettlement == plan.Muster && plan.Calls.All(call => call.Party.CurrentSettlement == plan.Muster) ?
                0d : SetupSlowdown * plan.ArrivalHours + TravelMargin;
        private static double SiegeAllowance(double hours) => SiegeSlowdown * hours + TravelMargin;

        private void Count(string reason) => _rejections[reason] = _rejections.TryGetValue(reason, out int count) ? count + 1 : 1;

        private void End(string verdict, long wall)
        {
            _command.TrackCoordinationContests = false;
            _verdict = verdict;
            _cleanupDeadline = wall + TimeSpan.FromSeconds(30).Ticks;
            Settle(wall);
        }

        private void Settle(long wall)
        {
            if (_result != null)
                return;
            bool pending = !ObserverOnly && Attacker.CancelAcceptance(_id!);
            if (pending && wall < _cleanupDeadline)
                return;
            _terminalCleanupRequested = true;
            _result = $"Run {_id}: {_verdict}. " + (ObserverOnly ? "Observer only; production orders left intact." :
                $"attempts={_attempts}; cleanup={(pending ? "deferred to native event safety" : "no owned assignment left")}.") +
                $" Search: {_searchSummary ?? (_searchWait ?? "not reached")}.";
            SaveReceipt("result", _result);
            _log.Event($"Acceptance final: {_result}");
        }

        private void Write(string line) => _log.Event($"Acceptance run={_id} {line}");
    }
}
