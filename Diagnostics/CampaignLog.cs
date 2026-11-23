using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul.Diagnostics
{
    internal sealed class CampaignLog
    {
        private const string FileName = "BannerlordMilitaryAIOverhaul.log";
        private static readonly AccessTools.FieldRef<Army, CampaignTime> CreationTime =
            AccessTools.FieldRefAccess<Army, CampaignTime>("_creationTime");
        private static CampaignLog? _current;
        private readonly NativeCampaign _campaign;
        private readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 8);
        private readonly Dictionary<(string source, string subject), string> _last = new();

        private CampaignLog(NativeCampaign campaign) => _campaign = campaign;

        internal static CampaignLog? Current => _current?._campaign == NativeCampaign.Current ? _current : null;

        internal static CampaignLog Open(NativeCampaign campaign) => _current = new CampaignLog(campaign);

        internal static void Close(NativeCampaign campaign)
        {
            if (_current?._campaign == campaign)
                _current = null;
        }

        internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        internal static string PointId(IMapPoint? point) => point switch
        {
            Settlement settlement => settlement.StringId,
            MobileParty party => party.StringId,
            _ => point?.Name?.ToString() ?? "-"
        };

        internal static string ArmyId(Army army) =>
            $"{army.LeaderParty.StringId}@{CreationTime(army).ToHours.ToString("R", CultureInfo.InvariantCulture)}";

        internal static string ArmyState(Army army) =>
            $"kingdom={army.Kingdom?.StringId ?? "-"} army={ArmyId(army)} " +
            $"order={army.LeaderParty.DefaultBehavior} objective={PointId(army.AiBehaviorObject)} cohesion={Number(army.Cohesion)}";

        // Dedupe the substance of a decision, not fluctuating scores and balances.
        internal bool Changed(string source, string subject, string? state)
        {
            var key = (source, subject);
            if (state == null)
                return _last.Remove(key);
            if (_last.TryGetValue(key, out var prior) && state == prior)
                return false;
            _last[key] = state;
            return true;
        }

        internal void Event(string message) => Write(Context() + message);

        internal void Events(IEnumerable<string> messages)
        {
            string lines;
            using (CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.ReportBuild))
            {
                string context = Context();
                lines = string.Concat(messages.Select(message => Format(context + message)));
            }
            using (CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.ReportAppend))
                Append(lines);
        }

        private string Context() => $"session={_session} campaignHour={Number(CampaignTime.Now.ToHours)} ";

        internal static void Write(string message)
        {
            string line = Format(message);
            using (CampaignPerformanceCapture.Measure(CampaignPerformanceCapture.Work.LogAppend))
                Append(line);
        }

        private static string Format(string message) =>
            $"[{DateTimeOffset.Now:O}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";

        private static void Append(string lines)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (desktop.Length != 0 && lines.Length != 0)
                    File.AppendAllText(Path.Combine(desktop, FileName), lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
