using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// A VECTORED exception handler that finally puts NATIVE crashes on record. The reported
    /// 卡死 (≈30 s of a frozen process — 0 CPU, no messages, no log lines — then a silent
    /// recovery) turned out to be Windows Error Reporting dumping a crashed process: the
    /// Application event log shows <c>task_monitor.exe</c> faulting with <c>0xc0000005</c> and
    /// faulting module "unknown" (StackHash), i.e. an access violation the MANAGED handlers
    /// (AppDomain / Dispatcher / WndProc) never see — which is exactly why the app's own log
    /// stayed empty through every one of them.
    ///
    /// <para>A VEH runs BEFORE SEH, on the faulting thread, while its stack is still intact, so
    /// it can record what the crash was: the exception code, the faulting address with its
    /// module+offset, a raw return-address walk (same spelling) and — best effort — the MANAGED
    /// stack. It then returns <c>EXCEPTION_CONTINUE_SEARCH</c>: this only observes, Windows and
    /// the CLR keep handling exactly as they did before.</para>
    ///
    /// <para>Two deliberate choices: the report goes to its OWN file
    /// (<c>logs/native-crash.log</c>, plain <see cref="File.AppendAllText"/>) instead of
    /// <see cref="Logger"/> — the fault may have happened INSIDE the logger while holding its
    /// lock, and a VEH that blocks on that lock would turn a 30 s freeze into a permanent hang.
    /// And the handler is throttled to <see cref="MaxReports"/> records per run, so an AV in a
    /// loop cannot flood the file. Managed exceptions are filtered out by CODE: .NET raises them
    /// as <c>0xE0434352</c> via <c>RaiseException</c>, so they hit a VEH first on the NORMAL
    /// path — logging those would drown the real faults.</para>
    /// </summary>
    internal static class CrashTrace
    {
        private const uint EXCEPTION_CONTINUE_SEARCH = 0;
        private const int MaxReports = 4;
        private const int MaxFrames = 24;
        // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT
        private const uint ModuleFromAddress = 0x00000004 | 0x00000002;

        private static int _reports;
        private static string _path;
        private static VectoredHandler _handlerDelegate;   // must stay rooted for the process

        private delegate int VectoredHandler(IntPtr exceptionPointers);

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

        [DllImport("kernel32.dll")]
        private static extern ushort RtlCaptureStackBackTrace(uint framesToSkip, uint framesToCapture,
            [Out] IntPtr[] backTrace, out uint backTraceHash);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

        private const uint MemImage = 0x1000000;
        private const uint MemMapped = 0x40000;

        // ---- the recent-message ring (see NoteMessage) ----
        private const int RecentCount = 8;   // power of two — the index masks instead of modulo
        private static readonly uint[] _recentMsg = new uint[RecentCount];
        private static readonly long[] _recentWParam = new long[RecentCount];
        private static int _recentNext;

        /// <summary>
        /// Called at the very top of the overlay's WndProc. The 21:43 record showed a fault
        /// inside the DISPATCH path with no managed frame left to walk, so "what was this thread
        /// doing" cannot come from a stack — it has to be kept as it goes. One array write and
        /// an index bump per message: no allocation, no lock, safe on the hot path.
        /// </summary>
        public static void NoteMessage(uint msg, long wParam)
        {
            int i = _recentNext++ & (RecentCount - 1);
            _recentMsg[i] = msg;
            _recentWParam[i] = wParam;
        }

        /// <summary>
        /// The step a long-running path is currently in (the 1s tick's sampler steps, its phases,
        /// the Start() steps). The 21:46 record proved the fault happens in JIT'ed code while
        /// WM_TIMER is dispatched — and a JIT frame cannot be walked OR symbolized, so the only
        /// way to name the crashing step is to record it before it runs. Literal strings only:
        /// the assignment allocates nothing.
        /// </summary>
        public static void NoteStep(string step) => _step = step;

        /// <summary>The step last recorded — read by the handlers that log a caught fault.</summary>
        public static string CurrentStep => _step ?? "(未标记)";

        /// <summary>
        /// Snapshot of the recent-message ring, oldest first ("0x111(w=0)" items, space-joined).
        /// Read by App's stall watchdog: a thread blocked inside a call can never run the VEH
        /// dump below, so "what was it doing" has to be pulled from the OTHER side. Lock-free
        /// and best-effort — a mid-bump read just yields one torn slot.
        /// </summary>
        public static string RecentMessages()
        {
            var sb = new StringBuilder();
            for (int k = 0; k < RecentCount; k++)
            {
                int i = (_recentNext + k) & (RecentCount - 1);
                uint m = _recentMsg[i];
                if (m == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("0x").Append(m.ToString("X")).Append("(w=").Append(_recentWParam[i]).Append(')');
            }
            return sb.Length == 0 ? "(空)" : sb.ToString();
        }

        private static volatile string _step;

        /// <summary>
        /// Called by <see cref="SystemInfo.TrimMemory"/> right before its forced GC. The reported
        /// crash correlates with closing detail windows, i.e. with that trim — so every crash
        /// record states how long ago one ran. Negative = none this run.
        /// </summary>
        public static void NoteTrim() => _lastTrimTick = (long)SystemInfo.GetTickCount64();

        private static long _lastTrimTick;

        /// <summary>Milliseconds since the last forced trim, or -1 when none has run.</summary>
        public static long SinceTrimMs
        {
            get
            {
                long last = _lastTrimTick;
                return last == 0 ? -1 : (long)SystemInfo.GetTickCount64() - last;
            }
        }

        /// <summary>Installed from <see cref="CrashReporter.Install"/> (App's static ctor), i.e.
        /// before Main — a native fault during startup must land here too. Never throws.</summary>
        public static void Install()
        {
            if (_handlerDelegate != null) return;
            try
            {
                _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "native-crash.log");
                _handlerDelegate = Handler;
                if (AddVectoredExceptionHandler(1, _handlerDelegate) == IntPtr.Zero)
                    Logger.Warn("原生崩溃跟踪：AddVectoredExceptionHandler 失败——原生崩溃仍不会留下记录");
                else
                    Logger.Info("原生崩溃跟踪已安装（VEH）——原生崩溃将记录到 logs/native-crash.log");
            }
            catch (Exception ex)
            {
                Logger.Warn("原生崩溃跟踪：安装失败（" + ex.Message + "）——原生崩溃仍不会留下记录");
            }
        }

        // The VEH gets a PVOID to an EXCEPTION_POINTERS = { EXCEPTION_RECORD*; CONTEXT* }.
        // The fields are read by hand at their documented x64 offsets — and the RECORD must be
        // DEREFERENCED first (offset 0 is a pointer TO the record, not the exception code; the
        // first cut of this read the pointer's low half as the code and therefore silently
        // ignored the very crash it was built for). EXCEPTION_RECORD x64:
        //   +0 ExceptionCode  +4 ExceptionFlags  +8 ExceptionRecord*  +16 ExceptionAddress
        private static int Handler(IntPtr exceptionPointers)
        {
            try
            {
                if (_reports >= MaxReports || IntPtr.Size != 8 || exceptionPointers == IntPtr.Zero)
                    return (int)EXCEPTION_CONTINUE_SEARCH;

                IntPtr record = Marshal.ReadIntPtr(exceptionPointers);        // EXCEPTION_RECORD*
                if (record == IntPtr.Zero) return (int)EXCEPTION_CONTINUE_SEARCH;
                int code = Marshal.ReadInt32(record);                         // ExceptionCode
                int flags = Marshal.ReadInt32(record, 4);                     // ExceptionFlags
                IntPtr address = Marshal.ReadIntPtr(record, 16);              // ExceptionAddress
                if (!IsFault(code)) return (int)EXCEPTION_CONTINUE_SEARCH;
                _reports++;
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append(" 原生崩溃 #").Append(_reports)
                  .Append(" 异常=0x").Append(((uint)code).ToString("X8"))
                  .Append(" flags=0x").Append(((uint)flags).ToString("X8"))
                  .Append(" 地址=").Append(Describe(address))
                  .Append(" 线程=0x").Append(GetCurrentThreadId().ToString("X"))
                  .Append("（托管 ").Append(Thread.CurrentThread.ManagedThreadId).Append(')')
                  .AppendLine();

                var frames = new IntPtr[MaxFrames];
                uint hash;
                int n = RtlCaptureStackBackTrace(0, MaxFrames, frames, out hash);
                sb.Append("  原生栈(hash=0x").Append(hash.ToString("X8")).Append(", ").Append(n).Append(" 帧)=");
                for (int i = 0; i < n; i++) sb.Append(i == 0 ? "" : " ← ").Append(Describe(frames[i]));
                sb.AppendLine();

                // Where the fault sits: an IMAGE region names a system module, MEM_PRIVATE means
                // JIT/generated code (a callback thunk, say) — the two demand different fixes.
                sb.Append("  故障地址区域=").Append(DescribeRegion(address));
                if (n > 0) sb.Append("  栈顶帧区域=").Append(DescribeRegion(frames[0]));
                sb.AppendLine();

                // An access violation's parameters: [0] = 0 read / 1 write / 8 execute,
                // [1] = the address that was touched. Together with the registers below this
                // separates "jumped into garbage" from "dereferenced a freed COM pointer".
                int nParams = Marshal.ReadInt32(record, 24);
                if (nParams > 0)
                {
                    long kind = Marshal.ReadIntPtr(record, 32).ToInt64();
                    long touched = nParams > 1 ? Marshal.ReadIntPtr(record, 40).ToInt64() : 0;
                    sb.Append("  访问=").Append(kind == 0 ? "读" : kind == 1 ? "写" : kind == 8 ? "执行" : kind.ToString())
                      .Append(" 目标=0x").Append(touched.ToString("X"));
                    if (touched != 0) sb.Append("（").Append(DescribeRegion(new IntPtr(touched))).Append('）');
                    sb.AppendLine();
                }

                // Registers of the faulting instruction — read straight out of the CONTEXT at the
                // AMD64 offsets (Rax 0x78, Rcx 0x80, Rdx 0x88, Rsp 0x98, Rbp 0xA0, R8 0xB8,
                // R9 0xC0, Rip 0xF8). Rcx is the first argument: for an interop/COM stub that is
                // the interface pointer — a garbage value there IS the use-after-free.
                IntPtr context = Marshal.ReadIntPtr(exceptionPointers, 8);
                if (context != IntPtr.Zero)
                {
                    sb.Append("  寄存器 Rip=0x").Append(ReadReg(context, 0xF8).ToString("X"))
                      .Append(" Rsp=0x").Append(ReadReg(context, 0x98).ToString("X"))
                      .Append(" Rbp=0x").Append(ReadReg(context, 0xA0).ToString("X"))
                      .Append(" Rax=0x").Append(ReadReg(context, 0x78).ToString("X"))
                      .Append(" Rcx=0x").Append(ReadReg(context, 0x80).ToString("X"))
                      .Append(" Rdx=0x").Append(ReadReg(context, 0x88).ToString("X"))
                      .Append(" R8=0x").Append(ReadReg(context, 0xB8).ToString("X"))
                      .Append(" R9=0x").Append(ReadReg(context, 0xC0).ToString("X"));
                    sb.AppendLine();

                    // Raw stack scan: the frames above a JIT/stub frame cannot be UNWOUND, but the
                    // return addresses are still lying there — classify every stack word that
                    // points into a module or into executable private memory. That is the closest
                    // thing to a call chain we can get without a debugger.
                    long rsp = ReadReg(context, 0x98);
                    sb.Append("  栈扫描(Rsp 起 48 个 8 字节)=");
                    for (int k = 0; k < 48; k++)
                    {
                        long v;
                        try { v = Marshal.ReadIntPtr(new IntPtr(rsp + k * 8)).ToInt64(); }
                        catch { break; }
                        if (v < 0x10000 || (v & 0xF) != 0) continue;      // not a plausible code address
                        string d = DescribeCode(v);
                        if (d == null) continue;
                        sb.Append('[').Append(k).Append(']').Append(d).Append(' ');
                    }
                    sb.AppendLine();
                }

                // What this thread was doing: the ring survives the missing managed frame.
                sb.Append("  最近消息(旧→新)=");
                int start = _recentNext & (RecentCount - 1);
                for (int k = 0; k < RecentCount; k++)
                {
                    int i = (start + k) & (RecentCount - 1);
                    if (k > 0) sb.Append(" → ");
                    sb.Append("0x").Append(_recentMsg[i].ToString("X")).Append("(w=").Append(_recentWParam[i]).Append(')');
                }
                sb.AppendLine();

                sb.Append("  当前步骤=").Append(_step ?? "(未标记)");
                long sinceTrim = SinceTrimMs;
                sb.Append("  距上次内存回收(GC+trim)=").Append(sinceTrim < 0 ? "本次运行未发生" : sinceTrim + "ms");
                sb.AppendLine();

                // Best effort: the faulting thread's MANAGED frames are still on this stack.
                try { sb.Append("  托管栈=").Append(new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", "\n    ")); }
                catch (Exception ex) { sb.Append("  托管栈=(不可用: ").Append(ex.GetType().Name).Append(')'); }
                sb.AppendLine();

                Append(sb.ToString());
            }
            catch { /* a crash recorder must never crash */ }
            return (int)EXCEPTION_CONTINUE_SEARCH;
        }

        /// <summary>The native fault codes worth a record — an access violation, an in-page
        /// error, illegal/privileged instruction, the arithmetic faults, stack overflow and
        /// fast-fail. Deliberately NOT a catch-all: 0xE0434352 (managed) and the countless
        /// benign first-chance codes must stay invisible.</summary>
        private static bool IsFault(int code)
        {
            switch (unchecked((uint)code))
            {
                case 0xC0000005:   // access violation — the one this exists for
                case 0xC0000006:   // in-page error
                case 0xC000001D:   // illegal instruction
                case 0xC000001E:   // invalid lock sequence
                case 0xC0000025:   // noncontinuable exception
                case 0xC000008C:   // array bounds exceeded
                case 0xC0000094:   // integer divide by zero
                case 0xC0000095:   // integer overflow
                case 0xC0000096:   // privileged instruction
                case 0xC00000FD:   // stack overflow
                case 0xC0000409:   // fail fast
                case 0xC0000417:   // invalid CRT parameter
                case 0xC000041D:   // unhandled exception in a callback (the WndProc case, gotcha §3)
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>"module+0xoffset" for an address (falls back to the bare address when it is
        /// not inside a loaded module — JIT-compiled managed code lives in an anonymous heap).</summary>
        private static string Describe(IntPtr address)
        {
            try
            {
                IntPtr module;
                if (GetModuleHandleExW(ModuleFromAddress, address, out module) && module != IntPtr.Zero)
                {
                    var name = new StringBuilder(260);
                    if (GetModuleFileNameW(module, name, (uint)name.Capacity) > 0)
                    {
                        long offset = address.ToInt64() - module.ToInt64();
                        return Path.GetFileName(name.ToString()) + "+0x" + offset.ToString("X");
                    }
                }
            }
            catch { }
            return "0x" + address.ToInt64().ToString("X");
        }

        /// <summary>What kind of memory an address lives in: MEM_IMAGE + a module name (system
        /// DLL, our exe), MEM_MAPPED, or MEM_PRIVATE — the last one is where JIT-compiled code
        /// and marshalling thunks live, and the two cases point at completely different bugs.
        /// <see cref="Describe"/> alone answered "no module" for the 21:43 fault address, which
        /// is exactly the ambiguity this resolves.</summary>
        private static string DescribeRegion(IntPtr address)
        {
            try
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery(address, out mbi, new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)))) == IntPtr.Zero)
                    return "无法查询";
                string kind = mbi.Type == MemImage ? "MEM_IMAGE" : mbi.Type == MemMapped ? "MEM_MAPPED" : "MEM_PRIVATE";
                string where = "";
                if (mbi.Type == MemImage && mbi.AllocationBase != IntPtr.Zero)
                {
                    var name = new StringBuilder(260);
                    if (GetModuleFileNameW(mbi.AllocationBase, name, (uint)name.Capacity) > 0)
                        where = Path.GetFileName(name.ToString()) + "+0x" +
                                (address.ToInt64() - mbi.AllocationBase.ToInt64()).ToString("X");
                }
                return $"{kind} 基址=0x{mbi.AllocationBase.ToInt64():X} 保护=0x{mbi.Protect:X} {where}".TrimEnd();
            }
            catch { return "查询异常"; }
        }

        /// <summary>One 64-bit register out of the CONTEXT record (offsets per winnt.h AMD64).</summary>
        private static long ReadReg(IntPtr context, int offset) => Marshal.ReadIntPtr(context, offset).ToInt64();

        /// <summary>Current thread's native id — public so the taskbar thread can stamp it
        /// for the stall watchdog (GetThreadContext needs the NATIVE id, not the managed one).</summary>
        public static uint CurrentNativeThreadId() => GetCurrentThreadId();

        // ---- stall forensics: another thread's native stack (the stall watchdog) ----
        // The 2026-09-30 卡死 left the step marker + message ring but still could not name
        // the blocking call; the ring only proves WHICH WndProc message, not where inside.
        // The next stall must answer with a real stack: suspend the taskbar thread for the
        // context+stack COPY only (µs), resume, then classify offline — so the capture
        // itself can never wedge behind whatever the thread is blocked in. Best-effort: a
        // torn frame just gets skipped. Never throws.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
        private const uint ThreadSuspendResume = 0x0002;
        private const uint ThreadGetContext = 0x0008;
        private const uint ThreadQueryInformation = 0x0040;
        private const int ContextBytes = 1232;                 // AMD64 CONTEXT
        private const int ContextFlagsControlInteger = 0x100003;   // AMD64|CONTROL|INTEGER

        /// <summary>One line: RIP (module+offset) + the raw stack scan, classified. "(…)"
        /// parenthesised text on any failure — the caller logs the line regardless.</summary>
        public static string CaptureStackOf(int nativeThreadId)
        {
            if (nativeThreadId <= 0 || IntPtr.Size != 8) return "(线程句柄不可用)";
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadQueryInformation, false, (uint)nativeThreadId);
                if (h == IntPtr.Zero) return "(OpenThread 失败 err=" + Marshal.GetLastWin32Error() + ")";
                if (SuspendThread(h) == 0xFFFFFFFF) return "(SuspendThread 失败)";
                long rip, rsp;
                var words = new long[64];
                try
                {
                    IntPtr ctx = Marshal.AllocHGlobal(ContextBytes);
                    try
                    {
                        Marshal.WriteInt32(ctx, 0x30, ContextFlagsControlInteger);   // ContextFlags @ +0x30
                        if (!GetThreadContext(h, ctx)) return "(GetThreadContext 失败)";
                        rip = ReadReg(ctx, 0xF8);
                        rsp = ReadReg(ctx, 0x98);
                    }
                    finally { Marshal.FreeHGlobal(ctx); }
                    for (int k = 0; k < words.Length; k++)
                    {
                        try { words[k] = Marshal.ReadIntPtr(new IntPtr(rsp + k * 8)).ToInt64(); }
                        catch { words[k] = 0; }
                    }
                }
                finally { ResumeThread(h); }
                var sb = new StringBuilder(256);
                sb.Append("RIP=").Append(Describe(new IntPtr(rip)));
                sb.Append(" 栈=");
                bool any = false;
                for (int k = 0; k < words.Length; k++)
                {
                    long v = words[k];
                    if (v < 0x10000 || (v & 0xF) != 0) continue;
                    string d = DescribeCode(v);
                    if (d == null) continue;
                    if (any) sb.Append(" ← ");
                    sb.Append(d);
                    any = true;
                }
                if (!any) sb.Append("(无可归因帧)");
                return sb.ToString();
            }
            catch (Exception ex) { return "(抓栈异常: " + ex.GetType().Name + ")"; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>Describes a stack word ONLY if it plausibly is a code address in a module or
        /// in executable private memory (JIT/stub code) — otherwise null. Used by the raw stack
        /// scan, which stands in for the unwinder that cannot walk a JIT frame.</summary>
        private static string DescribeCode(long value)
        {
            try
            {
                var address = new IntPtr(value);
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery(address, out mbi, new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)))) == IntPtr.Zero)
                    return null;
                bool executable = (mbi.Protect & 0xF0) != 0 && (mbi.Protect & 0x01) == 0;   // execute, not guard
                if (!executable) return null;
                if (mbi.Type == MemImage)
                {
                    var name = new StringBuilder(260);
                    if (GetModuleFileNameW(mbi.AllocationBase, name, (uint)name.Capacity) > 0)
                        return Path.GetFileName(name.ToString()) + "+0x" + (value - mbi.AllocationBase.ToInt64()).ToString("X");
                    return null;
                }
                return "[JIT/桩+0x" + (value - mbi.AllocationBase.ToInt64()).ToString("X") + "]";
            }
            catch { return null; }
        }

        private static void Append(string text)        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, text, Encoding.UTF8);
            }
            catch { /* the disk may be exactly what is broken */ }
        }
    }
}
