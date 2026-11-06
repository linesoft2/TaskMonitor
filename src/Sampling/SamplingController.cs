using System;
using System.Diagnostics;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// Owns one live <see cref="SystemSampler"/> on a dedicated STA thread. Overlay
    /// lifetimes never shut it down: a failed sampler is retired before reconstruction,
    /// and the last sampler is retired when this thread exits. Retirement unregisters SRUM
    /// and stops pollers; a failed unregister is retried up to 3 times and blocks reconstruction.
    /// </summary>
    internal sealed class SamplingController
    {
        private const int DefaultSampleIntervalMs = 1000;
        private const int MinSampleIntervalMs = 250;
        private const int MaxSampleIntervalMs = 10000;
        private const int InitialRebuildDelayMs = 1000;
        private const int MaxRebuildDelayMs = 30000;
        private const int StopJoinMs = 1000;

        private readonly object _lifecycleSync = new object();
        private readonly object _samplerSync = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stopping;
        private volatile bool _sampleRequested;
        private volatile SystemSampler _sampler;
        private volatile SystemSnapshot _latestSnapshot;

        private volatile int _sampleIntervalMs = DefaultSampleIntervalMs;
        private volatile int _enabledMask = SystemSampler.MaskAll;
        private volatile bool _mergeByPath;
        private volatile int _diskDisplayMode;
        private volatile int _diskDisplayIndex;
        private volatile int _gpuDisplayMode = (int)MetricDisplayMode.Max;
        private volatile int _gpuDisplayIndex;
        private volatile int _gpuTempIndex = -1;
        private volatile bool _gpuTempFahrenheit;
        private volatile string _netAdapterId;
        private volatile bool _clashEnabled = true;
        private volatile string _clashApiAddress;
        private volatile string _clashApiSecret;
        private volatile bool _publicIpLookupEnabled = true;

        public SystemSnapshot LatestSnapshot => _latestSnapshot;
        public long LastTickTickCount;
        public int NativeThreadId;
        public Action<SystemSnapshot> SnapshotChanged;

        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_thread != null && _thread.IsAlive) return;

                _stopping = false;
                var thread = new Thread(SamplingLoop) { IsBackground = true, Name = "SamplingController" };
                thread.SetApartmentState(ApartmentState.STA);
                _thread = thread;
                try
                {
                    thread.Start();
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_thread, thread)) _thread = null;
                    _stopping = true;
                    Logger.Error("采样线程启动失败", ex);
                }
            }
        }

        public void Stop()
        {
            Thread thread;
            lock (_lifecycleSync)
            {
                _stopping = true;
                thread = _thread;
            }
            _wake.Set();

            if (thread == null || thread == Thread.CurrentThread) return;
            if (!thread.Join(StopJoinMs) && thread.IsAlive)
            {
                Logger.Warn($"采样线程停止等待超时（{StopJoinMs}ms）——采样调用返回后仍会在线程末尾清理 SRUM");
            }
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_thread, thread) && !thread.IsAlive) _thread = null;
            }
        }

        public void SetSampleInterval(int ms)
        {
            _sampleIntervalMs = Math.Max(MinSampleIntervalMs, Math.Min(MaxSampleIntervalMs, ms));
            _wake.Set();
        }

        public void SetEnabledMask(int mask)
        {
            lock (_samplerSync)
            {
                _enabledMask = mask;
                _sampler?.SetEnabledMask(mask);
            }
            RequestImmediateSample();
        }

        public void SetMergeByPath(bool on)
        {
            lock (_samplerSync)
            {
                _mergeByPath = on;
                _sampler?.SetMergeByPath(on);
            }
        }

        public void SetDiskDisplay(int mode, int index)
        {
            lock (_samplerSync)
            {
                _diskDisplayMode = mode;
                _diskDisplayIndex = index;
                _sampler?.SetDiskDisplay(mode, index);
            }
        }

        public void SetGpuDisplay(int mode, int index)
        {
            lock (_samplerSync)
            {
                _gpuDisplayMode = mode;
                _gpuDisplayIndex = index;
                _sampler?.SetGpuDisplay(mode, index);
            }
        }

        public void SetGpuTempConfig(int index, bool fahrenheit)
        {
            lock (_samplerSync)
            {
                _gpuTempIndex = index;
                _gpuTempFahrenheit = fahrenheit;
                _sampler?.SetGpuTempConfig(index, fahrenheit);
            }
        }

        public void SetNetAdapter(string id)
        {
            lock (_samplerSync)
            {
                _netAdapterId = id;
                _sampler?.SetNetAdapter(id);
            }
        }

        public void SetClashApi(bool enabled, string address, string secret)
        {
            lock (_samplerSync)
            {
                _clashEnabled = enabled;
                _clashApiAddress = address;
                _clashApiSecret = secret;
                _sampler?.SetClashApi(enabled, address, secret);
            }
        }

        public void SetPublicIpLookup(bool enabled)
        {
            lock (_samplerSync)
            {
                _publicIpLookupEnabled = enabled;
                _sampler?.SetPublicIpLookup(enabled);
            }
        }

        public void RequestImmediateSample()
        {
            _sampleRequested = true;
            _wake.Set();
        }

        public void RequestWifiDetails()
        {
            lock (_samplerSync)
            {
                _sampler?.RequestWifiDetails();
            }
        }

        private void SamplingLoop()
        {
            NativeThreadId = (int)CrashTrace.CurrentNativeThreadId();
            LastTickTickCount = (long)SystemInfo.GetTickCount64();
            Logger.Info("采样线程已启动（STA）");

            int failures = 0;
            try
            {
                while (!_stopping)
                {
                    SystemSampler sampler = null;
                    try
                    {
                        sampler = new SystemSampler();
                        lock (_samplerSync)
                        {
                            ApplyConfiguration(sampler);
                            _sampler = sampler;
                        }

                        while (!_stopping)
                        {
                            _sampleRequested = false;
                            SampleAndPublish(sampler);
                            failures = 0;
                            WaitForNextSample();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!RetireSampler(sampler))
                        {
                            Logger.Error("旧 SRUM 注销连续失败——停止自动重建，防止重复注册");
                            break;
                        }
                        if (_stopping) break;

                        failures++;
                        int delay = Math.Min(MaxRebuildDelayMs,
                            InitialRebuildDelayMs << Math.Min(failures - 1, 5));
                        Logger.Warn($"采样器创建或采样失败（连续 {failures} 次），{delay}ms 后重建", ex);
                        WaitForRebuild(delay);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("采样线程异常退出", ex);
            }
            finally
            {
                RetireSampler(_sampler);
                Logger.Info("采样线程已停止");
            }
        }

        private void ApplyConfiguration(SystemSampler sampler)
        {
            sampler.SetEnabledMask(_enabledMask);
            sampler.SetMergeByPath(_mergeByPath);
            sampler.SetDiskDisplay(_diskDisplayMode, _diskDisplayIndex);
            sampler.SetGpuDisplay(_gpuDisplayMode, _gpuDisplayIndex);
            sampler.SetGpuTempConfig(_gpuTempIndex, _gpuTempFahrenheit);
            sampler.SetNetAdapter(_netAdapterId);
            sampler.SetClashApi(_clashEnabled, _clashApiAddress, _clashApiSecret);
            sampler.SetPublicIpLookup(_publicIpLookupEnabled);
        }

        private void SampleAndPublish(SystemSampler sampler)
        {
            LastTickTickCount = (long)SystemInfo.GetTickCount64();
            CrashTrace.NoteStep("采样阶段");
            SystemSnapshot snapshot = sampler.Sample();
            snapshot.SampleIntervalMs = _sampleIntervalMs;

            CrashTrace.NoteStep("发布快照");
            _latestSnapshot = snapshot;
            var changed = SnapshotChanged;
            if (changed == null) return;

            foreach (Action<SystemSnapshot> subscriber in changed.GetInvocationList())
            {
                try
                {
                    subscriber(snapshot);
                }
                catch (Exception ex)
                {
                    Logger.WarnOnce("snapshot-subscriber", "快照订阅者异常（已隔离，采样线程继续）", ex);
                }
            }
        }

        private void WaitForNextSample()
        {
            long start = Stopwatch.GetTimestamp();
            while (!_stopping && !_sampleRequested)
            {
                int interval = _sampleIntervalMs;
                long elapsed = ElapsedMilliseconds(start);
                if (elapsed >= interval) return;

                // An interval change also signals _wake; without _sampleRequested it only
                // recalculates this wait and never creates an extra sampling pass.
                _wake.WaitOne((int)(interval - elapsed));
            }
        }

        private void WaitForRebuild(int delayMs)
        {
            long start = Stopwatch.GetTimestamp();
            while (!_stopping)
            {
                LastTickTickCount = (long)SystemInfo.GetTickCount64();
                long elapsed = ElapsedMilliseconds(start);
                if (elapsed >= delayMs) return;
                _wake.WaitOne((int)Math.Min(1000, delayMs - elapsed));
            }
        }

        private static long ElapsedMilliseconds(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency;
        }

        private bool RetireSampler(SystemSampler sampler)
        {
            if (sampler == null) return true;
            lock (_samplerSync)
            {
                if (ReferenceEquals(_sampler, sampler)) _sampler = null;
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (sampler.Shutdown()) return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"采样器 Shutdown 异常（第 {attempt}/3 次）", ex);
                }
                if (attempt < 3) _wake.WaitOne(250);
            }
            Logger.Error("采样器注销连续 3 次失败——保留原注册句柄，禁止继续重建");
            return false;
        }
    }
}
