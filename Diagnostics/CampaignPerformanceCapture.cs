using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BannerlordMilitaryAIOverhaul.Diagnostics
{
    // Main-thread timing between entries to our MBSubModule application
    // callback. Nothing here estimates render/GPU time or native tick phases.
    internal sealed class CampaignPerformanceCapture
    {
        internal enum Work { Relief, ReliefPlan, SiegeReview, SiegePlan, ReportBuild, ReportAppend, LogAppend, Acceptance, SiegeCommit, Count }
        private static readonly string[] WorkNames = {
            "reliefInclusive", "reliefPlanSubset", "siegeReview", "siegePlan", "reportBuild", "reportAppend", "logAppend", "acceptanceInclusive", "siegeCommitSubset"
        };
        private const int LimitSeconds = 120, WindowSeconds = 5, GapSeconds = 2;
        internal const string Marker = "capture-v1.armed";
        internal static CampaignPerformanceCapture? Active { get; private set; }

        private readonly string _id;
        private readonly long _frequency, _waitUntil;
        private readonly Action<string> _write;
        private int _thread;
        private readonly long[] _current = new long[(int)Work.Count], _sum = new long[(int)Work.Count],
            _peak = new long[(int)Work.Count], _slowest = new long[(int)Work.Count];
        private readonly int[] _calls = new int[(int)Work.Count], _callsInWindow = new int[(int)Work.Count];
        private long _deadline, _windowAt, _boundary, _windowSlow, _total, _excludedMax, _writeCost, _slowIndex, _index;
        private int _samples, _over33, _over50, _over100, _save, _pauseOrOther, _gaps, _ownWrites;
        private double _hour, _slowHour;
        private string? _mode, _slowMode, _excludedReason;
        private bool _started, _anchored, _ownWrite, _ended;
        private volatile bool _saveCrossed;

        internal CampaignPerformanceCapture(string id, long frequency, long armedAt, Action<string> write) =>
            (_id, _frequency, _waitUntil, _write) = (id, frequency, armedAt + LimitSeconds * frequency, write);

        internal static CampaignPerformanceCapture? TryClaim(string directory)
        {
            // One probe on campaign start. Never poll the disk on a frame.
            var arm = Path.Combine(directory, Marker);
            if (!File.Exists(arm))
                return null;
            try
            {
                var text = File.ReadAllText(arm).Trim().Split(' ');
                if (text.Length != 2 || text[0] != "performance-v1" || !Guid.TryParseExact(text[1], "N", out var id))
                    return null;
                string prefix = Path.Combine(directory, $"performance-v1.{id:N}");
                if (File.Exists(prefix + ".log"))
                    return null;
                File.Move(arm, prefix + ".claimed");
                using (File.Open(prefix + ".log", FileMode.CreateNew, FileAccess.Write)) { }
                return new CampaignPerformanceCapture(id.ToString("N"), Stopwatch.Frequency, Stopwatch.GetTimestamp(),
                    lines => File.AppendAllText(prefix + ".log", lines));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static void Open()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Active = local.Length == 0 ? null : TryClaim(Path.Combine(local, "BannerlordMilitaryAIOverhaul", "Performance"));
        }

        internal static void Close()
        {
            Active?.Finish(Stopwatch.GetTimestamp(), "campaign-ended");
            Active = null;
        }

        internal static Timing Measure(Work work)
        {
            var capture = Active;
            return capture != null && capture._anchored && IsPlay(capture._mode) &&
                Thread.CurrentThread.ManagedThreadId == capture._thread ? new Timing(capture, work) : default;
        }

        internal readonly struct Timing : IDisposable
        {
            private readonly CampaignPerformanceCapture? _capture;
            private readonly Work _work;
            private readonly long _start;
            internal Timing(CampaignPerformanceCapture capture, Work work) =>
                (_capture, _work, _start) = (capture, work, Stopwatch.GetTimestamp());
            public void Dispose()
            {
                if (_capture != null)
                    _capture.Add(_work, Stopwatch.GetTimestamp() - _start);
            }
        }

        internal void Add(Work work, long elapsed)
        {
            if (!_anchored || _ended)
                return;
            _current[(int)work] += elapsed;
            _calls[(int)work]++;
        }

        internal static void SaveBoundary() { if (Active is { } capture) capture.MarkSave(); }
        internal void MarkSave() => _saveCrossed = true;
        private static bool IsPlay(string? mode) => mode == "normal" || mode == "fast";

        internal void Boundary(long stamp, double hour, string mode)
        {
            if (_ended)
                return;
            bool first = !_started;
            if (!_started)
            {
                if (stamp >= _waitUntil)
                {
                    Finish(stamp, "no-active-map");
                    return;
                }
                if (!IsPlay(mode))
                    return;
                _started = true;
                _thread = Thread.CurrentThread.ManagedThreadId;
                _deadline = stamp + LimitSeconds * _frequency;
                _windowAt = stamp;
                Publish($"performance-v1 run={_id} utc={DateTime.UtcNow:O} limitSeconds={LimitSeconds} windowSeconds={WindowSeconds} " +
                    "boundary=MAIO-OnApplicationTick-entry; proxy=wall pacing, not FPS; inclusive work not additive");
                if (_ended)
                    return;
            }
            if (stamp >= _deadline)
            {
                Finish(stamp, "time-limit"); // never include the unfinished interval crossing the bound
                return;
            }
            if (_anchored)
            {
                long elapsed = stamp - _boundary;
                _index++;
                if (_ownWrite) Exclude(elapsed, "capture-write", ref _ownWrites);
                else if (_saveCrossed || mode == "save" || _mode == "save") Exclude(elapsed, "save", ref _save);
                else if (!IsPlay(mode) || mode != _mode) Exclude(elapsed, "pause-menu-load-or-speed-change", ref _pauseOrOther);
                else if (elapsed >= GapSeconds * _frequency) Exclude(elapsed, "unclassified-gap", ref _gaps);
                else if (elapsed > 0) Sample(elapsed);
            }
            _anchored = true;
            _boundary = stamp;
            _mode = mode;
            _hour = hour;
            _saveCrossed = false;
            _ownWrite = first; // opening header was written at this boundary
            Array.Clear(_current, 0, _current.Length);
            Array.Clear(_calls, 0, _calls.Length);
            if (stamp - _windowAt >= WindowSeconds * _frequency)
                Flush(stamp);
        }

        private void Exclude(long elapsed, string reason, ref int count)
        {
            count++;
            if (elapsed > _excludedMax)
                (_excludedMax, _excludedReason) = (elapsed, reason);
        }

        private void Sample(long elapsed)
        {
            _samples++;
            _total += elapsed;
            if (elapsed >= _frequency * 33 / 1000) _over33++;
            if (elapsed >= _frequency * 50 / 1000) _over50++;
            if (elapsed >= _frequency / 10) _over100++;
            for (int i = 0; i < _current.Length; i++)
            {
                _sum[i] += _current[i];
                _callsInWindow[i] += _calls[i];
                _peak[i] = Math.Max(_peak[i], _current[i]);
            }
            if (elapsed > _windowSlow)
            {
                (_windowSlow, _slowIndex, _slowHour, _slowMode) = (elapsed, _index, _hour, _mode);
                Array.Copy(_current, _slowest, _current.Length);
            }
        }

        private string Ms(long ticks) => (1000d * ticks / _frequency).ToString("0.###", CultureInfo.InvariantCulture);

        private void Flush(long stamp)
        {
            if (_samples + _save + _pauseOrOther + _gaps + _ownWrites == 0)
                return;
            var work = new StringBuilder();
            for (int i = 0; i < WorkNames.Length; i++)
                if (_callsInWindow[i] != 0)
                    work.Append(' ').Append(WorkNames[i]).Append('=').Append(_callsInWindow[i]).Append('/')
                        .Append(Ms(_sum[i])).Append('/').Append(Ms(_peak[i])).Append('/').Append(Ms(_slowest[i]));
            Publish($"window endUtc={DateTime.UtcNow:O} wallMs={Ms(stamp - _windowAt)} samples={_samples} " +
                $"meanMs={Ms(_samples == 0 ? 0 : _total / _samples)} maxMs={Ms(_windowSlow)} ge33/50/100={_over33}/{_over50}/{_over100} " +
                $"slowInterval={_slowIndex} startCampaignHour={CampaignLog.Number(_slowHour)} mode={_slowMode ?? "-"} " +
                $"excludedSave/Other/Gap/CaptureWrite={_save}/{_pauseOrOther}/{_gaps}/{_ownWrites} excludedMaxMs={Ms(_excludedMax)} " +
                $"excludedMaxReason={_excludedReason ?? "-"} priorCaptureAppendMs={Ms(_writeCost)} " +
                $"work(count/totalMs/peakIntervalMs/onSlowIntervalMs):{(work.Length == 0 ? " none" : work.ToString())}");
            _windowAt = stamp;
            _windowSlow = _total = _excludedMax = _slowIndex = 0;
            _slowHour = 0;
            _slowMode = _excludedReason = null;
            _samples = _over33 = _over50 = _over100 = _save = _pauseOrOther = _gaps = _ownWrites = 0;
            Array.Clear(_sum, 0, _sum.Length); Array.Clear(_peak, 0, _peak.Length);
            Array.Clear(_callsInWindow, 0, _callsInWindow.Length); Array.Clear(_slowest, 0, _slowest.Length);
        }

        private void Publish(string line)
        {
            // Separate sink: no CampaignLog recursion. The synchronous
            // summary write affects the interval that starts at this boundary.
            long start = Stopwatch.GetTimestamp();
            try { _write(line + Environment.NewLine); }
            catch (IOException) { Stop(); }
            catch (UnauthorizedAccessException) { Stop(); }
            _writeCost = Stopwatch.GetTimestamp() - start;
            _ownWrite = true;
        }

        internal void Finish(long stamp, string reason)
        {
            if (_ended)
                return;
            Flush(stamp);
            if (!_ended)
                Publish($"performance-v1 end run={_id} reason={reason} completedIntervals={_index}; partial tail not sampled");
            Stop();
        }

        private void Stop()
        {
            _ended = true;
            _anchored = false;
            if (Active == this)
                Active = null;
        }
    }
}
