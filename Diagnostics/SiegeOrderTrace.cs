using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Campaign;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMilitaryAIOverhaul.Diagnostics
{
    // Optional observer only: no new orders, probes, schedule changes or save fields.
    internal sealed class SiegeOrderTrace
    {
        internal const string Marker = "trace-v1.armed";
        private const string PatchId = "BannerlordMilitaryAIOverhaul.SiegeOrderTrace";
        private const int CampaignHours = 32 * 24, MaxSubjects = 128, MaxHolds = 12, MaxReviews = 32;
        private static SiegeOrderTrace? _active;
        private readonly MilitaryCommandBehavior _command;
        private readonly CampaignLog _log;
        private readonly string _id;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<(Army, Settlement, string), Evidence> _subjects = new();
        private double? _until;
        private bool _ended, _subjectLimitReported;
        private int _holds, _reviews;

        private SiegeOrderTrace(MilitaryCommandBehavior command, CampaignLog log, Guid id) =>
            (_command, _log, _id) = (command, log, id.ToString("N"));

        internal readonly struct Subject
        {
            internal readonly Army Army;
            internal readonly Settlement Target, Waypoint;
            internal readonly string Attempt;
            internal readonly bool March;
            internal readonly int Reissues;
            internal Subject(Army army, Settlement target, Settlement waypoint, string attempt, bool march, int reissues) =>
                (Army, Target, Waypoint, Attempt, March, Reissues) = (army, target, waypoint, attempt, march, reissues);
            internal (Army, Settlement, string) Key => (Army, Target, Attempt);
        }

        private sealed class Evidence
        {
            internal int Holds, Reviews;
            internal double HoldDistance;
            internal string? LastReview;
        }

        private sealed class Hold
        {
            internal readonly SiegeOrderTrace Trace;
            internal readonly Subject Subject;
            internal readonly int Number;
            internal readonly string Before, Caller;
            internal Hold(SiegeOrderTrace trace, Subject subject, int number, string before, string caller) =>
                (Trace, Subject, Number, Before, Caller) = (trace, subject, number, before, caller);
        }

        internal static void Open(MilitaryCommandBehavior command, CampaignLog log)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length != 0)
                TryClaim(Path.Combine(local, "BannerlordMilitaryAIOverhaul", "SiegeOrderTrace"), command, log);
        }

        internal static bool TryClaim(string directory, MilitaryCommandBehavior command, CampaignLog log)
        {
            string marker = Path.Combine(directory, Marker);
            if (_active != null || !File.Exists(marker))
                return false;
            try
            {
                string[] text = File.ReadAllText(marker).Trim().Split(' ');
                if (text.Length != 2 || text[0] != "siege-order-v1" || !Guid.TryParseExact(text[1], "N", out var id))
                    return false;
                File.Move(marker, Path.Combine(directory, $"siege-order-v1.{id:N}.claimed"));
                var method = typeof(MobileParty).GetMethod(nameof(MobileParty.SetMoveModeHold),
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method == null || method.ReturnType != typeof(void))
                    throw new MissingMethodException(typeof(MobileParty).FullName, nameof(MobileParty.SetMoveModeHold));
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                new Harmony(PatchId).Patch(method,
                    prefix: new HarmonyMethod(typeof(SiegeOrderTrace).GetMethod(nameof(BeforeHold), flags)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(SiegeOrderTrace).GetMethod(nameof(AfterHold), flags)) { priority = Priority.Last });
                _active = new SiegeOrderTrace(command, log, id);
                _active.Write($"armed method=MobileParty.SetMoveModeHold mvid={method.Module.ModuleVersionId} " +
                    $"windowHours={CampaignHours} maxSubjects={MaxSubjects} maxHoldsPerSubject={MaxHolds}.");
                return true;
            }
            catch (Exception error)
            {
                _active = null;
                RemoveHook();
                log.Event($"Siege order trace unavailable: {error.GetType().Name}: {error.Message}; normal AI unchanged.");
                return false;
            }
        }

        internal static void Close()
        {
            var trace = _active;
            if (trace == null)
                return;
            _active = null;
            try { trace.Finish("campaign-ended"); }
            catch (Exception) { } // the campaign may already be unavailable to the log
            finally { RemoveHook(); }
        }

        private static void RemoveHook()
        {
            try { new Harmony(PatchId).UnpatchAll(PatchId); }
            catch (Exception) { } // a residual optional hook is inert without an active capture
        }

        internal static void Advance(MilitaryCommandBehavior command, double now)
        {
            if (_active is not { _ended: false } trace || trace._command != command)
                return;
            try
            {
                trace._until ??= now + CampaignHours;
                if (now >= trace._until)
                    trace.Finish("campaign-window-ended");
            }
            catch (Exception) { trace._ended = true; }
        }

        internal static bool Watches(MilitaryCommandBehavior command) =>
            _active is { _ended: false } trace && trace._command == command;

        private void Finish(string reason)
        {
            if (_ended)
                return;
            _ended = true;
            Write($"finished reason={reason} subjects={_subjects.Count} holds={_holds} reviews={_reviews}.");
            _subjects.Clear();
        }

        private static void BeforeHold(MobileParty __instance, out Hold? __state)
        {
            __state = null;
            if (_active is not { _ended: false } trace || Thread.CurrentThread.ManagedThreadId != trace._thread)
                return;
            try
            {
                if (!trace._command.TrySiegeTraceSubject(__instance, out var subject) ||
                    (__instance.DefaultBehavior == AiBehavior.Hold && __instance.ShortTermBehavior == AiBehavior.Hold &&
                        __instance.TargetSettlement == null && __instance.TargetParty == null &&
                        __instance.ShortTermTargetSettlement == null && __instance.ShortTermTargetParty == null))
                    return;
                if (!trace._subjects.TryGetValue(subject.Key, out var evidence))
                {
                    if (trace._subjects.Count >= MaxSubjects)
                    {
                        if (!trace._subjectLimitReported)
                            trace.Write("subject limit reached; not watching new assignments.");
                        trace._subjectLimitReported = true;
                        return;
                    }
                    trace._subjects.Add(subject.Key, evidence = new Evidence());
                }
                if (evidence.Holds >= MaxHolds)
                    return;

                string before = PartyState(__instance, subject);
                string caller = Callers();
                evidence.HoldDistance = Remaining(__instance, subject);
                evidence.LastReview = null;
                __state = new Hold(trace, subject, ++evidence.Holds, before, caller);
            }
            catch (Exception) { } // do not interrupt native Hold
        }

        private static void AfterHold(MobileParty __instance, Hold? __state)
        {
            if (__state == null || _active != __state.Trace)
                return;
            try
            {
                var trace = __state.Trace;
                trace._holds++;
                trace.Write($"hold {Describe(__state.Subject)} seq={__state.Number} " +
                    $"caller=[{__state.Caller}] before=[{__state.Before}] after=[{PartyState(__instance, __state.Subject)}].");
            }
            catch (Exception) { } // do not interrupt native Hold
        }

        internal static void Review(MilitaryCommandBehavior command, Subject subject, string decision,
            bool? route, bool? musterPath, string? visitLock, bool memberLock)
        {
            if (_active is not { _ended: false } trace || trace._command != command)
                return;
            try
            {
                if (!trace._subjects.TryGetValue(subject.Key, out var evidence) || evidence.Holds == 0 ||
                    evidence.Reviews >= MaxReviews)
                    return;
                var leader = subject.Army.LeaderParty;
                string? blocked = memberLock ? FirstReleaseBlocker(subject.Army) : null;
                bool restored = subject.March ? ArmyOrders.HasSiegeOrder(subject.Army, subject.Target) :
                    subject.Army.AiBehaviorObject == subject.Waypoint && leader.TargetSettlement == subject.Waypoint &&
                    leader.DefaultBehavior == AiBehavior.GoToSettlement;
                string shape = $"{subject.March}/{decision}/{subject.Reissues}/{visitLock}/{blocked}/{restored}";
                if (shape == evidence.LastReview)
                    return;
                evidence.LastReview = shape;
                evidence.Reviews++;
                trace._reviews++;
                trace.Write($"review {Describe(subject)} seq={evidence.Holds} decision={decision} " +
                    $"route={Check(route)} musterPath={Check(musterPath)} visitLock={visitLock ?? "-"} " +
                    $"releaseLock={blocked ?? (memberLock ? "blocked" : "-")} orderRestored={restored} " +
                    $"straightProgressSinceHold={CampaignLog.Number(evidence.HoldDistance - Remaining(leader, subject))} " +
                    $"state=[{PartyState(leader, subject)}].");
            }
            catch (Exception) { } // never make a review decision depend on tracing
        }

        private static string? FirstReleaseBlocker(Army army)
        {
            foreach (var member in ArmyOrders.Members(army))
                if (ArmyOrders.ReleaseBlocker(member) is string reason)
                    return $"{member.StringId}:{reason}";
            return null;
        }

        private static string Check(bool? value) => value == null ? "not-checked" : value.Value ? "pass" : "fail";
        private static string Describe(Subject subject) => $"army={CampaignLog.ArmyId(subject.Army)} target={subject.Target.StringId} " +
            $"attempt={subject.Attempt} phase={(subject.March ? "march" : "muster")} waypoint={subject.Waypoint.StringId} reissues={subject.Reissues}";
        private static double Remaining(MobileParty party, Subject subject) =>
            Math.Sqrt(party.Position.DistanceSquared(subject.Waypoint.GatePosition.ToVec2()));

        private static string PartyState(MobileParty party, Subject subject) =>
            $"default={party.DefaultBehavior} targetSettlement={party.TargetSettlement?.StringId ?? "-"} targetParty={party.TargetParty?.StringId ?? "-"} " +
            $"short={party.ShortTermBehavior} shortSettlement={party.ShortTermTargetSettlement?.StringId ?? "-"} shortParty={party.ShortTermTargetParty?.StringId ?? "-"} " +
            $"armyObjective={CampaignLog.PointId(subject.Army.AiBehaviorObject)} " +
            $"position=({CampaignLog.Number(party.Position.X)},{CampaignLog.Number(party.Position.Y)},{(party.Position.IsOnLand ? "land" : "sea")}) " +
            $"straightRemaining={CampaignLog.Number(Remaining(party, subject))} mapEvent={party.MapEvent?.EventType.ToString() ?? "-"} " +
            $"eventSite={party.MapEvent?.MapEventSettlement?.StringId ?? "-"} eventSide={(party.MapEvent == null ? "-" : party.Party.Side.ToString())}";

        private static string Callers()
        {
            var names = new List<string>();
            foreach (var frame in new StackTrace(false).GetFrames())
            {
                var method = frame.GetMethod();
                if (method == null || method.DeclaringType == typeof(SiegeOrderTrace))
                    continue;
                names.Add($"{method.DeclaringType?.FullName ?? "dynamic"}.{method.Name}");
                if (names.Count >= 10)
                    break;
            }
            return string.Join(" <- ", names);
        }

        private void Write(string line) => _log.Event($"Siege order trace: run={_id} {line}");
    }
}
