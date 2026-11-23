using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        private const double PaidSiegeStandDownHours = 48d;
        private const double SiegeMusterStallHours = KingdomCommander.SiegeRecoveryMusterHours;

        private enum PaidSiegeFailureKind { Unverified, Strength, Gathering, RecoveryFailed }

        private sealed class PaidSiegeFailure
        {
            public string Owner = "", AttemptId = "";
            public PaidSiegeFailureKind Kind;
            public double Until;
            public PreCampFailure? Strength;
        }

        private Dictionary<string, PaidSiegeFailure> _paidSiegeFailures = new(StringComparer.Ordinal);

        private void SyncPaidSiegeFailures(IDataStore store)
        {
            string? value = store.IsSaving ? JsonConvert.SerializeObject(_paidSiegeFailures) : null;
            store.SyncData("siegePaidFailureEpisodes", ref value);
            if (!store.IsLoading)
                return;
            try
            {
                _paidSiegeFailures = value == null ? new(StringComparer.Ordinal) :
                    JsonConvert.DeserializeObject<Dictionary<string, PaidSiegeFailure>>(value) ?? new(StringComparer.Ordinal);
            }
            catch (JsonException error)
            {
                _paidSiegeFailures.Clear();
                _log.Event($"Siege paid retry metadata could not load: {error.Message}.");
            }
        }

        private void ClearPaidSiegeOnOwnerChange(Settlement target)
        {
            string suffix = "/" + target.StringId;
            foreach (var key in _paidSiegeFailures.Keys.Where(key => key.EndsWith(suffix, StringComparison.Ordinal) &&
                _paidSiegeFailures[key].Owner != target.MapFaction?.StringId).ToArray())
                _paidSiegeFailures.Remove(key);
        }

        private void RecordPaidSummons(Army army, SiegeAssignment job, MobileParty escort, float amount)
        {
            var state = job.State;
            bool first = state.PaidSummons == 0f;
            state.PaidSummons += amount;
            (state.PaidEscorts ??= new HashSet<string>(StringComparer.Ordinal)).Add(escort.StringId);
            if (first)
                _log.Event($"Siege paid attempt: {SiegeId(army, job)} attempt={state.AttemptId} " +
                    $"recovery={state.PaidRecovery} firstDebit={CampaignLog.Number(amount)} " +
                    $"assemblyDeadline={(state.RecoveryDeadline > 0d ? state.RecoveryDeadline.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-")}.");
        }

        // Called from native dispersal, lost authority, and ordinary close.
        // The same attempt may be observed by more than one of these paths.
        private void FinishPaidSiege(Army army, SiegeAssignment job, double now, string reason)
        {
            var state = job.State;
            if (state.PaidFinalized || !(state.PaidSummons > 0f) || state.AttemptId == null)
                return;
            state.PaidFinalized = true;
            if (state.Issued && !state.OwnerEpochEnded && state.CampHour < 0 && InCamp(army, job.Target) is { } camp)
                ObserveNativeCamp(army, job, camp);
            if (state.CampHour >= 0 || state.Captured || state.OwnerEpochEnded ||
                job.Target.MapFaction?.StringId != state.Owner)
                return;
            if (reason is "settlement-owner-changed" or "no-longer-at-war-or-eligible" or "repulse-cooldown" or KingdomCommander.OutsideSiegeFrontier)
                return;
            var kingdom = army.Kingdom;
            if (kingdom?.StringId != state.Kingdom)
                kingdom = Kingdom.All.FirstOrDefault(candidate => candidate.StringId == state.Kingdom);
            if (kingdom == null || !MilitaryObjective.EnemyFortification(job.Target, kingdom))
                return;

            string key = SiegeKey(state.Kingdom, job.Target);
            if (_paidSiegeFailures.TryGetValue(key, out var prior) && prior.AttemptId == state.AttemptId)
                return;
            var kind = state.PaidRecovery ? PaidSiegeFailureKind.RecoveryFailed :
                state.ObservedStrengthFailure != null ? PaidSiegeFailureKind.Strength :
                reason is "muster-no-observed-progress" or "cleared-muster-order-not-recoverable" ? PaidSiegeFailureKind.Gathering :
                PaidSiegeFailureKind.Unverified;
            _paidSiegeFailures[key] = new PaidSiegeFailure
            {
                Owner = state.Owner, AttemptId = state.AttemptId, Kind = kind,
                Until = now + PaidSiegeStandDownHours, Strength = state.ObservedStrengthFailure
            };
            RememberCrossTargetRebuy(army, job, now, reason);
            _log.Event($"Siege paid pre-camp failure: {SiegeId(army, job)} attempt={state.AttemptId} reason={reason} " +
                $"paid={CampaignLog.Number(state.PaidSummons)} recovery={state.PaidRecovery} policy={kind} " +
                $"failedCheckPower={CampaignLog.Number(state.ObservedStrengthFailure?.FailedPower ?? 0f)} " +
                $"committedPower={CampaignLog.Number(state.CommittedPower)} reviewAfter={now + PaidSiegeStandDownHours:R}.");
        }

        private (KingdomCommander.FreshSiegeRules rules, int work, bool hasFailedStrength,
            bool weakerDefenses, PaidSiegeFailure? episode) ReadFreshSiegePolicy(Kingdom kingdom, Settlement target, double now)
        {
            string key = SiegeKey(kingdom.StringId, target);
            string owner = target.MapFaction.StringId;
            _paidSiegeFailures.TryGetValue(key, out var episode);
            if (episode != null && (episode.Owner != owner || now >= episode.Until))
                episode = null;
            _preCampFailures.TryGetValue(key, out var strength);
            if (strength?.Owner != owner)
                strength = null;
            var episodeStrength = episode?.Strength;
            var evidence = episode?.Kind == PaidSiegeFailureKind.Strength ? episodeStrength : strength;
            if (!(evidence?.FailedPower > 0f))
                evidence = null; // older saves have only the paid commitment, not the failed check

            float floor = 0f;
            int work = 0;
            bool weaker = false;
            if (strength != null || episodeStrength != null)
            {
                var (garrison, militia, mobile) = SiegeDefenders(target);
                float defenders = garrison + militia + mobile;
                int wall = target.Town.GetWallLevel();
                float food = Math.Min(100f, target.Town.FoodStocks);
                bool Weak(PreCampFailure snapshot) => WeakerSiegeDefenses(snapshot, defenders, wall, food);
                if (strength != null && !Weak(strength))
                    floor = StrongerRetryPower(strength.CommittedPower);
                if (episodeStrength != null && !Weak(episodeStrength))
                    floor = Math.Max(floor, StrongerRetryPower(episodeStrength.CommittedPower));
                weaker = floor == 0f;
                work = 1;
            }

            // An assignment already marked for closure can be waiting on a
            // native lock. Do not buy another before its result is recorded.
            string? paidBlock = _sieges.Values.Any(job => job.Target == target && job.State.Kingdom == kingdom.StringId &&
                job.State.Owner == owner && !job.State.OwnerEpochEnded && job.State.End != null &&
                job.State.PaidSummons > 0f && !job.State.PaidFinalized) ? "paid-siege-close-pending" : episode?.Kind switch
                {
                    PaidSiegeFailureKind.RecoveryFailed => "paid-siege-stand-down",
                    PaidSiegeFailureKind.Unverified => "paid-siege-unverified-recovery",
                    _ => null
                };
            float paidFloor = episode?.Kind == PaidSiegeFailureKind.Strength && evidence != null ?
                StrongerRetryPower(evidence.FailedPower) : 0f;
            return (new KingdomCommander.FreshSiegeRules(floor, paidFloor, paidBlock, episode?.Kind == PaidSiegeFailureKind.Gathering),
                work, evidence != null, weaker, episode);
        }
    }
}
