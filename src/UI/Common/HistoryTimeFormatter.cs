namespace task_monitor
{
    /// <summary>
    /// "N 秒前" label for the detail charts' hover tooltips. The histories are 60-TICK
    /// rolling queues, so a tick offset only becomes seconds via the current sampling
    /// interval (settings 采样间隔 → <see cref="SystemSnapshot.SampleIntervalMs"/>): at the
    /// 1s default tick N = N seconds, at 2s the same offset is 2N seconds. Whole seconds
    /// print bare, the 0.5s interval's half-steps print one decimal ("29.5 秒前").
    /// </summary>
    internal static class HistoryTimeFormatter
    {
        /// <summary>ticksAgo=0 → "现在", otherwise "N 秒前" (interval-scaled).</summary>
        public static string Ago(int ticksAgo, int intervalMs)
        {
            double sec = ticksAgo * intervalMs / 1000.0;
            if (sec <= 0) return "现在";
            return sec % 1 == 0 ? $"{sec:F0} 秒前" : $"{sec:F1} 秒前";
        }
    }
}
