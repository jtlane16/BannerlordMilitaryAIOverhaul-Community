using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        private const double CrossTargetRebuyHours = 8d;

        private sealed class CrossTargetRebuy
        {
            public string Kingdom = "", Target = "", Attempt = "";
            public double Until;
            public HashSet<string> Escorts = new(StringComparer.Ordinal);
        }

        private List<CrossTargetRebuy> _crossTargetRebuys = new();

        private void SyncCrossTargetRebuys(IDataStore store)
        {
            string? value = store.IsSaving ? JsonConvert.SerializeObject(_crossTargetRebuys) : null;
            store.SyncData("siegeCrossTargetRebuys", ref value);
            if (!store.IsLoading)
                return;
            try
            {
                _crossTargetRebuys = value == null ? new() :
                    JsonConvert.DeserializeObject<List<CrossTargetRebuy>>(value) ?? new();
            }
            catch (JsonException error)
            {
                _crossTargetRebuys.Clear();
                _log.Event($"Siege cross-target purchase metadata could not load: {error.Message}.");
            }
        }

        private void RememberCrossTargetRebuy(Army army, SiegeAssignment job, double now, string reason)
        {
            if (job.State.PaidEscorts is not { Count: > 0 } paid || reason is not (
                "muster-projected-force-no-longer-viable" or "march-no-longer-meets-native-strength" or
                "paid-retry-attached-strength-unattainable" or "paid-retry-assembly-deadline" or
                "muster-no-observed-progress" or "cleared-muster-order-not-recoverable" or
                "cleared-siege-order-not-recoverable" or "muster-or-march-route-lost" or
                "march-land-path-lost" or "native-besiege-order-not-issued" or "visible-overwhelming-threat-before-camp"))
                return;
            var record = new CrossTargetRebuy
            {
                Kingdom = job.State.Kingdom, Target = job.Target.StringId, Attempt = job.State.AttemptId!,
                Until = now + CrossTargetRebuyHours, Escorts = new HashSet<string>(paid, StringComparer.Ordinal)
            };
            _crossTargetRebuys.Add(record);
            _log.Event($"Siege cross-target purchase guard: {SiegeId(army, job)} attempt={record.Attempt} " +
                $"paidEscorts={record.Escorts.Count} until={record.Until:R}.");
        }

        private CrossTargetRebuy[] ActiveCrossTargetRebuys(Kingdom kingdom, double now) =>
            _crossTargetRebuys.Count == 0 ? Array.Empty<CrossTargetRebuy>() :
                _crossTargetRebuys.Where(record => record.Kingdom == kingdom.StringId && now < record.Until).ToArray();

        private static CrossTargetRebuy? MatchingRebuy(CrossTargetRebuy[] records, Settlement target, IEnumerable<string> escorts)
        {
            if (records.Length == 0)
                return null;
            var distinct = escorts as HashSet<string> ?? new HashSet<string>(escorts, StringComparer.Ordinal);
            if (distinct.Count == 0)
                return null;
            foreach (var record in records)
            {
                if (record.Target == target.StringId || record.Escorts.Count == 0)
                    continue;
                int shared = distinct.Count(record.Escorts.Contains);
                if (shared * 2 > distinct.Count && shared * 2 > record.Escorts.Count)
                    return record;
            }
            return null;
        }

        private void LogBlockedCrossTargetRebuy(Kingdom kingdom, Settlement target, CrossTargetRebuy record, string stage) =>
            _log.Event($"Siege cross-target purchase blocked: kingdom={kingdom.StringId} target={target.StringId} " +
                $"previousTarget={record.Target} previousAttempt={record.Attempt} stage={stage} until={record.Until:R}.");
    }
}
