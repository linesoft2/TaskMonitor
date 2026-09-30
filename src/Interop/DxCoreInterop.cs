using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// The DXCore (dxcore.dll) COM boundary — the API Taskmgr.exe's <c>WdcGpuMonitor</c>
    /// uses for EVERY GPU metric (reversed from Taskmgr.exe, 24H2 build; no PDH, no
    /// D3DKMT, no vendor SDK). Vtables below are complete and in declaration order —
    /// a COM-interop interface must declare every slot or later calls land on the wrong
    /// method. Enum values / struct layouts verified against the Windows SDK
    /// (um/dxcore_interface.h). C++ <c>bool</c> returns are 1 byte — always
    /// <c>[return: MarshalAs(UnmanagedType.I1)]</c> (the default 4-byte BOOL marshaling
    /// would read garbage).
    ///
    /// Win10 reality check (measured on 22H2/19045): dxcore.dll exists since 1903, but
    /// <see cref="IDXCoreAdapter1"/> is NOT implemented there (QI → E_NOINTERFACE, despite
    /// the SDK docs' "Windows 10 2004+" claim — Win11-only in practice), and
    /// CreateAdapterList only matches the runtime attributes (D3D11/D3D12 Graphics,
    /// D3D12 Core Compute), never the Win11 hardware-type ones (GPU/NPU/…). Adapters are
    /// therefore always fetched as the base <see cref="IDXCoreAdapter"/> and
    /// IDXCoreAdapter1 is QI'd separately where it exists.
    /// </summary>
    internal enum DXCoreAdapterProperty : uint
    {
        InstanceLuid = 0,
        DriverVersion = 1,
        DriverDescription = 2,
        HardwareID = 3,
        KmdModelVersion = 4,
        ComputePreemptionGranularity = 5,
        GraphicsPreemptionGranularity = 6,
        DedicatedAdapterMemory = 7,
        DedicatedSystemMemory = 8,
        SharedSystemMemory = 9,
        AcgCompatible = 10,
        IsHardware = 11,
        IsIntegrated = 12,
        IsDetachable = 13,
        HardwareIDParts = 14,
        PhysicalAdapterCount = 15,
        AdapterEngineCount = 16,       // GetPropertyWithInput, in: uint32 physicalAdapterIndex → uint32 count
        AdapterEngineName = 17,        // GetPropertyWithInput, in: DXCoreEngineNamePropertyInput
    }

    internal enum DXCoreAdapterState : uint
    {
        IsDriverUpdateInProgress = 0,
        AdapterMemoryBudget = 1,                 // in: {nodeIndex, segmentGroup} (segmentGroup takes DXCoreMemoryType's values) → DXCoreAdapterMemoryBudget {budget, currentUsage, availableForReservation, currentReservation}
        AdapterMemoryUsageBytes = 2,                 // in: {physIdx, DXCoreMemoryType} → DXCoreMemoryUsage {committed, resident}
        AdapterMemoryUsageByProcessBytes = 3,
        AdapterEngineRunningTimeMicroseconds = 4,    // in: {physIdx, engineIdx, processId=0} → uint64 μs
        AdapterEngineRunningTimeByProcessMicroseconds = 5,
        AdapterTemperatureCelsius = 6,               // no input → float °C (support varies by driver)
        AdapterInUseProcessCount = 7,
        AdapterInUseProcessSet = 8,
        AdapterEngineFrequencyHertz = 9,
        AdapterMemoryFrequencyHertz = 10,
    }

    internal enum DXCoreMemoryType : uint
    {
        Dedicated = 0,   // "Local" segment group — 专用 GPU 内存
        Shared = 1,      // "NonLocal" — 共享 GPU 内存
    }

    internal enum DXCoreNotificationType : uint
    {
        AdapterListStale = 0,
        AdapterNoLongerValid = 1,
        AdapterBudgetChange = 2,
        AdapterHardwareContentProtectionTeardown = 3,
    }

    [ComImport]
    [Guid("78ee5945-c36e-4b13-a669-005dd11c0f06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapterFactory
    {
        // filterAttributes is a raw const GUID* — declared IntPtr deliberately: letting the
        // COM marshaller handle a Guid[] delivers a pointer to marshal metadata instead of
        // the GUID data (the list then stores a garbage attribute and matches nothing).
        [PreserveSig]
        int CreateAdapterList(uint numAttributes, IntPtr filterAttributes, ref Guid riid,
            out IDXCoreAdapterList ppvAdapterList);
        // Slot 2. Declared even though nothing calls it: a COM vtable is positional, so
        // omitting a slot silently shifts every method below onto the wrong function
        // (dxcore_interface.h order: CreateAdapterList, GetAdapterByLuid,
        // IsNotificationTypeSupported, RegisterEventNotification, UnregisterEventNotification).
        [PreserveSig]
        int GetAdapterByLuid(ref long adapterLuid, ref Guid riid, out IDXCoreAdapter ppvAdapter);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsNotificationTypeSupported(DXCoreNotificationType notificationType);
        [PreserveSig]
        int RegisterEventNotification([MarshalAs(UnmanagedType.IUnknown)] object dxcoreObject,
            DXCoreNotificationType notificationType, IntPtr callback, IntPtr callbackContext,
            out uint eventCookie);
        [PreserveSig]
        int UnregisterEventNotification(uint eventCookie);
    }

    [ComImport]
    [Guid("526c7776-40e9-459b-b711-f32ad76dfc28")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapterList
    {
        // GetAdapter outputs the BASE IDXCoreAdapter — implemented by every dxcore.dll
        // since Win10 1903. Asking for IDXCoreAdapter1 here instead fails with
        // E_NOINTERFACE on Win10 (it doesn't exist there) and would find zero adapters.
        [PreserveSig]
        int GetAdapter(uint index, ref Guid riid, out IDXCoreAdapter ppvAdapter);
        [PreserveSig]
        uint GetAdapterCount();
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsStale();
        [PreserveSig]
        int GetFactory(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvFactory);
        [PreserveSig]
        int Sort(uint numPreferences, IntPtr preferences);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsAdapterPreferenceSupported(uint preference);
    }

    [ComImport]
    [Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapter
    {
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsValid();
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsAttributeSupported(ref Guid attributeGUID);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsPropertySupported(DXCoreAdapterProperty property);
        [PreserveSig]
        int GetProperty(DXCoreAdapterProperty property, UIntPtr bufferSize, IntPtr propertyData);
        [PreserveSig]
        int GetPropertySize(DXCoreAdapterProperty property, out UIntPtr bufferSize);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsQueryStateSupported(DXCoreAdapterState property);
        [PreserveSig]
        int QueryState(DXCoreAdapterState state, UIntPtr inputStateDetailsSize,
            IntPtr inputStateDetails, UIntPtr outputBufferSize, IntPtr outputBuffer);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsSetStateSupported(DXCoreAdapterState property);
        [PreserveSig]
        int SetState(DXCoreAdapterState state, UIntPtr inputStateDetailsSize,
            IntPtr inputStateDetails, UIntPtr inputDataSize, IntPtr inputData);
        [PreserveSig]
        int GetFactory(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvFactory);
    }

    [ComImport]
    [Guid("a0783366-cfa3-43be-9d79-55b2da97c63c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapter1
    {
        // net48's COM interop MISDISPATCHES methods declared on a derived [ComImport]
        // interface (the base interface's slots aren't counted, so GetPropertyWithInput
        // landed on IsValid/IsPropertySupported and returned a bogus bool-as-HRESULT).
        // The interface is therefore declared FLAT — all base IDXCoreAdapter methods
        // repeated in vtable order, then the IDXCoreAdapter1 addition (slot 13).
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsValid();
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsAttributeSupported(ref Guid attributeGUID);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsPropertySupported(DXCoreAdapterProperty property);
        [PreserveSig]
        int GetProperty(DXCoreAdapterProperty property, UIntPtr bufferSize, IntPtr propertyData);
        [PreserveSig]
        int GetPropertySize(DXCoreAdapterProperty property, out UIntPtr bufferSize);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsQueryStateSupported(DXCoreAdapterState property);
        [PreserveSig]
        int QueryState(DXCoreAdapterState state, UIntPtr inputStateDetailsSize,
            IntPtr inputStateDetails, UIntPtr outputBufferSize, IntPtr outputBuffer);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.I1)]
        bool IsSetStateSupported(DXCoreAdapterState property);
        [PreserveSig]
        int SetState(DXCoreAdapterState state, UIntPtr inputStateDetailsSize,
            IntPtr inputStateDetails, UIntPtr inputDataSize, IntPtr inputData);
        [PreserveSig]
        int GetFactory(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvFactory);
        [PreserveSig]
        int GetPropertyWithInput(DXCoreAdapterProperty property, UIntPtr inputPropertyDetailsSize,
            IntPtr inputPropertyDetails, UIntPtr outputBufferSize, IntPtr outputBuffer);
    }

    internal static class DxCoreInterop
    {
        public static readonly Guid IID_IDXCoreAdapterFactory = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
        public static readonly Guid IID_IDXCoreAdapterList = new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
        public static readonly Guid IID_IDXCoreAdapter = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        public static readonly Guid IID_IDXCoreAdapter1 = new Guid("a0783366-cfa3-43be-9d79-55b2da97c63c");

        // Adapter-list filter attributes (dxcore_interface.h). The first is a Win11
        // HARDWARE_TYPE attribute — Win11's CreateAdapterList matches it, Win10's returns
        // an empty list for it. The other three are runtime attributes matched on BOTH
        // OS families (they're the passes that find GPUs on Win10 — a VMware SVGA 3D
        // carries D3D12_GRAPHICS but not the hardware-type GPU one). Taskmgr enumerates
        // five (these plus the ML/Media runtime attributes — not GPUs); the passes we
        // run dedupe by LUID.
        public static readonly Guid AttributeGpu = new Guid("b69eb219-3ded-4464-979f-a00bd4687006");
        public static readonly Guid AttributeD3D12Graphics = new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
        public static readonly Guid AttributeD3D11Graphics = new Guid("8c47866b-7583-450d-f0f0-6bada895af4b");
        public static readonly Guid AttributeD3D12CoreCompute = new Guid("248e2800-a793-4724-abaa-23a6de1be090");

        [DllImport("dxcore.dll", ExactSpelling = true)]
        public static extern int DXCoreCreateAdapterFactory(ref Guid riid, out IDXCoreAdapterFactory ppvFactory);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern bool QueryPerformanceFrequency(out long lpFrequency);

        /// <summary>The taskbar STA thread does pure P/Invoke COM elsewhere (DirectN), so it
        /// may never have entered a COM apartment when the first RCW is created here. Join one
        /// explicitly — S_OK / S_FALSE / RPC_E_CHANGED_MODE (0x80010106) are all fine; never
        /// CoUninitialize (the thread lives for the process lifetime and shares COM with WPF).</summary>
        public static void EnsureComApartment()
        {
            CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        }

        // objbase.h: COINIT_APARTMENTTHREADED = 0x2, COINIT_MULTITHREADED = 0x0. Passing the
        // literal 0 asked for the MTA while the comment (and the STA thread this runs on)
        // said apartment-threaded — on a thread that had not joined an apartment yet that
        // would have bound every RCW created here to the MTA.
        private const uint CoInitApartmentThreaded = 0x2;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    }
}
