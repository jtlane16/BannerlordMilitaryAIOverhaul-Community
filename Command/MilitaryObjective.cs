using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Command
{
    // The siege, not a particular responding army, defines the life of a job.
    internal sealed class MilitaryObjective
    {
        internal readonly Kingdom Kingdom;
        internal readonly Kingdom Owner;
        internal readonly Settlement Target;
        internal readonly SiegeEvent Siege;
        internal readonly MobileParty CampLeader;
        internal readonly IFaction Besieger;
        internal readonly float Value;
        internal readonly float CampPower;
        internal readonly float LocalRadius;
        internal CampaignVec2 CampPosition => CampLeader.Position;
        internal bool HasLandCamp => !CampLeader.IsCurrentlyAtSea && CampPosition.IsOnLand;
        internal bool IsHome => Kingdom == Owner;
        internal string Scope => IsHome ? "home" : "allied";
        internal string Key => $"{Kingdom.StringId}/{Target.StringId}";

        private MilitaryObjective(Kingdom kingdom, Kingdom owner, Settlement target, SiegeEvent siege)
        {
            (Kingdom, Owner, Target, Siege, CampLeader, Besieger) =
                (kingdom, owner, target, siege, siege.BesiegerCamp.LeaderParty, siege.BesiegerCamp.MapFaction);
            Value = target.GetSettlementValueForFaction(kingdom);
            CampPower = siege.BesiegerCamp.GetInvolvedPartiesForEventType().Distinct()
                .Where(party => party.MobileParty is { IsActive: true, IsCurrentlyAtSea: false })
                .Sum(party => Power(party, BattleSideEnum.Defender));
            LocalRadius = NativeCampaign.Current.Models.MobilePartyAIModel.SettlementDefendingNearbyPartyCheckRadius;
        }

        internal static bool FormallyAllied(Kingdom kingdom, Kingdom owner) => kingdom != owner &&
            !kingdom.IsEliminated && !owner.IsEliminated &&
            NativeCampaign.Current.GetCampaignBehavior<IAllianceCampaignBehavior>()?.IsAllyWithKingdom(kingdom, owner) == true;

        internal static string? AidBlocker(Kingdom kingdom, Kingdom owner, IFaction besieger)
        {
            if (!FormallyAllied(kingdom, owner) || kingdom.IsAtWarWith(owner))
                return "formal-alliance-not-active";
            return kingdom != besieger && kingdom.IsAtWarWith(besieger) ? null : "reliever-not-at-war-with-besieger";
        }

        internal static bool EnemyFortification(Settlement target, Kingdom kingdom) =>
            target.IsFortification && !kingdom.IsEliminated && target.MapFaction is IFaction owner &&
            owner != kingdom && !owner.IsEliminated && (owner is not Kingdom other || !FormallyAllied(kingdom, other)) &&
            kingdom.IsAtWarWith(owner);

        internal static string? ReliefBlocker(Settlement target, Kingdom kingdom, string expectedOwner)
        {
            if (!target.IsFortification || kingdom.IsEliminated)
                return "fortification-or-kingdom-ineligible";
            if (target.MapFaction is not Kingdom owner || owner.IsEliminated || owner.StringId != expectedOwner)
                return "settlement-owner-changed";
            if (target.SiegeEvent is not SiegeEvent siege)
                return "siege-ended-owner-retained";
            if (siege.BesiegerCamp?.LeaderParty is not { IsActive: true } leader ||
                !siege.BesiegerCamp.HasInvolvedPartyForEventType(leader.Party))
                return "no-active-camp";
            if (siege.BesiegerCamp.MapFaction is not IFaction attacker || leader.MapFaction != attacker ||
                attacker == owner || !owner.IsAtWarWith(attacker))
                return "no-longer-hostile";
            return kingdom == owner ? null : AidBlocker(kingdom, owner, attacker);
        }

        internal static MilitaryObjective? Relief(Settlement target, Kingdom kingdom) =>
            target.MapFaction is Kingdom owner && ReliefBlocker(target, kingdom, owner.StringId) == null ?
                new(kingdom, owner, target, target.SiegeEvent) : null;

        internal bool StillEligible => Target.SiegeEvent == Siege && Siege.BesiegerCamp?.LeaderParty == CampLeader &&
            Siege.BesiegerCamp.MapFaction == Besieger &&
            ReliefBlocker(Target, Kingdom, Owner.StringId) == null;

        internal bool Sufficient(float fieldPower) => fieldPower > 1.2f * CampPower;

        internal bool IsLocal(MobileParty party) => HasLandCamp && party.IsActive && !party.IsCurrentlyAtSea &&
            !party.IsTransitionInProgress && party.CurrentSettlement == null && party.Position.IsOnLand &&
            party.Position.DistanceSquared(CampPosition.ToVec2()) <= LocalRadius * LocalRadius &&
            KingdomCommander.LandDistance(party.Position, CampPosition, unrestricted: true) <= LocalRadius;

        internal bool Responding(MobileParty party)
        {
            // The party can have an older long-term defend order while it
            // engages another nearby camp. Credit its actual fight/contact
            // first, not both a tactical target and a strategic one.
            if (party.MapEvent is { IsFinalized: false })
                return InReliefBattle(party);
            if (party.ShortTermBehavior is AiBehavior.EngageParty or AiBehavior.JoinParty &&
                party.ShortTermTargetParty is { } contact)
                return contact.BesiegerCamp == Siege.BesiegerCamp;
            return party.DefaultBehavior == AiBehavior.DefendSettlement && party.TargetSettlement == Target;
        }

        internal bool InReliefBattle(MobileParty party) => party.MapEvent is { IsFinalized: false } battle &&
            (battle == CampLeader.MapEvent || (battle.MapEventSettlement == Target && battle.IsSiegeOutside)) &&
            party.MapEventSide != null && party.MapEventSide != CampLeader.MapEventSide &&
            party.MapFaction?.IsAtWarWith(CampLeader.MapFaction) == true;

        internal static float Power(PartyBase party, BattleSideEnum side = BattleSideEnum.Attacker,
            MapEvent.PowerCalculationContext context = MapEvent.PowerCalculationContext.PlainBattle)
        {
            float power = party.GetCustomStrength(side, context);
            return power > 0 && !float.IsInfinity(power) ? power : 0f;
        }
    }
}
