using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// Process-wide file log: one rolling file per day — <c>logs/task_monitor-yyyy-MM-dd.log</c>
    /// next to the exe (the same place settings.yaml lives). Files older than
    /// <see cref="RetentionDays"/> days are pruned at startup and again at each day
    /// rollover. Levels: DEBUG / INFO / WARN / ERROR — everything is written; the volume
    /// is bounded by DESIGN, not by filtering: hot per-tick paths never log, only state
    /// changes, decisions, degradations and failures do (per-tick failure paths use the
    /// <c>Once</c> variants). Crash stacks land here through <see cref="CrashReporter"/>.
    ///
    /// Thread-safe — the WPF UI thread, the overlay's taskbar STA thread and the SRU
    /// callback thread all write. Every line is flushed immediately (AutoFlush), so a
    /// fatal crash never loses its own stack to a buffered writer. NEVER throws: a
    /// logging failure must not take down whatever it was describing — after
    /// <see cref="MaxFailures"/> consecutive I/O failures the log turns itself off
    /// silently for the rest of the run.
    /// </summary>
    internal static class Logger
    {
        private const int RetentionDays = 7;
        private const int MaxFailures = 5;

        private static readonly object _sync = new object();
        private static readonly HashSet<string> _onceKeys = new HashSet<string>(StringComparer.Ordinal);
        private static StreamWriter _writer;     // null until the first successful write
        private static string _dir;              // null = no usable log dir (read-only install)
        private static DateTime _fileDate;       // the day _writer's file belongs to
        private static int _failures;            // consecutive I/O failures

        /// <summary>
        /// Locate/create the log dir and prune old files. Called once from App's static
        /// ctor (before Main), so even an early startup crash lands in the file. The
        /// unelevated first-run instance may find the install dir read-only — it simply
        /// runs without a log (the elevated relaunched child retries and succeeds).
        /// </summary>
        public static void Init()
        {
            lock (_sync)
            {
                if (_dir != null) return;
                try
                {
                    _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    Directory.CreateDirectory(_dir);
                    CleanupLocked();
                }
                catch { _dir = null; }
            }
        }

        public static void Debug(string msg) => Write("DEBUG", msg, null);
        public static void Info(string msg) => Write("INFO", msg, null);
        public static void Warn(string msg) => Write("WARN", msg, null);
        public static void Warn(string msg, Exception ex) => Write("WARN", msg, ex);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);

        /// <summary>WARN only the first time per <paramref name="key"/> — for failure
        /// paths that run every sampling tick (a per-tick WARN would flood the file).</summary>
        public static void WarnOnce(string key, string msg, Exception ex = null)
        {
            lock (_sync) { if (!_onceKeys.Add(key)) return; }
            Write("WARN", msg, ex);
        }

        /// <summary>INFO only the first time per <paramref name="key"/>.</summary>
        public static void InfoOnce(string key, string msg)
        {
            lock (_sync) { if (!_onceKeys.Add(key)) return; }
            Write("INFO", msg, null);
        }

        private static void Write(string level, string msg, Exception ex)
        {
            lock (_sync)
            {
                if (_dir == null || _failures >= MaxFailures) return;
                try
                {
                    DateTime today = DateTime.Today;
                    if (_writer == null || today != _fileDate)   // first write / day rollover
                    {
                        OpenLocked(today);
                        CleanupLocked();
                    }
                    Thread t = Thread.CurrentThread;
                    string thread = string.IsNullOrEmpty(t.Name)
                        ? "T" + t.ManagedThreadId.ToString(CultureInfo.InvariantCulture)
                        : t.Name;
                    var sb = new StringBuilder(256);
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                      .Append(" [").Append(level.PadRight(5)).Append("] [").Append(thread).Append("] ")
                      .Append(msg);
                    if (ex != null) sb.AppendLine().Append(ex);
                    _writer.WriteLine(sb.ToString());
                    _failures = 0;
                }
                catch { _failures++; }   // transient or not — retry next write, up to MaxFailures
            }
        }

        private static void OpenLocked(DateTime day)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _fileDate = day;
            _writer = new StreamWriter(
                Path.Combine(_dir, $"task_monitor-{day:yyyy-MM-dd}.log"), append: true)
            { AutoFlush = true };
        }

        // Prune task_monitor-*.log files untouched for over a week. Best-effort per file —
        // a locked log (another instance writing it) is skipped, never fatal.
        private static void CleanupLocked()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (string f in Directory.GetFiles(_dir, "task_monitor-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }
    }
}
