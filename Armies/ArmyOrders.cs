using System;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Armies
{
    internal static class ArmyOrders
    {
        internal const int InfluenceReserve = 200;
        private static Debit? _debit;
        internal static Army? Announcing { get; private set; }
        internal static Army? Releasing { get; private set; }

        private sealed class Debit
        {
            internal readonly MobileParty Leader;
            internal readonly MobileParty Joiner;
            internal readonly Clan Payer;
            internal readonly CampaignLog Log;
            internal readonly bool AllowPaid;
            internal readonly Action<float>? OnPaid;
            internal readonly Func<bool>? CanCharge;
            internal bool Accepted;
            internal bool Seen;

            internal Debit(MobileParty leader, MobileParty joiner, Clan payer, CampaignLog log,
                bool allowPaid = true, Action<float>? onPaid = null, Func<bool>? canCharge = null)
            {
                Leader = leader;
                Joiner = joiner;
                Payer = payer;
                Log = log;
                AllowPaid = allowPaid;
                OnPaid = onPaid;
                CanCharge = canCharge;
            }
        }

        internal static bool Affordable(float influence, float cost) =>
            cost == 0f || (cost > 0f && cost <= Math.Max(0f, influence - InfluenceReserve));

        internal static int CallCost(MobileParty leader, MobileParty party) =>
            NativeCampaign.Current.Models.ArmyManagementCalculationModel.CalculatePartyInfluenceCost(leader, party);

        // Used only in Army.OnAddPartyInternal, after vanilla has calculated the
        // actual price and just before its one debit. No global influence patch.
        internal static void ApplyMembershipInfluence(Clan clan, float amount, Army army, MobileParty party)
        {
            var debit = _debit;
            if (debit == null || army.LeaderParty != debit.Leader || party != debit.Joiner)
            {
                ChangeClanInfluenceAction.Apply(clan, amount);
                return;
            }

            if (debit.Seen)
                return;
            debit.Seen = true;
            float before = clan.Influence;
            float cost = -amount;
            bool leaderCall = debit.Leader == party;
            bool legal = clan == debit.Payer && army.Kingdom is Kingdom kingdom &&
                party.Army == army && CanDirect(army.LeaderParty, kingdom) && CanDirect(party, kingdom) &&
                !EventLocked(army.LeaderParty) && !EventLocked(party) &&
                (cost == 0f || (!leaderCall && debit.AllowPaid)) && Affordable(before, cost) &&
                (cost == 0f || debit.CanCharge?.Invoke() != false);
            if (legal)
            {
                debit.Accepted = true;
                if (cost > 0f)
                    ChangeClanInfluenceAction.Apply(clan, amount);
            }
            float actual = before - clan.Influence;
            if (legal && actual > 0f)
                debit.OnPaid?.Invoke(actual);
            debit.Log.Event($"Muster debit army={CampaignLog.ArmyId(army)} party={party.StringId} payer={clan.StringId} " +
                $"balanceBefore={CampaignLog.Number(before)} reserve={InfluenceReserve} budget={CampaignLog.Number(Math.Max(0f, before - InfluenceReserve))} " +
                $"nativeCost={CampaignLog.Number(cost)} accepted={legal} actualDebit={CampaignLog.Number(actual)} balanceAfter={CampaignLog.Number(clan.Influence)}.");
        }

        internal static bool CanDirect(MobileParty party, Kingdom kingdom) =>
            !party.IsMainParty && party.IsActive && party.IsLordParty && !party.IsDisbanding &&
            party.LeaderHero is { IsActive: true, IsPrisoner: false, CanJoinArmy: true, Clan: not null } && party.MapFaction == kingdom &&
            party.Army?.LeaderParty.IsMainParty != true;

        internal static string? FreePartyBlocker(MobileParty party, Kingdom kingdom, bool asLeader, Army? afterRelease = null)
        {
            bool fromReleasableCamp = afterRelease != null && party.BesiegerCamp != null &&
                party.BesiegerCamp == afterRelease.LeaderParty.BesiegerCamp &&
                party.MapEvent == null && party.CurrentSettlement?.IsUnderSiege != true;
            if (!CanDirect(party, kingdom) || kingdom.IsEliminated ||
                (party.Army != null && party.Army != afterRelease) ||
                (party.AttachedTo != null && (afterRelease == null || party.AttachedTo != afterRelease.LeaderParty)) ||
                (EventLocked(party) && !fromReleasableCamp) || party.IsInRaftState || party.IsCurrentlyUsedByAQuest ||
                party.Ai == null || party.Ai.IsDisabled || party.Ai.DoNotMakeNewDecisions ||
                NativeCampaign.Current.GetCampaignBehavior<IDisbandPartyCampaignBehavior>()?.IsPartyWaitingForDisband(party) == true ||
                !party.LeaderHero.CanLeadParty())
                return "not-free-or-native-ineligible";
            if (party.IsCurrentlyAtSea || party.IsTransitionInProgress || !party.HasLandNavigationCapability)
                return "not-ready-for-land-movement";
            var hero = party.LeaderHero;
            var model = NativeCampaign.Current.Models.ArmyManagementCalculationModel;
            if (!(party.PartySizeRatio > model.AIMobilePartySizeRatioToCallToArmy))
                return "below-native-call-size";
            double minimumFood = asLeader ? NativeCampaign.Current.Models.MobilePartyAIModel.NeededFoodsInDaysThresholdForSiege
                : model.MinimumNeededFoodInDaysToCallToArmy;
            if (!(party.GetNumDaysForFoodToLast() > minimumFood))
                return "below-native-food-threshold";
            if (!asLeader)
                return hero == kingdom.Leader ? "ruler-not-a-summon" : null;
            var clan = hero.Clan;
            return clan.IsUnderMercenaryService || (clan.Leader != hero &&
                (clan.Leader == null || clan.Leader.PartyBelongedTo != null || clan.WarPartyComponents?.FirstOrDefault() != party.WarPartyComponent))
                ? "native-command-role" : null;
        }

        internal static bool EventLocked(MobileParty party) =>
            party.MapEvent != null || party.SiegeEvent != null || party.CurrentSettlement?.IsUnderSiege == true;

        internal static string? ReleaseBlocker(MobileParty party, bool ignoreFleeing = false) =>
            party.IsMainParty || PlayerEncounter.EncounteredMobileParty == party ? "player" :
            EventLocked(party) ? "native-event" :
            party.IsTransitionInProgress || party.StartTransitionNextFrameToExitFromPort ? "map-transition" :
            party.IsCurrentlyUsedByAQuest || party.Ai?.DoNotMakeNewDecisions == true ? "party-control-lock" :
            party.Ai?.IsDisabled == true || (!ignoreFleeing && party.IsFleeing()) ? "native-movement-lock" : null;

        internal static bool CanRelease(Army army) =>
            !army.IsDispersing && !Members(army).Any(party => ReleaseBlocker(party) != null);

        internal static MobileParty[] Members(Army army) =>
            army.Parties.Concat(army.LeaderParty.AttachedParties).Append(army.LeaderParty).Distinct().ToArray();

        internal static Army Create(Kingdom kingdom, MobileParty leader, Settlement muster, CampaignLog log, out bool formed)
        {
            var debit = new Debit(leader, leader, leader.LeaderHero.Clan, log);
            Army army;
            _debit = debit;
            try { army = new Army(kingdom, leader, Army.ArmyTypes.Defender); }
            finally { _debit = null; }
            formed = debit.Accepted && leader.Army == army && kingdom.Armies.Contains(army);
            if (!formed)
                return army;

            Visit(army, muster);
            GatherArmyAction.Apply(leader, muster); // Native notification only; Army.Gather would choose its own muster.
            Announcing = army;
            try { CampaignEventDispatcher.Instance.OnArmyCreated(army); }
            finally { Announcing = null; }
            log.Event($"Muster formed: {CampaignLog.ArmyState(army)} muster={muster.StringId} leaderMember={leader.Army == army} target={leader.TargetSettlement?.StringId ?? "-"}.");
            return army;
        }

        internal static bool Summon(Army army, MobileParty party, CampaignLog log) =>
            Summon(army, party, log, allowPaid: true, onPaid: null);

        internal static bool Summon(Army army, MobileParty party, CampaignLog log, bool allowPaid, Action<float>? onPaid,
            Func<bool>? canCharge = null)
        {
            var leader = army.LeaderParty;
            if (army.Kingdom is not Kingdom kingdom || !CanDirect(leader, kingdom) || EventLocked(leader))
                return false;
            if (FreePartyBlocker(party, kingdom, asLeader: false) is string blocker)
            {
                log.Event($"Muster call refused: army={CampaignLog.ArmyId(army)} party={party.StringId} reason={blocker}; no membership or debit.");
                return false;
            }
            var payer = leader.LeaderHero.Clan;
            int cost = CallCost(leader, party);
            if ((!allowPaid && cost != 0) || !Affordable(payer.Influence, cost))
            {
                log.Event($"Muster call refused: army={CampaignLog.ArmyId(army)} party={party.StringId} payer={payer.StringId} " +
                    $"nativeCost={cost} balance={CampaignLog.Number(payer.Influence)} reserve={InfluenceReserve}; no membership or debit.");
                return false;
            }

            var debit = new Debit(leader, party, payer, log, allowPaid, onPaid, canCharge);
            _debit = debit;
            try { party.Army = army; }
            finally { _debit = null; }
            if (!debit.Accepted || party.Army != army)
            {
                if (party.Army == army)
                    ReleaseMember(army, party, log, "summons debit refused");
                return false;
            }
            SetPartyAiAction.GetActionForEscortingParty(party, leader, MobileParty.NavigationType.Default, false, false);
            log.Event($"Muster summons: army={CampaignLog.ArmyId(army)} party={party.StringId} member={party.Army == army} " +
                $"present={party.AttachedTo == leader} order={party.DefaultBehavior} escortTarget={party.TargetParty?.StringId ?? "-"}.");
            return party.DefaultBehavior == AiBehavior.EscortParty && party.TargetParty == leader;
        }

        // Parties already inside the muster won't enter it again to trigger
        // EnterSettlementAction's native merge. Use that same merge only after
        // observing both backlinks and actual co-location inside the fortification.
        internal static void MergeAtMuster(Army army, MobileParty party, Settlement muster)
        {
            if (army.Kingdom is Kingdom kingdom && party != army.LeaderParty && party.Army == army && party.AttachedTo == null &&
                army.LeaderParty.CurrentSettlement == muster && party.CurrentSettlement == muster &&
                CanDirect(party, kingdom) && !EventLocked(party) && !EventLocked(army.LeaderParty))
                army.AddPartyToMergedParties(party);
        }

        internal static void Visit(Army army, Settlement target) =>
            SetPartyAiAction.GetActionForVisitingSettlement(army.LeaderParty, target, MobileParty.NavigationType.Default, false, false);

        // Distance is an ETA estimate, not proof that the movement path
        // builder accepts this land order. Probe into scratch storage only.
        internal static bool CanPathVisit(MobileParty party, Settlement target)
        {
            if (party.IsCurrentlyAtSea || party.IsTransitionInProgress || party.StartTransitionNextFrameToExitFromPort ||
                !party.HasLandNavigationCapability)
                return false;
            if (party.CurrentSettlement == target)
                return true;
            return CanPathLand(party, party.Position, target.GatePosition);
        }

        // Can also be quoted from a planned fort gate without moving the party.
        internal static bool CanPathLand(MobileParty party, CampaignVec2 from, CampaignVec2 to)
        {
            if (!from.IsOnLand || !to.IsOnLand || !from.Face.IsValid() || !to.Face.IsValid())
                return false;
            var campaign = NativeCampaign.Current;
            var navigation = campaign.Models.PartyNavigationModel;
            var mode = MobileParty.NavigationType.Default;
            var terrain = campaign.MapSceneWrapper.GetFaceTerrainType(to.Face);
            return navigation.IsTerrainTypeValidForNavigationType(terrain, mode) &&
                navigation.IsTerrainTypeValidForNavigationType(terrain, party.NavigationCapability) &&
                (from.Face.FaceIndex == to.Face.FaceIndex ||
                    campaign.MapSceneWrapper.GetPathBetweenAIFaces(from.Face, to.Face, from.ToVec2(), to.ToVec2(), 0.3f,
                        new NavigationPath(), navigation.GetInvalidTerrainTypesForNavigationType(mode), 2f,
                        party.GetRegionSwitchCostFromSeaToLand(), party.GetRegionSwitchCostFromLandToSea()));
        }

        internal static bool CanRouteToRelief(MobileParty leader, CampaignVec2 from, CampaignVec2 to) =>
            leader.HasLandNavigationCapability && !leader.IsCurrentlyAtSea && !leader.IsInRaftState &&
            !leader.IsTransitionInProgress && !leader.StartTransitionNextFrameToExitFromPort && CanPathLand(leader, from, to);

        internal static bool HasDefendOrder(Army army, Settlement target) =>
            army.AiBehaviorObject == target && army.LeaderParty.DefaultBehavior == AiBehavior.DefendSettlement &&
            army.LeaderParty.TargetSettlement == target;

        internal static string? VisitLock(MobileParty leader, bool held = false, Army? afterRelease = null, bool ignoreFleeing = false)
        {
            if (leader.IsCurrentlyUsedByAQuest ||
                (leader.AttachedTo != null && leader.AttachedTo != afterRelease?.LeaderParty) ||
                leader.Ai == null || leader.Ai.DoNotMakeNewDecisions)
                return "party-control-lock";
            if (leader.IsTransitionInProgress || leader.StartTransitionNextFrameToExitFromPort)
                return "map-transition";
            if (leader.Ai.IsDisabled || (!ignoreFleeing && leader.IsFleeing()) ||
                leader.ShortTermBehavior is AiBehavior.EngageParty or AiBehavior.JoinParty ||
                (held && leader.ShortTermBehavior is not (AiBehavior.Hold or AiBehavior.None)))
                return "native-movement-lock";
            if (leader.IsCurrentlyAtSea || leader.IsInRaftState || !leader.HasLandNavigationCapability)
                return "not-land-ready";
            return null;
        }

        internal static void Defend(Army army, Settlement target) =>
            SetPartyAiAction.GetActionForDefendingSettlement(army.LeaderParty, target, MobileParty.NavigationType.Default, false, false);

        internal static void Besiege(Army army, Settlement target) =>
            SetPartyAiAction.GetActionForBesiegingSettlement(army.LeaderParty, target, MobileParty.NavigationType.Default, false);

        internal static bool HasSiegeOrder(Army army, Settlement target) =>
            army.AiBehaviorObject == target && army.LeaderParty.TargetSettlement == target &&
            army.LeaderParty.DefaultBehavior is AiBehavior.BesiegeSettlement or AiBehavior.AssaultSettlement;

        internal static string Sustain(Army army, string assignment, CampaignLog log)
        {
            var leader = army.LeaderParty;
            float before = army.Cohesion;
            if (!(before > 0f && before < 50f) || army.IsDispersing ||
                army.Kingdom is not Kingdom kingdom || !kingdom.Armies.Contains(army) ||
                leader.Army != army || !CanDirect(leader, kingdom) || Members(army).Any(p => p.IsMainParty))
                return "ineligible";
            var payer = leader.LeaderHero.Clan;
            var model = NativeCampaign.Current.Models.ArmyManagementCalculationModel;
            int Price(float gain) => model.CalculateTotalInfluenceCost(army, gain);
            float balance = payer.Influence;
            float full = 100f - before;
            int fullCost = Price(full);
            // Price the actual complete refill, not an impossible 100-point gain.
            float gain = Affordable(balance, fullCost) ? full :
                fullCost > 0 && balance > InfluenceReserve ? FindAffordableGain(full, balance, Price) : 0f;

            int cost = gain == full ? fullCost : gain > 0f ? Price(gain) : 0;
            // Recheck the actual quoted amount and live payer before the native
            // action, which itself performs the debit and applies the gain.
            balance = payer.Influence;
            string mode = gain == full ? "full" : "partial";
            if (gain <= 0f || before + gain <= before || cost < 0 || (cost == 0 && mode == "partial") ||
                !Affordable(balance, cost) || payer != leader.LeaderHero?.Clan || army.Cohesion != before ||
                army.Kingdom != kingdom || leader.Army != army)
            {
                string reason = fullCost < 0 ? "invalid-native-price" : "no-affordable-positive-refill";
                if (log.Changed("upkeep", CampaignLog.ArmyId(army), $"{assignment}/{reason}"))
                    log.Event($"Upkeep skipped: {CampaignLog.ArmyState(army)} assignment={assignment} reason={reason} payer={payer.StringId} " +
                        $"balance={CampaignLog.Number(balance)} reserve={InfluenceReserve} budget={CampaignLog.Number(Math.Max(0f, balance - InfluenceReserve))} " +
                        $"fullCost={fullCost} cohesionBefore={CampaignLog.Number(before)}.");
                return reason;
            }
            log.Changed("upkeep", CampaignLog.ArmyId(army), null);
            army.BoostCohesionWithInfluence(gain, cost);
            log.Event($"Upkeep {mode}: {CampaignLog.ArmyState(army)} assignment={assignment} payer={payer.StringId} " +
                $"reserve={InfluenceReserve} budget={CampaignLog.Number(Math.Max(0f, balance - InfluenceReserve))} " +
                $"fullCost={fullCost} nativeCost={cost} requestedGain={CampaignLog.Number(gain)} " +
                $"balanceBefore={CampaignLog.Number(balance)} balanceAfter={CampaignLog.Number(payer.Influence)} " +
                $"actualDebit={CampaignLog.Number(balance - payer.Influence)} cohesionBefore={CampaignLog.Number(before)} " +
                $"cohesionAfter={CampaignLog.Number(army.Cohesion)} actualGain={CampaignLog.Number(army.Cohesion - before)}.");
            return mode;
        }

        // Installed model is a nondecreasing, integer-valued price curve. Do
        // not turn a rounded-zero portion into recurring free maintenance.
        private static float FindAffordableGain(float full, float balance, Func<float, int> price)
        {
            float low = 0f, high = full;
            for (int i = 0; i < 32; ++i)
            {
                float mid = low + (high - low) * 0.5f;
                if (mid == low || mid == high)
                    break;
                int cost = price(mid);
                if (cost < 0)
                    return 0f;
                if (Affordable(balance, cost))
                    low = mid;
                else
                    high = mid;
            }
            return price(low) > 0 ? low : 0f;
        }

        internal static bool ReleaseMember(Army army, MobileParty party, CampaignLog log, string reason)
        {
            if (army.LeaderParty.IsMainParty || party.IsMainParty || party == army.LeaderParty ||
                party.Army != army || ReleaseBlocker(party) != null)
                return false;
            party.Army = null;
            ResumeVanilla(party, army.LeaderParty);
            log.Event($"Muster member released: army={CampaignLog.ArmyId(army)} party={party.StringId} reason={reason} " +
                $"stillMember={party.Army == army} attached={party.AttachedTo == army.LeaderParty} order={party.DefaultBehavior}.");
            return party.Army != army;
        }

        // Returns why cancellation still has to wait, or null once reconciled.
        internal static string? CancelSurplus(Army army, MobileParty party, bool registered, CampaignLog log)
        {
            var leader = army.LeaderParty;
            if (party.Army != army || !registered)
            {
                // Native removal/transfer can win the race. Never remove from a
                // successor army, but clear our own escort on a free party.
                if (party.Army == null && !party.IsMainParty && party.IsActive && HasEscort(party, leader))
                {
                    if (ReleaseBlocker(party) is string lockReason)
                        return $"{lockReason}; membership ended, escort cleanup pending";
                    ResumeVanilla(party, leader);
                }
                log.Event($"Muster surplus reconciled: army={CampaignLog.ArmyId(army)} party={party.StringId} " +
                    $"registered={registered} stillMember={party.Army == army} order={party.DefaultBehavior}; no membership change.");
                return null;
            }
            if (ReleaseBlocker(party) is string blocker)
                return blocker;
            if (party.AttachedTo == leader && Members(army).Any(p => (p == leader || p.AttachedTo == leader) && ReleaseBlocker(p) != null))
                return "attached-to-formation-in-native-event";
            return ReleaseMember(army, party, log, "not-needed-for-departure") ? null : "native-release-unavailable";
        }

        internal static bool Release(Army army, CampaignLog log, string reason)
        {
            if (!CanRelease(army))
                return false;
            var parties = Members(army);
            Releasing = army;
            try { DisbandArmyAction.ApplyByUnknownReason(army); }
            finally { Releasing = null; }
            foreach (var party in parties.Where(party => party.Army == null))
                ResumeVanilla(party, army.LeaderParty);
            bool released = army.Kingdom == null && parties.All(party => party.Army != army);
            log.Event($"Muster release: army={CampaignLog.ArmyId(army)} reason={reason} complete={released} " +
                $"parties=[{string.Join(", ", parties.Select(party => $"{party.StringId}:stillMember={party.Army == army}:order={party.DefaultBehavior}"))}].");
            return released;
        }

        internal static string? SiegeWithdrawalBlocker(Army army, Settlement target)
        {
            var leader = army.LeaderParty;
            var camp = leader.BesiegerCamp;
            if (camp == null)
                return CanRelease(army) ? null : "native-or-player-lock";
            if (camp.SiegeEvent != target.SiegeEvent || camp.SiegeEvent.BesiegedSettlement != target || !HasSiegeOrder(army, target))
                return "not-the-owned-siege-order";

            var members = Members(army);
            var campParties = camp.GetInvolvedPartiesForEventType().ToArray();
            if (members.Any(p => p.IsMainParty || PlayerEncounter.EncounteredMobileParty == p) ||
                campParties.Any(p => p.MobileParty?.IsMainParty == true || p.MobileParty?.Army?.LeaderParty.IsMainParty == true) ||
                MobileParty.MainParty?.CurrentSettlement == target)
                return "player-protection";
            if (target.Party.MapEvent != null || campParties.Any(p => p.MapEvent != null) || members.Any(p => p.MapEvent != null))
                return "native-battle";
            if (army.IsDispersing || !campParties.Any(p => p == leader.Party) ||
                members.Any(p => p.Army != army || p.AttachedParties.Any(child => !members.Contains(child)) ||
                    p.CurrentSettlement?.IsUnderSiege == true || (p.BesiegerCamp != null && p.BesiegerCamp != camp) ||
                    p.IsTransitionInProgress || p.StartTransitionNextFrameToExitFromPort || p.IsCurrentlyUsedByAQuest ||
                    p.Ai?.DoNotMakeNewDecisions == true || p.Ai?.IsDisabled == true || p.IsFleeing()))
                return "inconsistent-or-other-native-commitment";
            return null;
        }

        // Remove only our formation. The native setter transfers leadership or
        // finalizes an empty camp; never finalize or remove other forces here.
        internal static string? WithdrawCancelledSiege(Army army, Settlement target, CampaignLog log)
        {
            if (SiegeWithdrawalBlocker(army, target) is string blocker)
                return blocker;
            var leader = army.LeaderParty;
            var camp = leader.BesiegerCamp;
            if (camp == null)
                return null;
            var members = Members(army);
            var leaving = members.Where(p => p.BesiegerCamp == camp).ToArray();
            // Clear the long- and short-term order before removing anyone. A
            // surviving camp must not cause re-entry on the next native tick.
            army.FinishArmyObjective();
            foreach (var party in leaving.Where(p => p != leader))
                party.BesiegerCamp = null;
            leader.BesiegerCamp = null;
            log.Changed("siege-cancel", CampaignLog.ArmyId(army), null);
            log.Event($"Assignment withdrawal: {CampaignLog.ArmyState(army)} target={target.StringId} " +
                $"departed=[{string.Join(", ", leaving.Where(p => p.BesiegerCamp != camp).Select(p => p.StringId))}] " +
                $"campRemaining={camp.GetInvolvedPartiesForEventType().Count()} settlementStillBesieged={target.IsUnderSiege}.");
            return leaving.All(p => p.BesiegerCamp != camp) ? null : "native-camp-removal-incomplete";
        }

        private static void ResumeVanilla(MobileParty party, MobileParty leader)
        {
            if (party.IsMainParty || !party.IsActive || party.Army != null)
                return;
            if (HasEscort(party, leader))
                party.SetMoveModeHold();
            party.Ai.RethinkAtNextHourlyTick = true;
        }

        private static bool HasEscort(MobileParty party, MobileParty leader) =>
            party.DefaultBehavior == AiBehavior.EscortParty && party.TargetParty == leader;
    }
}
