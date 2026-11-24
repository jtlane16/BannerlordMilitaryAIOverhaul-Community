using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using BannerlordMilitaryAIOverhaul.Diagnostics;

namespace BannerlordMilitaryAIOverhaul
{
    // Player-tunable values from _Module/Settings.xml, read at each campaign start.
    // A missing file or invalid value keeps the built-in default.
    internal static class ModSettings
    {
        private const string FileName = "Settings.xml";
        private const double DefaultStayHoursAfterSiege = 0d;

        internal static double StayHoursAfterSiege { get; private set; } = DefaultStayHoursAfterSiege;

        internal static void Load()
        {
            StayHoursAfterSiege = DefaultStayHoursAfterSiege;
            var bin = Path.GetDirectoryName(typeof(ModSettings).Assembly.Location)!;
            var path = Path.Combine(Directory.GetParent(bin)?.Parent?.FullName ?? bin, FileName);
            if (!File.Exists(path))
            {
                CampaignLog.Write($"Settings: {FileName} not found; using defaults.");
                return;
            }
            try
            {
                var root = XDocument.Load(path).Root;
                StayHoursAfterSiege = Hours(root?.Element(nameof(StayHoursAfterSiege))?.Attribute("value")?.Value, DefaultStayHoursAfterSiege);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
            {
                CampaignLog.Write($"Settings: {FileName} could not be read ({error.Message}); using defaults.");
                return;
            }
            CampaignLog.Write($"Settings: StayHoursAfterSiege={CampaignLog.Number(StayHoursAfterSiege)}.");
        }

        private static double Hours(string? text, double fallback) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            value >= 0d && !double.IsInfinity(value) ? value : fallback;
    }
}