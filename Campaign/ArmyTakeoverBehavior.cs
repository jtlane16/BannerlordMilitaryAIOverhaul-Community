using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed class ArmyTakeoverBehavior : CampaignBehaviorBase
    {
        private static ArmyTakeoverBehavior? _current;
        internal static ArmyTakeoverBehavior? Current => _current?.Campaign == NativeCampaign.Current ? _current : null;
        internal NativeCampaign Campaign { get; }
        internal bool InitialCheckComplete { get; private set; }
        internal bool IsPending(Army army) => _pending.Contains(army);
        private CampaignLog Log { get; }
        private Army? Releasing { get; set; }

        private bool _captured;
        private bool _checkOnNextTick;
        private List<Army> _pending = new();
        private readonly Dictionary<Army, (Army.ArmyDispersionReason reason, string before, string parties, bool released)>
            _unsettledDispersals = new();

        // Bannerlord keys the saved behavior data by this ID, not the renamed CLR type.
        internal ArmyTakeoverBehavior(NativeCampaign campaign, CampaignLog log) : base("BannerlordMilitaryAIOverhaul.Stage0Takeover")
        {
            Campaign = campaign;
            Log = log;
            _current = this;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnAfterSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            CampaignEvents.QuarterHourlyTickEvent.AddNonSerializedListener(this, CheckPending);
            CampaignEvents.ArmyCreated.AddNonSerializedListener(this, OnArmyCreated);
            CampaignEvents.OnPartyLeftArmyEvent.AddNonSerializedListener(this, OnPartyLeftArmy);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, OnSiegeEnded);
        }

        public override void SyncData(IDataStore dataStore)
        {
            if (dataStore.IsSaving)
                ReconcileDispersals();
            dataStore.SyncData("captured", ref _captured);
            // Vanilla also syncs List<Army>. Exact save references cannot match a later army
            // just because it reuses the same leader, nor depend on a custom save type ID.
            var pending = dataStore.IsSaving ? _pending.ToList() : _pending;
            dataStore.SyncData("pendingArmies", ref pending);
            if (dataStore.IsLoading)
                _pending = pending ?? new List<Army>();
            else if (_captured)
                Log.Event($"Takeover save state: captured=true pending=[{string.Join(", ", pending.Select(CampaignLog.ArmyId))}].");
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            bool first = !_captured;
            if (first)
            {
                var armies = Kingdom.All.SelectMany(kingdom => kingdom.Armies).Distinct().ToArray();
                _pending.AddRange(armies.Where(IsAiLed));
                _captured = true;
                Log.Event($"Takeover captured {_pending.Count} existing AI armies; player-led armies left alone: {armies.Length - _pending.Count}.");
            }
            else
            {
                Log.Event($"Takeover loaded with {_pending.Count} pending release(s); no new snapshot.");
            }

            foreach (Army army in _pending)
                Log.Event($"Takeover {(first ? "queued" : "resumed")}: {CampaignLog.ArmyState(army)} parties=[{DescribeMembers(army)}].");
            // SessionLaunched precedes vanilla map/siege manager OnAfterLoad. No mutation
            // until a campaign tick; event cleanup is likewise never re-entered from a callback.
            _checkOnNextTick = true;
        }

        private void OnTick(float dt)
        {
            if (!_checkOnNextTick)
                return;
            _checkOnNextTick = false;
            CheckPending();
            InitialCheckComplete = true;
        }

        private void CheckPending()
        {
            ReconcileDispersals();
            for (int i = _pending.Count - 1; i >= 0; --i)
            {
                Army army = _pending[i];
                if (army.IsDispersing)
                    continue;
                if (army.Kingdom == null || !army.Kingdom.Armies.Contains(army))
                {
                    _pending.RemoveAt(i);
                    Log.Event($"Takeover ended without release: army={CampaignLog.ArmyId(army)} is no longer registered.");
                    continue;
                }
                if (!IsAiLed(army))
                {
                    _pending.RemoveAt(i);
                    Log.Event($"Takeover canceled, now player-led: {CampaignLog.ArmyState(army)}.");
                    continue;
                }

                string wait = ReleaseBlockers(army);
                if (wait.Length != 0)
                {
                    if (Log.Changed("takeover-wait", CampaignLog.ArmyId(army), wait))
                        Log.Event($"Takeover deferred: {CampaignLog.ArmyState(army)} active events=[{wait}].");
                    continue;
                }

                Log.Event($"Takeover releasing: {CampaignLog.ArmyState(army)} no active battle or siege; parties=[{DescribeMembers(army)}].");
                Releasing = army;
                try
                {
                    DisbandArmyAction.ApplyByUnknownReason(army);
                }
                finally
                {
                    Releasing = null;
                }
            }
        }

        internal static bool IsAiLed(Army? army) => army?.LeaderParty is MobileParty leader && !leader.IsMainParty;

        private static IEnumerable<MobileParty> Members(Army army) =>
            army.Parties.Concat(army.LeaderParty.AttachedParties).Append(army.LeaderParty).Distinct();

        internal static string DescribeMembers(Army army) => string.Join(", ", Members(army).Select(party =>
            $"{party.StringId}:{(party == army.LeaderParty ? "leader" : party.AttachedTo == army.LeaderParty ? "attached" : "traveling")}"));

        private static string ReleaseBlockers(Army army)
        {
            var blockers = new List<string>();
            foreach (MobileParty party in Members(army))
            {
                if (party.MapEvent is { } battle)
                    blockers.Add($"{party.StringId}:map-event({battle.EventType}, {battle.MapEventSettlement?.StringId ?? "field"})");
                if ((party.SiegeEvent ?? party.CurrentSettlement?.SiegeEvent) is { } siege)
                    blockers.Add($"{party.StringId}:siege({siege.BesiegedSettlement.StringId})");
            }
            blockers.Sort(System.StringComparer.Ordinal);
            return string.Join(", ", blockers);
        }

        internal void OnDispersalReturned(Army army, Army.ArmyDispersionReason reason, string before, string parties)
        {
            var observation = (reason, before, parties, released: ReferenceEquals(Releasing, army));
            if (army.Kingdom == null && army.Parties.Count == 0 && army.LeaderParty.Army == army)
            {
                // Removing the leader can disperse the army inside MobileParty.Army's
                // setter, before that setter updates the leader's own reference.
                _unsettledDispersals[army] = observation;
                _checkOnNextTick = true;
                return;
            }
            RecordDispersal(army, observation);
        }

        private void ReconcileDispersals()
        {
            foreach (var entry in _unsettledDispersals)
                RecordDispersal(entry.Key, entry.Value);
            _unsettledDispersals.Clear();
        }

        private void RecordDispersal(Army army, (Army.ArmyDispersionReason reason, string before, string parties, bool released) observation)
        {
            bool leaderStillMember = army.LeaderParty.Army == army;
            bool finished = army.Kingdom == null && army.Parties.Count == 0 && !leaderStillMember;
            bool registered = army.Kingdom?.Armies.Contains(army) == true;
            bool pending = _pending.Contains(army);
            // An unregistered army cannot be released by takeover even if its
            // backlinks are inconsistent. Report that here, not as a disappearance.
            if (pending && !registered)
                _pending.Remove(army);
            Log.Event($"Army dispersal outcome={(finished ? "complete" : "incomplete")} " +
                $"source={(observation.released ? "Stage 0 takeover" : ReferenceEquals(ArmyOrders.Releasing, army) ? "commander" : "native")} reason={observation.reason} " +
                $"pendingTakeover={pending} {observation.before} partiesBefore=[{observation.parties}] " +
                $"remaining={army.Parties.Count} registered={registered} kingdomCleared={army.Kingdom == null} leaderStillMember={leaderStillMember}.");
        }

        private void OnPartyLeftArmy(MobileParty party, Army army)
        {
            // This event follows the reference assignment. A direct transfer to
            // another army has no leave event and is reconciled before the next scan.
            if (party == army.LeaderParty && _unsettledDispersals.TryGetValue(army, out var observation))
            {
                _unsettledDispersals.Remove(army);
                RecordDispersal(army, observation);
            }
            if (!ReferenceEquals(Releasing, army) || party.IsMainParty || !party.IsActive || party.Army != null)
                return;
            // Native detachment clears attached orders. An unattached member may still
            // have the stale escort order; clear only that order, then ask vanilla to rethink.
            bool clearEscort = party.DefaultBehavior == AiBehavior.EscortParty && party.TargetParty == army.LeaderParty;
            if (clearEscort)
                party.SetMoveModeHold();
            party.Ai.RethinkAtNextHourlyTick = true;
            Log.Event($"Takeover party detached: army={CampaignLog.ArmyId(army)} party={party.StringId} " +
                $"armyMembership=none staleEscortCleared={clearEscort} nextOrder=vanilla.");
        }

        private void OnArmyCreated(Army army) =>
            Log.Event($"Army created: leaderControl={(ReferenceEquals(ArmyOrders.Announcing, army) ? "commander" : IsAiLed(army) ? "AI (unexpected native creation)" : "player")} " +
                $"{CampaignLog.ArmyState(army)} parties=[{DescribeMembers(army)}].");

        private void OnMapEventEnded(MapEvent battle)
        {
            foreach (Army army in _pending)
            {
                if (!Members(army).Any(party => party.MapEvent == battle))
                    continue;
                Log.Event($"Takeover waiting on native event: {CampaignLog.ArmyState(army)} map-event={battle.EventType} " +
                    $"site={battle.MapEventSettlement?.StringId ?? "field"} ended battleState={battle.BattleState}.");
                _checkOnNextTick = true;
            }
        }

        private void OnSiegeEnded(SiegeEvent siege)
        {
            foreach (Army army in _pending)
            {
                if (!Members(army).Any(party => party.SiegeEvent == siege || party.CurrentSettlement?.SiegeEvent == siege))
                    continue;
                Log.Event($"Takeover waiting on native event: {CampaignLog.ArmyState(army)} siege={siege.BesiegedSettlement.StringId} ended.");
                _checkOnNextTick = true;
            }
        }

        internal void Close()
        {
            if (Campaign == NativeCampaign.Current)
                ReconcileDispersals();
            CampaignLog.Write($"Stage 0 campaign ended; pending releases={_pending.Count}.");
            if (ReferenceEquals(_current, this))
                _current = null;
        }
    }
}
