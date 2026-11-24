using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Command;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        private const double IdleGraceHours = 24d;
        private sealed class IdlePeriod
        {
            public string Kingdom = "";
            public string? FailedMusterKey, FailedReliefKey;
            public double Until;
            public HashSet<string>? Abandoned;
            public string? StayAt;
            public double StayUntil;
        }
        private readonly Dictionary<Army, IdlePeriod> _idle = new();

        private void SyncIdle(IDataStore store)
        {
            if (store.IsSaving && _idle.Count == 0)
                return;
            var entries = store.IsSaving ? _idle.Where(p => Registered(p.Key)).ToArray() :
                Array.Empty<KeyValuePair<Army, IdlePeriod>>();
            var armies = entries.Select(p => p.Key).ToList();
            string? json = store.IsSaving ? JsonConvert.SerializeObject(entries.Select(p => p.Value)) : null;
            store.SyncData("idleGraceArmies", ref armies);
            store.SyncData("idleGracePeriods", ref json);
            if (!store.IsLoading)
                return;
            _idle.Clear();
            IdlePeriod[]? periods;
            try { periods = json == null ? null : JsonConvert.DeserializeObject<IdlePeriod[]>(json); }
            catch (JsonException error)
            {
                _log.Event($"Idle grace metadata could not load: {error.Message}.");
                return;
            }
            if (periods == null || armies?.Count != periods.Length)
                return;
            for (int i = 0; i < periods.Length; i++)
                if (armies[i] != null && periods[i]?.Until > 0 && !double.IsInfinity(periods[i].Until) &&
                    !_relief.ContainsKey(armies[i]) && !_sieges.ContainsKey(armies[i]))
                    _idle[armies[i]] = periods[i];
        }

        // Only a closing assignment may enter grace; never infer it from Hold.
        private bool BeginIdle(Army army, double now, string reason, HashSet<string>? abandoned = null,
            string? failedMusterKey = null, string? failedReliefKey = null, Settlement? stayAt = null)
        {
            if (!Registered(army) || OwnsControlledFixture(army) || ArmyTakeoverBehavior.Current?.IsPending(army) == true ||
                !ArmyOrders.CanRelease(army) || army.LeaderParty.BesiegerCamp != null || UnsustainableIdle(army))
                return false;
            army.FinishArmyObjective(); // native Hold + clears the objective; does not change membership
            _sieges.Remove(army);
            _relief.Remove(army);
            if (!_idle.ContainsKey(army))
            {
                double stay = stayAt == null ? 0d : ModSettings.StayHoursAfterSiege;
                double until = now + stay + IdleGraceHours;
                _idle.Add(army, new IdlePeriod { Kingdom = army.Kingdom.StringId, Until = until,
                    Abandoned = abandoned, FailedMusterKey = failedMusterKey, FailedReliefKey = failedReliefKey,
                    StayAt = stayAt?.StringId, StayUntil = now + stay });
                _log.Event($"Army idle grace started: kingdom={army.Kingdom.StringId} army={CampaignLog.ArmyId(army)} ended={reason} until={until:R}" +
                           (stayAt == null ? "." : $" stayAt={stayAt.StringId} stayUntil={now + stay:R}; offense waits, defense may still call."));
            }
            return true;
        }

        // Hold a capturing army in place before its one-ring follow-on.
        private bool BeginPostCaptureStay(Army army, SiegeAssignment old, double now) =>
            ModSettings.StayHoursAfterSiege > 0d &&
            BeginIdle(army, now, old.State.End ?? "native-capture", old.State.PreviousTargets, stayAt: old.Target);

        private static bool UnsustainableIdle(Army army)
        {
            var present = ArmyOrders.Members(army).Where(p => p == army.LeaderParty || p.AttachedTo == army.LeaderParty).ToArray();
            return !ArmyOrders.CanDirect(army.LeaderParty, army.Kingdom) || present.Count(p => p.Party.IsStarving) * 2 > present.Length;
        }

        private bool IdleTargetAllowed(Army army, Settlement target)
        {
            if (!_idle.TryGetValue(army, out var period))
                return true;
            if (period.StayAt != null)
                return false; // post-capture stay: no offense until it ends
            if (period.Abandoned?.Contains(target.StringId) != true)
                return true;
            // Don't bounce between the same failed objectives during grace.
            if (!_preCampFailures.TryGetValue(SiegeKey(period.Kingdom, target), out var failure))
                return false;
            if (failure.Owner != target.MapFaction?.StringId || KingdomCommander.SiegeRoster(army) >= StrongerRetryPower(failure.FailedPower))
                return true;
            var (g, m, p) = SiegeDefenders(target);
            return WeakerSiegeDefenses(failure, g + m + p, target.Town.GetWallLevel(), Math.Min(100f, target.Town.FoodStocks));
        }

        private void EndIdle(Army army, string reason)
        {
            if (_idle.Remove(army))
                _log.Event($"Army idle grace ended: army={(army.LeaderParty == null ? "-" : CampaignLog.ArmyId(army))} reason={reason}.");
        }

        private void ReviewIdle(double now)
        {
            if (_idle.Count == 0)
                return;
            foreach (var pair in _idle.ToArray())
            {
                var army = pair.Key;
                if (!Registered(army) || army.Kingdom.StringId != pair.Value.Kingdom || army.LeaderParty.IsMainParty ||
                    OwnsControlledFixture(army) || ArmyTakeoverBehavior.Current?.IsPending(army) == true ||
                    _sieges.ContainsKey(army) || _relief.ContainsKey(army))
                {
                    EndIdle(army, "formation-gone-or-authority-changed");
                    continue;
                }
                if (!ArmyOrders.CanRelease(army))
                    continue; // native/player lock does not reset the deadline
                if (!IdleReliefArmy(army))
                {
                    EndIdle(army, "external-order");
                    continue;
                }
                bool unsustainable = UnsustainableIdle(army);
                if (pair.Value.StayAt is string stayAt && now >= pair.Value.StayUntil)
                {
                    pair.Value.StayAt = null;
                    _log.Event($"Army post-capture stay ended: army={CampaignLog.ArmyId(army)} settlement={stayAt}.");
                    var captured = Settlement.All.FirstOrDefault(site => site.StringId == stayAt);
                    if (!unsustainable && (TryReadyDefense(army, now) ||
                                           (captured != null && TrySiegeFollowOn(army, captured, now, 0d))))
                        continue;
                }
                if (!unsustainable && now < pair.Value.Until)
                    continue;
                if (!unsustainable && TryAssignAtIdleExpiry(army, now))
                    continue;
                string reason = unsustainable ? "idle-unsustainable" : "idle-grace-expired";
                var kingdom = army.Kingdom;
                if (ArmyOrders.Release(army, _log, reason))
                {
                    if (pair.Value.FailedMusterKey is string failed)
                        RememberFailedMuster(kingdom, failed);
                    if (pair.Value.FailedReliefKey is string reliefKey &&
                        _activeRelief.Values.Any(j => j.Kingdom == kingdom &&
                            reliefKey.StartsWith($"{j.Target.StringId}|{kingdom.StringId}|", StringComparison.Ordinal)))
                        _failedRelief[reliefKey] = ReliefInputs(KingdomForces.Read(kingdom));
                    EndIdle(army, reason);
                }
            }
        }

        private bool TryAssignAtIdleExpiry(Army army, double now)
        {
            if (!EligibleSiegeArmy(army))
                return false;
            if (TryReadyDefense(army, now))
                return true;
            var kingdom = army.Kingdom;
            if (KingdomCommander.HasHomeSiege(kingdom))
                return false;
            var targets = kingdom.FactionsAtWarWith.Where(f => f != null && f != kingdom && !f.IsEliminated)
                .SelectMany(f => f.Fiefs).Select(t => t.Settlement);
            var choice = KingdomCommander.ChooseSiege(kingdom, targets.Where(t => IdleTargetAllowed(army, t)),
                new[] { army }, null, t => SiegeTargetBlocker(kingdom, t, now, army), _ => false, _ => { }, _ => { },
                (_, _, _) => false, priorCamp: t => PriorCamp(kingdom, t));
            return choice != null && StartSiege(kingdom, choice, now, "idle-grace-final-review");
        }
    }
}
