using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Command
{
    internal sealed class KingdomForces
    {
        internal enum Role { Independent, Leader, Attached, Traveling }

        internal sealed class PartyForce
        {
            internal MobileParty Party { get; }
            internal Army? Army { get; }
            internal Role Membership { get; }
            internal CampaignVec2 Position { get; }
            internal string? SettlementId { get; }
            internal string? SiegeTargetId { get; }
            internal bool IsInsideBesiegedSettlement { get; }
            internal bool IsBesieging { get; }
            internal string SiegeSide => IsInsideBesiegedSettlement ? "defender" : IsBesieging ? "attacker" : "-";
            internal string? MapEventType { get; }
            internal bool IsTacticallyLocked => MapEventType != null || IsInsideBesiegedSettlement;
            internal float Strength { get; }
            internal string? Exclusion { get; private set; }
            internal bool IsPresent => Membership == Role.Leader || Membership == Role.Attached;
            internal float AssignableStrength => Exclusion == null ? Strength : 0f;

            internal string Reason(Kingdom kingdom) =>
                Exclusion ?? (Army != null ? (IsBesieging ? "attacking-siege-camp" : "army-commitment")
                    : Party.LeaderHero == kingdom.Leader ? "independent-ruler-not-a-summon"
                    : Party.LeaderHero?.Clan?.IsUnderMercenaryService == true ? "independent-mercenary-not-an-army-leader"
                    : "independent");

            internal PartyForce(Kingdom kingdom, MobileParty party, Army? army, bool registered, float strength,
                IDisbandPartyCampaignBehavior? disbanding)
            {
                Party = party;
                Army = army;
                Membership = army == null ? Role.Independent : party == army.LeaderParty ? Role.Leader
                    : party.AttachedTo == army.LeaderParty ? Role.Attached : Role.Traveling;
                Position = party.Position;
                SettlementId = party.CurrentSettlement?.StringId;
                IsInsideBesiegedSettlement = party.CurrentSettlement?.IsUnderSiege == true;
                var campSiege = party.SiegeEvent;
                IsBesieging = campSiege != null;
                SiegeTargetId = IsInsideBesiegedSettlement ? SettlementId : campSiege?.BesiegedSettlement?.StringId;
                // Wait is also the initial state of an assault or field battle.
                // Only a finalized event is no longer an encounter lock.
                var mapEvent = party.MapEvent;
                MapEventType = mapEvent is { IsFinalized: false } ? mapEvent.Component?.GetBattleType().ToString() ?? "unknown" : null;
                bool invalid = float.IsNaN(strength) || float.IsInfinity(strength) || strength < 0f;
                Strength = invalid ? 0f : strength;
                Exclusion = ExclusionFor(kingdom, this, disbanding);
                if (army != null && !registered)
                    Exclusion ??= "unregistered-army";
                if (Strength == 0f)
                    Exclusion ??= invalid ? "invalid-strength" : "no-fighting-strength";
            }

            internal PartyForce WithExclusion(string reason)
            {
                var row = (PartyForce)MemberwiseClone();
                row.Exclusion = reason;
                return row;
            }
        }

        internal IReadOnlyList<Army> Armies { get; }
        internal IReadOnlyList<PartyForce> Parties { get; }

        private KingdomForces(Army[] armies, PartyForce[] parties)
        {
            Armies = Array.AsReadOnly(armies);
            Parties = Array.AsReadOnly(parties);
        }

        internal static KingdomForces Read(Kingdom kingdom)
        {
            var armies = kingdom.Armies.Distinct().ToArray();
            var registered = new HashSet<Army>(armies);
            var candidates = new Dictionary<MobileParty, Army?>();
            var disbanding = NativeCampaign.Current.GetCampaignBehavior<IDisbandPartyCampaignBehavior>();

            // Army.Parties includes summons that have not arrived. Also account for the
            // leader and attached parties even if the kingdom's war-party list lacks them.
            foreach (var army in armies)
            {
                Add(army.LeaderParty, army);
                foreach (var party in army.Parties)
                    Add(party, army);
                foreach (var party in army.LeaderParty.AttachedParties)
                    Add(party, army);
            }
            foreach (var component in kingdom.WarPartyComponents)
                Add(component.MobileParty, null);
            Add(MobileParty.MainParty, null);

            var rows = new List<PartyForce>(candidates.Count);
            var lockedArmies = new HashSet<Army>();
            foreach (var candidate in candidates.OrderBy(entry => entry.Key.StringId, StringComparer.Ordinal))
            {
                var party = candidate.Key;
                var army = candidate.Value;
                // Same objective-independent context as the native army strength estimate.
                // Ask the model for fresh per-party power, not an army total or headcount.
                float strength = party.Party.GetCustomStrength(BattleSideEnum.Defender, MapEvent.PowerCalculationContext.Estimated);
                var row = new PartyForce(kingdom, party, army, army != null && registered.Contains(army), strength, disbanding);
                if (army != null && row.IsPresent && row.IsTacticallyLocked)
                    lockedArmies.Add(army);
                rows.Add(row);
            }

            // An army whose leader cannot be directed cannot be assigned intact.
            // Keep each member's own reason if it already has one.
            var leaders = rows.Where(row => row.Membership == Role.Leader).ToDictionary(row => row.Army!);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Army == null || row.Exclusion != null)
                    continue;
                if (lockedArmies.Contains(row.Army))
                    rows[i] = row.WithExclusion("army-tactically-locked");
                else if (!leaders.TryGetValue(row.Army, out var leader) || leader.Exclusion != null)
                    rows[i] = row.WithExclusion("army-leader-unavailable");
            }
            return new KingdomForces(armies, rows.ToArray());

            void Add(MobileParty? party, Army? rosterArmy)
            {
                if (party == null || candidates.ContainsKey(party))
                    return;
                var linkedArmy = party.Army ?? party.AttachedTo?.Army;
                // A linked army places the party in exactly one kingdom's report.
                // Without a link, fall back to current map faction, not a stale list.
                if ((linkedArmy?.Kingdom ?? party.MapFaction) == kingdom)
                    candidates.Add(party, linkedArmy ?? rosterArmy);
            }
        }

        private static string? ExclusionFor(Kingdom kingdom, PartyForce row, IDisbandPartyCampaignBehavior? disbanding)
        {
            var party = row.Party;
            var army = row.Army;
            var leader = party.LeaderHero;
            if (party.IsMainParty)
                return "player-main-party";
            if (army?.LeaderParty.IsMainParty == true)
                return "player-led-army";
            if (leader?.CanJoinArmy == false)
                return leader.Clan == Clan.PlayerClan ? "player-clan-opt-out" : "joining-armies-disabled";
            if (party.MapFaction != kingdom)
                return "different-faction";
            if (kingdom.IsEliminated)
                return "kingdom-eliminated";
            if (!party.IsLordParty)
                return "not-a-lord-party";
            if (!party.IsActive)
                return "inactive-party";
            if (leader == null)
                return "no-party-leader";
            if (leader.Clan == null)
                return "no-leader-clan";
            if (leader.IsPrisoner || !leader.IsActive)
                return "leader-unavailable";
            if (party.IsDisbanding || disbanding?.IsPartyWaitingForDisband(party) == true)
                return "disbanding";
            if (row.MapEventType != null || (army == null && party.MapEvent != null))
                return "in-map-event";
            if (row.IsInsideBesiegedSettlement)
                return "inside-besieged-settlement";
            // An army already at its attacking camp can break off outside combat.
            // An independent besieger is occupied, not a free summons.
            bool atArmyCamp = row.IsPresent && army != null && party.SiegeEvent is { } siege && siege == army.LeaderParty.SiegeEvent;
            if (row.IsBesieging && !atArmyCamp)
                return "other-siege-commitment";
            if (party.IsInRaftState)
                return "lost-at-sea";
            if (party.IsCurrentlyUsedByAQuest)
                return "quest-party";
            if (party.Ai == null || party.Ai.IsDisabled || party.Ai.DoNotMakeNewDecisions)
                return "ai-unavailable";
            if ((army != null && party.Army != army) ||
                (party.AttachedTo != null && party.AttachedTo != army?.LeaderParty))
                return "inconsistent-membership";
            if (!atArmyCamp && !leader.CanLeadParty())
                return "native-leader-ineligible";
            // Route, food, party-size thresholds, and influence are muster
            // preflight, not a commitment or a measure of current fighting power.
            return null;
        }
    }
}
