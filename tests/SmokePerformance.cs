using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

public static partial class SmokeFixtures
{
    private static void TestPerformance(Assembly mod, Harmony harmony, object sessionLog)
    {
        var capture = mod.GetType("BannerlordMilitaryAIOverhaul.Diagnostics.CampaignPerformanceCapture", true);
        var work = capture.GetNestedType("Work", BindingFlags.Public | BindingFlags.NonPublic);
        var measure = capture.GetMethod("Measure", flags);
        var active = capture.GetProperty("Active", flags);
        object Kind(string name) => Enum.Parse(work, name);
        var disabled = measure.Invoke(null, new[]{Kind("Relief")});
        Check(active.GetValue(null) == null && disabled.GetType().GetField("_capture", flags).GetValue(disabled) == null,
            "unarmed profiling returns a default timing token without starting a clock or opening a sink");

        string id = Guid.NewGuid().ToString("N");
        var text = new List<string>();
        object NewCapture(long frequency = 1000) =>
            Activator.CreateInstance(capture, flags, null, new object[]{id, frequency, 0L, new Action<string>(text.Add)}, null);
        object run = NewCapture();
        void At(long time, string mode = "fast", double hour = 555) =>
            capture.GetMethod("Boundary", flags).Invoke(run, new object[]{time, hour, mode});
        void WorkOn(string name, long milliseconds) =>
            capture.GetMethod("Add", flags).Invoke(run, new[]{Kind(name), (object)milliseconds});
        void Save() => capture.GetMethod("MarkSave", flags).Invoke(run, null);
        At(30, "excluded");
        At(80, "paused");
        Check(text.Count == 0, "loading and paused frames before active map play do not start collection");
        At(100);
        WorkOn("ReportAppend", 1000); At(124); // header-write interval, not usable
        At(146);
        WorkOn("Relief", 44); WorkOn("ReliefPlan", 38); WorkOn("LogAppend", 10);
        At(236);
        WorkOn("SiegePlan", 25); At(296);
        Save(); At(430);
        At(470, "paused"); At(480);
        At(3100); At(5101);
        string first = text.Single(line => line.StartsWith("window ", StringComparison.Ordinal));
        Check(first.Contains("samples=3") && first.Contains("maxMs=90") && first.Contains("excludedSave/Other/Gap/CaptureWrite=1/2/2/1") &&
            first.Contains("reliefInclusive=1/44/44/44") && first.Contains("reliefPlanSubset=1/38/38/38") &&
            first.Contains("logAppend=1/10/10/10") && first.Contains("siegePlan=1/25/25/0") && !first.Contains("reportAppend="),
            "the slowest callback interval reports its own inclusive work only; saves, gaps, pause and own writes are excluded and tagged");
        WorkOn("Relief", 9000); At(5251); // summary write contaminated this interval
        WorkOn("ReportBuild", 7); WorkOn("ReportAppend", 3); At(5281);
        At(5421); // slowest interval has no attributed mod work
        WorkOn("SiegePlan", 13); WorkOn("SiegeCommit", 12); At(5450);
        At(10301);
        string second = text.Last();
        Check(second.Contains("samples=3") && second.Contains("maxMs=140") && second.Contains("reportBuild=1/7/7/0") &&
            second.Contains("reportAppend=1/3/3/0") && second.Contains("siegeCommitSubset=1/12/12/0") &&
            !second.Contains("reliefInclusive="),
            "a slow interval with no measured work is not blamed on work from a different interval");
        At(120100);
        int finished = text.Count;
        At(120101); At(130000);
        Check(text.Last().Contains("reason=time-limit") && text.Count == finished,
            "the capture stops at its wall-clock timebox and does not emit further summaries");

        text.Clear();
        run = NewCapture();
        At(120000, "paused");
        At(120001);
        Check(text.Count == 1 && text[0].Contains("reason=no-active-map"),
            "the requested capture expires if the player never reaches a running map");

        text.Clear();
        run = NewCapture();
        for (long stamp = 100; stamp <= 120100; stamp += 1000) At(stamp);
        Check(text.Count(line => line.StartsWith("window ", StringComparison.Ordinal)) == 24 && text.Count == 26,
            "a complete capture produces at most 24 window rows plus its header and end");

        // Exercise the real two-path CampaignLog scopes. Smoke harness has
        // already replaced the Desktop append sink with an in-memory prefix.
        text.Clear();
        run = NewCapture();
        active.GetSetMethod(true).Invoke(null, new[]{run});
        var directWrite = sessionLog.GetType().GetMethod("Write", flags);
        var writeHook = typeof(SmokeFixtures).GetMethod("CaptureLog", flags);
        harmony.Unpatch(directWrite, writeHook); // leave the Append file-sink prefix in place
        try
        {
            At(100); At(120);
            sessionLog.GetType().GetMethod("Events", flags).Invoke(sessionLog, new object[]{new[]{"performance capture report fixture"}});
            sessionLog.GetType().GetMethod("Event", flags).Invoke(sessionLog, new object[]{"performance capture event fixture"});
            At(165); At(5200);
            string measured = text.Last();
            Check(measured.Contains("reportBuild=1/") && measured.Contains("reportAppend=1/") && measured.Contains("logAppend=1/"),
                "actual report formatting and one report and event append are timed separately without measuring the summary sink");
            At(120100);
            Check(active.GetValue(null) == null, "wall-time expiry removes the active capture from all instrumentation sites");
        }
        finally
        {
            harmony.Patch(directWrite, prefix: new HarmonyMethod(writeHook));
            active.GetSetMethod(true).Invoke(null, new object[]{null});
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "maio-perf-fixture-" + id)).FullName;
        object Claim() => capture.GetMethod("TryClaim", flags).Invoke(null, new object[]{folder});
        try
        {
            string arm = Path.Combine(folder, "capture-v1.armed");
            Check(Claim() == null && !Directory.EnumerateFiles(folder).Any(), "no marker means no profiling output");
            File.WriteAllText(arm, "bad-marker " + id);
            Check(Claim() == null && File.Exists(arm), "only a valid explicit performance marker starts a capture");
            File.WriteAllText(arm, "performance-v1 " + id);
            var claimed = Claim();
            Check(claimed != null && !File.Exists(arm) && Claim() == null &&
                File.Exists(Path.Combine(folder, "performance-v1." + id + ".claimed")),
                "a valid marker is atomically consumed once without an acceptance receipt or popup");
        }
        finally { Directory.Delete(folder, true); } // isolated directory created above
    }
}
