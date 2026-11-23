using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior : CampaignBehaviorBase
    {
        private const int ReportIntervalHours = 24;
        private readonly CampaignLog _log;
        private readonly bool _fixtureOnly;
        private bool _ready;
        private double? _nextReportHour;
        private bool _reviewRequested;
        private bool _resume;
        private Request? _requested;
        private Army? _army;
        private string? _kingdomId;
        private Settlement? _muster;
        private Settlement? _target;
        private float _required;
        private double _deadline;
        private string? _outcome;
        private int _phase;
        private List<MobileParty> _surplus = new();
        private double _guardHours;
        private double _guardUntil;
        private Settlement? _followOn;
        private bool _campObserved;
        private double _upkeepAfter;
        private string? _lastUpkeep;
        private bool _cancelFollowOn;
        private string? _acceptanceOwner;
        private int _visitRetries;
        private double _visitProgressAt, _visitRetryAfter, _visitBestRoute;
        private const int MaxVisitRetries = 2;
        private const double VisitRetryDelay = 1d, VisitStallHours = 6d, VisitProgressDistance = 0.25d;

        private const int Gathering = 1, Marching = 2, Closing = 3, Finished = 4, Guarding = 5, Besieging = 6;

        internal enum FixturePhase { Idle, Gathering, Marching, Closing, Finished, Guarding, Besieging }
        internal (FixturePhase Phase, Army? Army, Settlement? Target, string? Outcome, string? Owner) Fixture =>
            ((FixturePhase)_phase, _army, _target, _outcome, _acceptanceOwner);
        internal bool FixtureReady => _ready && (_fixtureOnly || _nextReportHour.HasValue) && ArmyTakeoverBehavior.Current?.InitialCheckComplete == true;
        internal bool FixtureBusy => _requested != null || _phase is not (0 or Finished);

        internal bool TryStartAcceptance(string owner, Kingdom kingdom, Settlement guard, Settlement siege,
            float required, double setupHours, double guardHours, double now, KingdomCommander.Plan plan)
        {
            if (!FixtureReady || FixtureBusy || plan.Muster != guard || !KingdomCommander.OpenFortification(guard, kingdom))
                return false;
            Begin(new Request(kingdom, guard, required, setupHours, guardHours, siege), now, plan, owner);
            return _army != null;
        }

        internal bool CancelAcceptance(string owner)
        {
            if (owner != _acceptanceOwner || !FixtureBusy)
                return false;
            // A battle (or loss of eligibility) can already have moved the job
            // to Closing while the owned siege is still intact.
            _cancelFollowOn |= _phase == Besieging ||
                (_phase == Closing && _army?.LeaderParty.BesiegedSettlement == _target && _target != null);
            if (_phase != Closing)
                Close("acceptance-cleanup");
            _reviewRequested = true;
            return true;
        }

        internal bool ClearIdleManualFixture()
        {
            if (_acceptanceOwner != null || _army == null || _phase is not (Gathering or Marching or Guarding) ||
                !ArmyOrders.CanRelease(_army))
                return false;
            Close("superseded-by-armed-acceptance");
            _reviewRequested = true;
            return true;
        }

        private sealed class Request
        {
            internal readonly Kingdom Kingdom;
            internal readonly Settlement Target;
            internal readonly float Required;
            internal readonly double Hours;
            internal readonly double Guard;
            internal readonly Settlement? FollowOn;
            internal Request(Kingdom kingdom, Settlement target, float required, double hours, double guard, Settlement? followOn) =>
                (Kingdom, Target, Required, Hours, Guard, FollowOn) = (kingdom, target, required, hours, guard, followOn);
        }

        internal MilitaryCommandBehavior(CampaignLog log) : this(log, fixtureOnly: false) { }
        internal MilitaryCommandBehavior(CampaignLog log, bool fixtureOnly) :
            base(fixtureOnly ? "BannerlordMilitaryAIOverhaul.ControlledAcceptanceAttack" : "BannerlordMilitaryAIOverhaul.MilitaryCommand") =>
            (_log, _fixtureOnly) = (log, fixtureOnly);

        internal static bool Owns(Army army) =>
            ArmyTakeoverBehavior.Current?.InitialCheckComplete == true &&
            NativeCampaign.Current?.GetCampaignBehavior<MilitaryCommandBehavior>() is { } command &&
            (command._relief.ContainsKey(army) || command._sieges.ContainsKey(army) || command._idle.ContainsKey(army) || OwnsControlledFixture(army));

        private static bool OwnsControlledFixture(Army army) =>
            NativeCampaign.Current?.GetCampaignBehaviors<MilitaryCommandBehavior>().Any(command => command._army == army) == true;

        public override void RegisterEvents()
        {
            CampaignEvents.OnAfterSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            if (!_fixtureOnly)
                CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.QuarterHourlyTickEvent.AddNonSerializedListener(this, RequestReview);
            CampaignEvents.PartyAttachedAnotherParty.AddNonSerializedListener(this, OnAttached);
            CampaignEvents.AfterSettlementEntered.AddNonSerializedListener(this, OnEntered);
            CampaignEvents.OnPartyLeftArmyEvent.AddNonSerializedListener(this, OnLeft);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnBattleEnded);
            CampaignEvents.ArmyDispersed.AddNonSerializedListener(this, OnDispersed);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, OnSiegeStarted);
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, _ => RequestReview());
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, (_, _, _, _, _, _) => RequestReview());
            CampaignEvents.OnPartyJoinedArmyEvent.AddNonSerializedListener(this, party =>
            {
                if (_army != null && party.Army == _army)
                    RequestReview();
            });
            if (!_fixtureOnly)
            {
                RegisterReliefEvents();
                RegisterSiegeEvents();
                if (CampaignPerformanceCapture.Active != null)
                {
                    CampaignEvents.OnSaveStartedEvent.AddNonSerializedListener(this, CampaignPerformanceCapture.SaveBoundary);
                    CampaignEvents.OnSaveOverEvent.AddNonSerializedListener(this, (_, _) => CampaignPerformanceCapture.SaveBoundary());
                }
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            if (!_fixtureOnly)
            {
                SyncRelief(dataStore);
                SyncSieges(dataStore);
                SyncIdle(dataStore);
            }
            if (dataStore.IsSaving && _phase == 0)
                return;
            dataStore.SyncData("fixturePhase", ref _phase);
            dataStore.SyncData("fixtureArmy", ref _army);
            dataStore.SyncData("fixtureKingdom", ref _kingdomId);
            dataStore.SyncData("fixtureMuster", ref _muster);
            dataStore.SyncData("fixtureTarget", ref _target);
            dataStore.SyncData("fixtureRequired", ref _required);
            dataStore.SyncData("fixtureDeadline", ref _deadline);
            dataStore.SyncData("fixtureOutcome", ref _outcome);
            // Exact native party references retain the decision even if a
            // traveler attaches later. Ordinary Stage 2 saves have no such key.
            if (dataStore.IsLoading || _surplus.Count != 0)
            {
                var surplus = dataStore.IsSaving ? _surplus.ToList() : _surplus;
                dataStore.SyncData("fixtureSurplusReleases", ref surplus);
                if (dataStore.IsLoading)
                    _surplus = surplus ?? new List<MobileParty>();
            }
            if (dataStore.IsLoading || _guardHours > 0)
            {
                dataStore.SyncData("fixtureGuardHours", ref _guardHours);
                dataStore.SyncData("fixtureGuardUntil", ref _guardUntil);
                dataStore.SyncData("fixtureFollowOn", ref _followOn);
                dataStore.SyncData("fixtureCampObserved", ref _campObserved);
            }
            if (dataStore.IsLoading || _upkeepAfter > 0)
            {
                dataStore.SyncData("fixtureUpkeepAfter", ref _upkeepAfter);
                dataStore.SyncData("fixtureLastUpkeep", ref _lastUpkeep);
            }
            if (dataStore.IsLoading || _cancelFollowOn)
                dataStore.SyncData("fixtureCancelFollowOn", ref _cancelFollowOn);
            if (dataStore.IsLoading || _acceptanceOwner != null)
                dataStore.SyncData("fixtureAcceptanceOwner", ref _acceptanceOwner);
            if (dataStore.IsLoading || _phase is Gathering or Marching)
            {
                dataStore.SyncData("fixtureVisitRetries", ref _visitRetries);
                dataStore.SyncData("fixtureVisitProgressAt", ref _visitProgressAt);
                dataStore.SyncData("fixtureVisitRetryAfter", ref _visitRetryAfter);
                dataStore.SyncData("fixtureVisitBestRoute", ref _visitBestRoute);
            }
            if (dataStore.IsLoading)
                _resume = _phase != 0;
            else
                _log.Event($"Muster save: {Status()}.");
        }

        private void OnSessionLaunched(CampaignGameStarter starter) => _ready = true;

        private void RequestReview() => _reviewRequested |= _army != null;
        private void OnBattleEnded(MapEvent battle) => RequestReview();
        private void OnSiegeStarted(SiegeEvent? siege)
        {
            if (siege != null && _phase == Besieging && _army?.LeaderParty.SiegeEvent == siege && siege.BesiegedSettlement == _target)
                _campObserved = true;
            RequestReview();
        }
        private void OnDispersed(Army army, Army.ArmyDispersionReason reason, bool playerInvolved)
        {
            if (ArmyOrders.Releasing != army)
                EndIdle(army, "native-dispersal:" + reason);
            if (_army != army || ArmyOrders.Releasing == army)
                return;
            _outcome = $"native-dispersal({reason})";
            _log.Event($"Assignment native dispersal: {Status()} cohesion={CampaignLog.Number(army.Cohesion)} " +
                $"lastUpkeep={_lastUpkeep ?? "-"} members=[{string.Join(", ", ArmyOrders.Members(army).Select(p => p.StringId))}].");
            RequestReview();
        }
        private void OnAttached(MobileParty party)
        {
            if (_army != null && party.Army == _army)
            {
                _log.Event($"Muster attachment: army={CampaignLog.ArmyId(_army)} party={party.StringId} nativeAttached={party.AttachedTo == _army.LeaderParty}.");
                RequestReview();
            }
        }
        private void OnEntered(MobileParty? party, Settlement settlement, Hero? hero)
        {
            if (_army != null && party == _army.LeaderParty)
            {
                _log.Event($"Muster settlement entry: {CampaignLog.ArmyState(_army)} settlement={settlement.StringId} " +
                    $"observed={party.CurrentSettlement == settlement} nativeTarget={party.TargetSettlement?.StringId ?? "-"}.");
                RequestReview();
            }
        }
        private void OnLeft(MobileParty party, Army army)
        {
            if (_army == army)
            {
                _log.Event($"Muster native leave: army={CampaignLog.ArmyId(army)} party={party.StringId} stillMember={party.Army == army}.");
                RequestReview();
            }
        }

        private void OnTick(float dt)
        {
            // The first world tick is after native load and the takeover's first check.
            if (!_fixtureOnly && _ready && !_nextReportHour.HasValue)
                Report("initial", CampaignTime.Now.ToHours);
            if (!_ready || ArmyTakeoverBehavior.Current?.InitialCheckComplete != true)
                return;
            var now = CampaignTime.Now.ToHours;
            if (_resume)
            {
                _resume = false;
                _log.Event($"Muster loaded: {Status()}; no calls or payments replayed.");
                _reviewRequested = true;
            }
            if (_requested is Request request)
            {
                _requested = null;
                Begin(request, now);
            }
            if (_reviewRequested)
            {
                _reviewRequested = false;
                Review(now);
            }
            if (!_fixtureOnly)
            {
                TickRelief(now);
                TickSieges(now);
            }
        }

        private void OnHourlyTick()
        {
            if (_nextReportHour is double due && CampaignTime.Now.ToHours >= due)
                Report("refresh", CampaignTime.Now.ToHours);
        }

        private void Report(string trigger, double hour)
        {
            _nextReportHour = hour + ReportIntervalHours;
            _log.Events(ReportLines(trigger));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("muster", "maio")]
        private static string MusterCommand(List<string> args)
        {
            var behavior = NativeCampaign.Current?.GetCampaignBehavior<MilitaryCommandBehavior>();
            if (behavior == null || !behavior._ready || !behavior._nextReportHour.HasValue ||
                ArmyTakeoverBehavior.Current?.InitialCheckComplete != true)
                return "Load a campaign with MAIO enabled and let the campaign map tick before running this fixture.";
            if (Game.Current?.GameStateManager.ActiveState is not MapState { AtMenu: false })
                return "Run this fixture from the campaign map, outside menus and battles.";
            return behavior.Control(args.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToArray());
        }

        private string Control(string[] args)
        {
            const string usage = "maio.muster status | cancel | neighbors <fort-id> | preview/start <kingdom-id> <friendly-fort-id> <strength> <march-deadline-hours> [<guard-hours> [<enemy-follow-on-id>]]";
            if (args.Length == 1 && args[0] == "status")
                return Status();
            if (args.Length == 2 && args[0] == "neighbors")
            {
                var origin = Settlement.All.FirstOrDefault(site => site.StringId == args[1] && site.IsFortification);
                return origin == null ? "Specify a town or castle ID." :
                    $"Immediate neighbors of {origin.StringId}: " +
                    string.Join(", ", origin.Town.GetNeighborFortifications(MobileParty.NavigationType.All)
                        .Select(site => $"{site.StringId} ({site.MapFaction?.StringId ?? "-"})"));
            }
            if (args.Length == 1 && args[0] == "cancel")
            {
                if (_requested != null)
                {
                    _requested = null;
                    _log.Event("Muster cancelled: explicit console cancel before preflight; no order or payment.");
                    return "Queued request removed.";
                }
                if (_phase is not (Gathering or Marching or Guarding or Besieging))
                    return Status();
                _cancelFollowOn = _phase == Besieging;
                Close("cancelled-by-player");
                _reviewRequested = true;
                return "Cancellation recorded; safe release is checked on the next campaign tick.";
            }
            if (args.Length < 5 || args.Length > 7 || (args[0] != "preview" && args[0] != "start") ||
                !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float required) ||
                !(required > 0) || float.IsInfinity(required) ||
                !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double hours) ||
                !(hours > 0 && hours <= float.MaxValue))
                return usage;
            double guard = 0;
            if (args.Length >= 6 && (!double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out guard) ||
                !(guard > 0 && guard <= float.MaxValue)))
                return usage;
            var kingdom = Kingdom.All.FirstOrDefault(item => item.StringId == args[1] && !item.IsEliminated);
            var target = kingdom?.Fiefs.Select(fief => fief.Settlement).FirstOrDefault(site => site.StringId == args[2]);
            if (kingdom == null || target == null || !KingdomCommander.OpenFortification(target, kingdom))
                return "Specify a current kingdom ID and one of its unbesieged towns or castles (string IDs).";
            var followOn = args.Length == 7 ? Settlement.All.FirstOrDefault(site => site.StringId == args[6] && site.IsFortification) : null;
            if (args.Length == 7 && followOn == null)
                return "Specify an existing town or castle ID for the optional follow-on. War, adjacency, route and strength are checked again on handoff.";
            var request = new Request(kingdom, target, required, hours, guard, followOn);
            if (args[0] == "preview")
            {
                double now = CampaignTime.Now.ToHours;
                var plan = Plan(request, now, "preview");
                return plan == null ? "No feasible muster. See Desktop log; no order or payment." :
                    $"Feasible: muster={plan.Muster.StringId} leader={plan.Leader.Party.StringId} calls={plan.Calls.Length} " +
                    $"cost={plan.Calls.Sum(row => row.Cost)} latestDeparture={CampaignLog.Number(now + hours - plan.MarchHours)}. See Desktop log.";
            }
            if (_requested != null || _phase is Gathering or Marching or Closing or Guarding or Besieging)
                return $"One fixture at a time. {Status()}";
            _requested = request;
            return $"Queued one fixture: kingdom={kingdom.StringId} target={target.StringId} required={CampaignLog.Number(required)} " +
                $"deadline={CampaignLog.Number(hours)} hours after first campaign tick. Unpause to run.";
        }

        private KingdomCommander.Plan? Plan(Request request, double now, string trigger)
        {
            _log.Event($"Muster request trigger={trigger} kingdom={request.Kingdom.StringId} target={request.Target.StringId} " +
                $"required={CampaignLog.Number(request.Required)} deadline={CampaignLog.Number(now + request.Hours)} objective=friendly-land-march " +
                $"guardHours={CampaignLog.Number(request.Guard)} followOn={request.FollowOn?.StringId ?? "-"}.");
            return KingdomCommander.OpenFortification(request.Target, request.Kingdom)
                ? KingdomCommander.Preflight(request.Kingdom, request.Target, request.Required, now, now + request.Hours, _log) : null;
        }

        private void Begin(Request request, double now, KingdomCommander.Plan? acceptedPlan = null, string? owner = null)
        {
            _muster = null;
            _kingdomId = request.Kingdom.StringId;
            _target = request.Target;
            _required = request.Required;
            _deadline = now + request.Hours;
            _outcome = null;
            _guardHours = request.Guard;
            _guardUntil = 0;
            _followOn = request.FollowOn;
            _campObserved = false;
            _cancelFollowOn = false;
            _upkeepAfter = 0;
            _lastUpkeep = null;
            _acceptanceOwner = owner;
            ResetVisit(now);
            var plan = acceptedPlan ?? Plan(request, now, "start");
            if (plan == null)
            {
                Finish("no-feasible-plan; no army, summons or payment");
                return;
            }
            _muster = plan.Muster;
            if (ArmyOrders.FreePartyBlocker(plan.Leader.Party, request.Kingdom, asLeader: true) is string veto)
            {
                Finish($"leader-recheck-failed({veto}); no army, summons or payment");
                return;
            }
            if (ArmyOrders.VisitLock(plan.Leader.Party) is string visitLock)
            {
                Finish($"leader-recheck-failed({visitLock}); no army, summons or payment");
                return;
            }
            if (!ArmyOrders.CanPathVisit(plan.Leader.Party, plan.Muster))
            {
                Finish($"native-muster-path-unreachable({plan.Muster.StringId}); no army, summons or payment");
                return;
            }
            _phase = Gathering;
            _army = ArmyOrders.Create(request.Kingdom, plan.Leader.Party, plan.Muster, _log, out bool formed);
            if (!formed)
                Close("native-formation-failed");
            else
            {
                foreach (var call in plan.Calls)
                    if (!KingdomCommander.WithinCallRange(plan.Leader.Party, call.Party) ||
                        now + KingdomCommander.HoursTo(call.Party, plan.Muster) > _deadline - plan.MarchHours ||
                        !ArmyOrders.Summon(_army, call.Party, _log))
                    {
                        Close($"summons-recheck-failed({call.Party.StringId})");
                        break;
                    }
            }
            _reviewRequested = true;
        }

        private void Review(double now)
        {
            if (_phase == 0 || _phase == Finished)
                return;
            var army = _army;
            if (army == null)
            {
                Finish("army-no-longer-registered; no repeated calls or release");
                return;
            }
            var kingdom = army.Kingdom;
            bool registered = kingdom?.Armies.Contains(army) == true && army.LeaderParty.Army == army;
            // A native transition or player/event lock must not detach even a
            // cancelled escort from this army while movement is protected.
            var leader = army.LeaderParty;
            if (!registered || ArmyOrders.ReleaseBlocker(leader) == null)
                ReconcileSurplus(army, registered);
            if (!registered || kingdom == null)
            {
                // Native dispersal is already complete. Only a stale escort
                // from our old summons may still need safe reconciliation.
                if (_surplus.Count == 0)
                    Finish(_outcome ?? "army-no-longer-registered; no repeated calls or release");
                else if (_phase != Closing)
                    Close("army-no-longer-registered; waiting to clear cancelled escort");
                return;
            }
            var members = ArmyOrders.Members(army);
            if (_phase != Closing)
            {
                if (_phase is Guarding or Besieging)
                    ReviewSustained(army, kingdom, members, now);
                else
                    ReviewAssignment(army, kingdom, leader, members, now);
            }
            if (_phase != Closing)
                return;
            if (_cancelFollowOn && _target != null &&
                ArmyOrders.WithdrawCancelledSiege(army, _target, _log) is string withdrawalBlocker)
            {
                if (_log.Changed("siege-cancel", CampaignLog.ArmyId(army), withdrawalBlocker))
                    _log.Event($"Assignment withdrawal deferred: {Status()} blocker={withdrawalBlocker}; no upkeep or replacement order.");
                return;
            }
            if (ArmyOrders.Release(army, _log, _outcome ?? "closed"))
            {
                if (_surplus.Count == 0)
                    Finish(_outcome ?? "closed");
            }
            else
            {
                string blockers = string.Join(", ", members.Select(p => (party: p, blocker: ArmyOrders.ReleaseBlocker(p)))
                    .Where(item => item.blocker != null).Select(item => $"{item.party.StringId}:{item.blocker}"));
                if (_log.Changed("muster-release", CampaignLog.ArmyId(army), blockers))
                    _log.Event($"Muster release deferred: {Status()} blockers=[{blockers}]; leaving native events and the main party alone.");
            }
        }

        private void ReviewAssignment(Army army, Kingdom kingdom, MobileParty leader, MobileParty[] members, double now)
        {
            var expected = _phase == Gathering ? _muster! : _target!;
            var muster = _muster!;
            bool frontier = _phase == Gathering && _acceptanceOwner != null && _muster == _target;
            if (kingdom.StringId != _kingdomId || !KingdomCommander.OpenFortification(_target, kingdom) ||
                (_phase == Gathering && !KingdomCommander.OpenFortification(_muster, kingdom)))
            {
                CloseVisit(army, expected, now, "target-or-muster-lost-or-besieged");
                return;
            }
            if (members.Any(party => party.IsMainParty) || !ArmyOrders.CanDirect(leader, kingdom))
            {
                CloseVisit(army, expected, now, "player-joined-or-leader-ineligible");
                return;
            }
            bool arrived = _phase == Marching && leader.CurrentSettlement == _target;
            if (now > _deadline && !arrived)
            {
                CloseVisit(army, expected, now, "deadline-missed");
                return;
            }
            var blockers = members.Where(p => (p == leader || p.AttachedTo == leader) &&
                ArmyOrders.ReleaseBlocker(p) is "native-event" or "player" or "map-transition" or "native-movement-lock").ToArray();
            if (blockers.Length != 0)
            {
                if (_phase == Gathering)
                {
                    double remainingMarch = frontier ? 0d : KingdomCommander.TravelHours(
                        KingdomCommander.LandDistance(_muster!.GatePosition, _target!.GatePosition), leader.Speed);
                    if (now >= _deadline - remainingMarch)
                    {
                        CloseVisit(army, expected, now, $"native-event-blocked-latest-departure({CampaignLog.Number(_deadline - remainingMarch)})");
                        return;
                    }
                }
                string blocked = string.Join(", ", blockers.Select(p => $"{p.StringId}:{ArmyOrders.ReleaseBlocker(p)}"));
                if (_log.Changed("muster-tactical", CampaignLog.ArmyId(army), blocked))
                    _log.Event($"Muster decision held for native event: {VisitState(army, expected, now)} parties=[{blocked}].");
                PauseVisit(now);
                return;
            }
            _log.Changed("muster-tactical", CampaignLog.ArmyId(army), null);
            bool ordered = leader.DefaultBehavior == AiBehavior.GoToSettlement && leader.TargetSettlement == expected &&
                leader.TargetParty == null && !leader.IsTargetingPort && leader.DesiredAiNavigationType == MobileParty.NavigationType.Default;
            bool held = leader.DefaultBehavior == AiBehavior.Hold && leader.TargetSettlement == null && leader.TargetParty == null &&
                !leader.IsTargetingPort && leader.DesiredAiNavigationType is MobileParty.NavigationType.None or MobileParty.NavigationType.Default;
            if (army.AiBehaviorObject != expected || (!ordered && !held))
            {
                CloseVisit(army, expected, now, "commander-move-no-longer-active");
                return;
            }
            if (ArmyOrders.VisitLock(leader, held) is string lockReason)
            {
                if (lockReason is "native-movement-lock" or "map-transition")
                {
                    if (_log.Changed("visit-lock", CampaignLog.ArmyId(army), lockReason))
                        _log.Event($"Muster visit deferred: {VisitState(army, expected, now)} reason={lockReason}.");
                    PauseVisit(now);
                }
                else
                    CloseVisit(army, expected, now, lockReason);
                return;
            }
            _log.Changed("visit-lock", CampaignLog.ArmyId(army), null);
            if (_phase == Gathering && leader.CurrentSettlement == _muster)
                foreach (var member in members)
                    ArmyOrders.MergeAtMuster(army, member, _muster!);
            var rows = KingdomForces.Read(kingdom).Parties.Where(row => row.Army == army && !_surplus.Contains(row.Party)).ToArray();
            float present = rows.Where(row => row.IsPresent).Sum(row => row.AssignableStrength);
            bool presentIneligible = rows.Any(row => row.IsPresent && row.Exclusion != null && row.Exclusion != "no-fighting-strength");
            if (arrived)
            {
                ReviewArrival(army, kingdom, members, now, present, presentIneligible);
                return;
            }
            if (presentIneligible)
            {
                CloseVisit(army, expected, now, "present-party-became-ineligible");
                return;
            }
            double route = KingdomCommander.DistanceTo(leader, expected);
            double hours = KingdomCommander.TravelHours(route, leader.Speed);
            if (double.IsPositiveInfinity(route))
            {
                CloseVisit(army, expected, now, "commander-visit-no-land-route", hours);
                return;
            }
            if (_phase == Marching)
            {
                if (present < _required || now + hours > _deadline)
                {
                    CloseVisit(army, expected, now, $"march-no-longer-feasible(present={CampaignLog.Number(present)})", hours);
                }
                else if (MaintainVisit(army, expected, now, held, hours, route))
                    Sustain(army, now);
                return;
            }
            var traveling = rows.Where(row => !row.IsPresent).ToArray();
            double march = frontier ? 0d : KingdomCommander.TravelHours(
                KingdomCommander.LandDistance(_muster!.GatePosition, _target!.GatePosition), leader.Speed);
            double leaderHours = hours;
            var arrivals = traveling.Where(row => row.Exclusion == null).Select(row =>
                (row, hours: KingdomCommander.HoursTo(row.Party, muster))).ToArray();
            var decision = KingdomCommander.CheckMuster(_required, present, leader.CurrentSettlement == _muster,
                now, _deadline, march, leaderHours, arrivals.Select(item => (item.row.Strength, item.hours)));
            string shape = $"{decision}:{present:R}:{string.Join(",", arrivals.Select(item => $"{item.row.Party.StringId}:{now + item.hours <= _deadline - march}"))}:{leader.CurrentSettlement == _muster}";
            if (_log.Changed("muster-decision", CampaignLog.ArmyId(army), shape))
                _log.Event($"Muster review decision={(frontier && decision == KingdomCommander.MusterDecision.Depart ? "ReadyToGuard" : decision.ToString())}: " +
                    $"{Status()} present={CampaignLog.Number(present)} " +
                    $"summoned={CampaignLog.Number(arrivals.Sum(item => item.row.Strength))} leaderETA={CampaignLog.Number(now + leaderHours)} " +
                    $"marchHours={CampaignLog.Number(march)} latestDeparture={CampaignLog.Number(_deadline - march)} " +
                    $"traveling=[{string.Join(", ", arrivals.Select(item => $"{item.row.Party.StringId}:eta={CampaignLog.Number(now + item.hours)}"))}].");
            if (decision == KingdomCommander.MusterDecision.Cancel)
                CloseVisit(army, expected, now, "no-feasible-muster-by-latest-departure", leaderHours);
            else if (decision == KingdomCommander.MusterDecision.Depart)
            {
                if (!frontier && !ArmyOrders.CanPathVisit(leader, _target!))
                {
                    CloseVisit(army, _target!, now, "native-march-path-unreachable", march);
                    return;
                }
                _surplus.AddRange(members.Where(p => p.Army == army && p != leader && p.AttachedTo != leader));
                ReconcileSurplus(army, registered: true);
                if (frontier)
                {
                    // CheckMuster required actual CurrentSettlement and present
                    // strength. No same-fort move or second arrival event needed.
                    ReviewArrival(army, kingdom, ArmyOrders.Members(army), now, present, presentIneligible);
                    return;
                }
                ArmyOrders.Visit(army, _target!);
                _phase = Marching;
                ResetVisit(now);
                _log.Event($"Muster departure: {Status()} present={CampaignLog.Number(present)} " +
                    $"nativeOrder={leader.DefaultBehavior} nativeTarget={leader.TargetSettlement?.StringId ?? "-"} " +
                    $"at={leader.CurrentSettlement?.StringId ?? "-"} deferredSurplus={_surplus.Count}.");
            }
            else
            {
                foreach (var row in traveling.Where(row => row.Exclusion != null))
                    ArmyOrders.ReleaseMember(army, row.Party, _log, row.Exclusion!);
                if (leader.CurrentSettlement == expected || MaintainVisit(army, expected, now, held, leaderHours, route))
                    Sustain(army, now);
            }
        }

        private void ReviewArrival(Army army, Kingdom kingdom, MobileParty[] members, double now, float present, bool presentIneligible)
        {
            _log.Event($"Muster arrival: {Status()} present={CampaignLog.Number(present)} " +
                $"actualTarget={army.LeaderParty.CurrentSettlement?.StringId ?? "-"} inTime={now <= _deadline}.");
            if (_guardHours > 0 && presentIneligible)
                CloseVisit(army, _target!, now, "present-party-became-ineligible", 0d);
            else if (now <= _deadline && present >= _required && _guardHours > 0)
            {
                _guardUntil = now + _guardHours;
                ArmyOrders.Defend(army, _target!);
                _phase = Guarding;
                _log.Event($"Assignment guard issued: {Status()} {CampaignLog.ArmyState(army)} present={CampaignLog.Number(present)}.");
                ReviewSustained(army, kingdom, members, now);
            }
            else
                Close(now > _deadline ? "arrived-late" : present < _required ? "arrived-understrength" : "arrived-by-deadline");
        }

        private void ResetVisit(double now)
        {
            _visitRetries = 0;
            _visitRetryAfter = 0;
            PauseVisit(now);
        }

        private void PauseVisit(double now)
        {
            _visitProgressAt = now;
            _visitBestRoute = -1d; // sample a fresh route on the next safe review
        }

        private bool MaintainVisit(Army army, Settlement target, double now, bool held, double eta, double route)
        {
            if (_visitProgressAt <= 0 || _visitBestRoute < 0 || route < _visitBestRoute - VisitProgressDistance)
            {
                _visitBestRoute = route;
                _visitProgressAt = now;
            }
            if (now - _visitProgressAt >= VisitStallHours)
            {
                CloseVisit(army, target, now, ArmyOrders.CanPathVisit(army.LeaderParty, target) ?
                    "commander-visit-no-route-progress" : "native-visit-path-unreachable", eta);
                return false;
            }
            if (!held)
                return true;
            if (now < _visitRetryAfter)
                return false;
            if (_visitRetries >= MaxVisitRetries || !ArmyOrders.CanPathVisit(army.LeaderParty, target))
            {
                CloseVisit(army, target, now, _visitRetries >= MaxVisitRetries ?
                    "commander-visit-retry-exhausted" : "native-visit-path-unreachable", eta);
                return false;
            }
            _log.Event($"Muster visit recovery before: {VisitState(army, target, now, eta)} nativePath=reachable reissue={_visitRetries + 1}.");
            ++_visitRetries;
            _visitRetryAfter = now + VisitRetryDelay;
            ArmyOrders.Visit(army, target);
            _log.Event($"Muster visit recovery after: {VisitState(army, target, now, eta)}.");
            return false; // observe the restored native order next review before upkeep
        }

        private void CloseVisit(Army army, Settlement target, double now, string reason, double? eta = null)
        {
            _log.Event($"Muster visit before cancellation: {VisitState(army, target, now, eta)} reason={reason}.");
            Close(reason, visitLogged: true);
        }

        private string VisitState(Army army, Settlement target, double now, double? eta = null)
        {
            var party = army.LeaderParty;
            return $"{Status()} expected=GoToSettlement:{target.StringId} actual={party.DefaultBehavior}:{party.TargetSettlement?.StringId ?? party.TargetParty?.StringId ?? "-"} " +
                $"shortTerm={party.ShortTermBehavior}:{party.ShortTermTargetSettlement?.StringId ?? party.ShortTermTargetParty?.StringId ?? "-"} " +
                $"armyObjective={CampaignLog.PointId(army.AiBehaviorObject)} position={Position(party.Position)} at={party.CurrentSettlement?.StringId ?? "-"} " +
                $"gate={Position(target.GatePosition)} moving={party.IsMoving} moveMode={party.PartyMoveMode} " +
                $"navigation={party.DesiredAiNavigationType} port={party.IsTargetingPort} " +
                $"transition={party.IsTransitionInProgress || party.StartTransitionNextFrameToExitFromPort} " +
                $"battle={party.MapEvent?.Component?.GetType().Name ?? (party.MapEvent != null ? "active" : "-")} " +
                $"siege={party.SiegeEvent?.BesiegedSettlement?.StringId ?? "-"} " +
                $"targetOwner={target.MapFaction?.StringId ?? "-"} targetBesieged={target.IsUnderSiege} " +
                $"etaHours={(eta.HasValue ? CampaignLog.Number(eta.Value) : "-")} timeLeft={CampaignLog.Number(_deadline - now)} " +
                $"bestRoute={(_visitBestRoute >= 0 && _visitProgressAt > 0 ? CampaignLog.Number(_visitBestRoute) : "-")} " +
                $"withoutProgressHours={(_visitProgressAt > 0 ? CampaignLog.Number(Math.Max(0d, now - _visitProgressAt)) : "-")} " +
                $"retries={_visitRetries}/{MaxVisitRetries} retryAfter={CampaignLog.Number(_visitRetryAfter)}";
        }

        private void ReviewSustained(Army army, Kingdom kingdom, MobileParty[] members, double now)
        {
            var target = _target!;
            var leader = army.LeaderParty;
            if (kingdom.StringId != _kingdomId || kingdom.IsEliminated || members.Any(p => p.IsMainParty) ||
                !ArmyOrders.CanDirect(leader, kingdom) ||
                members.Any(p => p.AttachedTo == leader && !_surplus.Contains(p) && !ArmyOrders.CanDirect(p, kingdom)))
            {
                Close("sustained-formation-ineligible");
                return;
            }
            var guard = _phase == Guarding;
            if (guard && target.MapFaction != kingdom)
            {
                Close("guard-fortification-lost");
                return;
            }
            if (!guard && (target.MapFaction is not IFaction owner || owner == kingdom || !kingdom.IsAtWarWith(owner)))
            {
                Close(target.MapFaction == kingdom ? "follow-on-fortification-taken" : "follow-on-war-ended");
                return;
            }
            bool ownCamp = leader.BesiegedSettlement == target;
            if (!guard && _campObserved && !ownCamp)
            {
                Close("follow-on-camp-ended-without-capture");
                return;
            }
            bool orderMatches = army.AiBehaviorObject == target && leader.TargetSettlement == target &&
                (guard ? leader.DefaultBehavior == AiBehavior.DefendSettlement : leader.DefaultBehavior == AiBehavior.BesiegeSettlement);
            if (!orderMatches && (guard || !ownCamp))
            {
                Close("sustained-order-no-longer-active");
                return;
            }
            // Read physical power, not AssignableStrength: the latter masks a
            // formation in an encounter. Never include travelers or cancelled calls.
            var present = KingdomForces.Read(kingdom).Parties.Where(row => row.Army == army && row.IsPresent &&
                !_surplus.Contains(row.Party)).Sum(row => row.Strength);
            if (guard && now >= _guardUntil)
            {
                var next = _followOn;
                _followOn = null; // one nominated handoff; never re-evaluate it after a rejection
                double hours = double.PositiveInfinity;
                float score = 0f;
                string? veto = next == null ? "no-nominated-follow-on" :
                    members.Any(p => (p == leader || p.AttachedTo == leader) && ArmyOrders.EventLocked(p)) ? "native-event-lock" :
                    FixedFollowOnCooldown(kingdom, next, now) ? "repulse-cooldown" :
                    KingdomCommander.FollowOnBlocker(army, kingdom, target, next, present, out hours, out score);
                if (veto == null && KnownSiegeThreat(leader, next!, present))
                    veto = "visible-overwhelming-local-threat";
                _log.Event($"Assignment guard complete: {Status()} followOn={next?.StringId ?? "-"} decision={(veto == null ? "reuse" : "release")} " +
                    $"reason={veto ?? "eligible-neighbor"} present={CampaignLog.Number(present)} routeHours={CampaignLog.Number(hours)} nativeScore={CampaignLog.Number(score)}.");
                if (veto != null)
                {
                    Close($"guard-complete({veto})");
                    return;
                }
                ArmyOrders.Besiege(army, next!);
                _target = next;
                _phase = Besieging;
                _log.Event($"Assignment follow-on issued: {Status()} {CampaignLog.ArmyState(army)}.");
                return; // confirm the new native order at the next review before spending
            }
            if (!guard)
            {
                if (ownCamp)
                    _campObserved = true;
                else if (leader.MapEvent == null && (FixedFollowOnCooldown(kingdom, target, now) ? "repulse-cooldown" :
                    KingdomCommander.NewSiegeBlocker(army, kingdom, target, present, out _, out _)) is string blocker)
                {
                    Close($"follow-on-no-longer-feasible({blocker})");
                    return;
                }
                string native = ownCamp ? $"camp:{leader.ShortTermBehavior}:{leader.MapEvent != null}" : "approaching";
                if (_log.Changed("follow-on", CampaignLog.ArmyId(army), native))
                    _log.Event($"Assignment follow-on observed: {Status()} native={native} present={CampaignLog.Number(present)}.");
            }
            if (present <= 0f)
            {
                Close("no-present-fighting-strength");
                return;
            }
            Sustain(army, now);
        }

        private void Sustain(Army army, double now)
        {
            if (now < _upkeepAfter || !(army.Cohesion > 0f && army.Cohesion < 50f))
                return;
            _upkeepAfter = now + 1d; // no per-tick retries or repeat on same-hour reload
            _lastUpkeep = ArmyOrders.Sustain(army, $"{_phase}:{_target?.StringId}", _log);
        }

        private void ReconcileSurplus(Army army, bool registered)
        {
            string armyId = CampaignLog.ArmyId(army);
            for (int i = _surplus.Count - 1; i >= 0; --i)
            {
                var party = _surplus[i];
                string key = $"{armyId}/{party.StringId}";
                string? wait = ArmyOrders.CancelSurplus(army, party, registered, _log);
                if (wait == null)
                    _surplus.RemoveAt(i);
                if (_log.Changed("muster-surplus", key, wait) && wait != null)
                    _log.Event($"Muster surplus cancellation deferred: army={armyId} party={party.StringId} " +
                        $"reason={wait} attached={party.AttachedTo == army.LeaderParty} countedForMarch=false; leaving native event alone.");
            }
        }

        private string Status() =>
            $"phase={_phase switch { Gathering => "gathering", Marching => "marching", Closing => "closing", Finished => "finished", Guarding => "guarding", Besieging => "besieging", _ => "idle" }} " +
            $"kingdom={_kingdomId ?? "-"} target={_target?.StringId ?? "-"} muster={_muster?.StringId ?? "-"} " +
            $"required={CampaignLog.Number(_required)} deadline={CampaignLog.Number(_deadline)} " +
            $"army={(_army == null ? "-" : CampaignLog.ArmyId(_army))} outcome={_outcome ?? "-"} queued={_requested != null} " +
            $"pendingSurplus=[{string.Join(", ", _surplus.Select(party => party.StringId))}]" +
            (_guardHours > 0 ? $" guardUntil={CampaignLog.Number(_guardUntil)} followOn={_followOn?.StringId ?? "-"}" : "") +
            (_army == null ? "" : $" cohesion={CampaignLog.Number(_army.Cohesion)} lastUpkeep={_lastUpkeep ?? "-"}") +
            (_acceptanceOwner == null ? "" : $" acceptance={_acceptanceOwner}");

        private void Close(string reason, bool visitLogged = false)
        {
            if (!visitLogged && _army != null && (_phase == Gathering ? _muster : _phase == Marching ? _target : null) is Settlement expected)
                _log.Event($"Muster visit before cancellation: {VisitState(_army, expected, CampaignTime.Now.ToHours)} reason={reason}.");
            _outcome = reason;
            _phase = Closing;
            _log.Event($"Muster closing: {Status()}.");
        }

        private void Finish(string reason)
        {
            _outcome = reason;
            _phase = Finished;
            _log.Event($"Muster finished: {Status()}.");
            _army = null;
            _cancelFollowOn = false;
            _surplus.Clear();
        }

        private static IEnumerable<string> ReportLines(string trigger)
        {
            foreach (var kingdom in Kingdom.All.OrderBy(item => item.StringId, StringComparer.Ordinal))
            {
                var forces = KingdomForces.Read(kingdom);
                var free = forces.Parties.Where(row => row.Army == null && row.Exclusion == null).ToArray();
                var committed = forces.Parties.Where(row => row.Army != null && row.Exclusion == null).ToArray();
                var excluded = forces.Parties.Where(row => row.Exclusion != null).ToArray();
                yield return $"Force inventory trigger={trigger} kingdom={kingdom.StringId} name=\"{kingdom.Name}\" strengthModel=estimated " +
                    $"armies={forces.Armies.Count} parties={forces.Parties.Count} available={free.Length} " +
                    $"availableStrength={CampaignLog.Number(free.Sum(row => row.Strength))} committed={committed.Length} " +
                    $"committedPresentStrength={CampaignLog.Number(committed.Where(row => row.IsPresent).Sum(row => row.Strength))} " +
                    $"committedTravelingStrength={CampaignLog.Number(committed.Where(row => !row.IsPresent).Sum(row => row.Strength))} " +
                    $"excluded={excluded.Length} excludedStrength={CampaignLog.Number(excluded.Sum(row => row.Strength))} " +
                    $"observedStrength={CampaignLog.Number(forces.Parties.Sum(row => row.Strength))} " +
                    $"assignableStrength={CampaignLog.Number(forces.Parties.Sum(row => row.AssignableStrength))}.";

                foreach (var army in forces.Armies.OrderBy(CampaignLog.ArmyId, StringComparer.Ordinal))
                {
                    var members = forces.Parties.Where(row => row.Army == army).ToArray();
                    var present = members.Where(row => row.IsPresent).ToArray();
                    var traveling = members.Where(row => row.Membership == KingdomForces.Role.Traveling).ToArray();
                    var leader = present.FirstOrDefault(row => row.Membership == KingdomForces.Role.Leader);
                    string reason = leader?.Reason(kingdom) ?? "leader-not-accounted-for";
                    yield return $"Force army kingdom={kingdom.StringId} army={CampaignLog.ArmyId(army)} leader={army.LeaderParty.StringId} " +
                        $"position={Position(army.LeaderParty.Position)} reason={reason} " +
                        $"present={present.Length} presentStrength={CampaignLog.Number(present.Sum(row => row.Strength))} " +
                        $"traveling={traveling.Length} travelingStrength={CampaignLog.Number(traveling.Sum(row => row.Strength))} " +
                        $"assignablePresentStrength={CampaignLog.Number(present.Sum(row => row.AssignableStrength))} " +
                        $"assignableTravelingStrength={CampaignLog.Number(traveling.Sum(row => row.AssignableStrength))} " +
                        $"siegeSide={leader?.SiegeSide ?? "-"} siegeTarget={leader?.SiegeTargetId ?? "-"} mapEvent={leader?.MapEventType ?? "-"}.";
                }

                foreach (var row in forces.Parties)
                {
                    var party = row.Party;
                    string availability = row.Exclusion != null ? "excluded" : row.Army != null ? "committed" : "available";
                    yield return $"Force party kingdom={kingdom.StringId} party={party.StringId} name=\"{party.Name}\" " +
                        $"army={(row.Army == null ? "-" : CampaignLog.ArmyId(row.Army))} role={row.Membership} " +
                        $"availability={availability} reason={row.Reason(kingdom)} position={Position(row.Position)} settlement={row.SettlementId ?? "-"} " +
                        $"strength={CampaignLog.Number(row.Strength)} assignableStrength={CampaignLog.Number(row.AssignableStrength)} " +
                        $"siegeSide={row.SiegeSide} siegeTarget={row.SiegeTargetId ?? "-"} mapEvent={row.MapEventType ?? "-"}.";
                }
            }
        }

        private static string Position(CampaignVec2 position) =>
            $"({CampaignLog.Number(position.X)},{CampaignLog.Number(position.Y)},{(position.IsOnLand ? "land" : "sea")})";
    }
}
