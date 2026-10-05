using System.Runtime.InteropServices;

namespace Chronos.Service.Wfp;

/// <summary>
/// Structs and entry points of the Windows Filtering Platform. The field offsets in
/// <see cref="FWPM_FILTER0"/> were confirmed against the running platform.
/// </summary>
internal static class WfpInterop
{
    public const uint RPC_C_AUTHN_WINNT = 10;

    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;
    public const uint FWPM_FILTER_FLAG_PERSISTENT = 0x00000001;
    public const uint FWPM_PROVIDER_FLAG_PERSISTENT = 0x00000001;
    public const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x00000001;

    /// <summary>FWP_ACTION_BLOCK with FWP_ACTION_FLAG_TERMINATING.</summary>
    public const uint FWP_ACTION_BLOCK = 0x00000001 | 0x00001000;

    // FWP_DATA_TYPE. Not 8 and 6: those are FWP_INT64 and FWP_INT16, and FWP_INT64 holds a pointer
    // the platform dereferences.
    public const uint FWP_UINT32 = 3;
    public const uint FWP_UINT64 = 4;

    public const uint FWP_MATCH_EQUAL = 0;

    public const uint FWP_E_NULL_DISPLAY_NAME = 0x80320023;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;

    // The "nothing to delete" answers, returned by a clean machine and by a second `chronos clean`.
    // The FWP_E_*_NOT_FOUND codes run CALLOUT, CONDITION, FILTER, LAYER, PROVIDER, PROVIDER_CONTEXT,
    // SUBLAYER, NOT_FOUND, then ALREADY_EXISTS at 9; PROVIDER_NOT_FOUND (5) and ALREADY_EXISTS (9)
    // are confirmed on the platform, which pins the codes between them.
    public const uint FWP_E_PROVIDER_NOT_FOUND = 0x80320005;
    public const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    public const uint FWP_E_NOT_FOUND = 0x80320008;

    public static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid ConditionIpRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    public static readonly Guid ConditionIpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    public static readonly Guid ConditionIpRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint size;
        private readonly uint padding;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_VALUE0
    {
        public uint type;
        private readonly uint padding;
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        private readonly uint padding;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
        private readonly int padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_PROVIDER0
    {
        public Guid providerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        private readonly uint padding;
        public FWP_BYTE_BLOB providerData;
        public IntPtr serviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        private readonly uint padding;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
        private readonly ushort padding2;
        private readonly uint padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        private readonly uint padding;
        public FWP_VALUE0 conditionValue;
    }

    // A GUID aligns to four, so this is twenty bytes, not twenty-four.
    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_ACTION0
    {
        public uint type;
        public Guid filterType;
    }

    // Explicit layout: the rawContext/providerContextKey union is GUID-sized and eight-aligned.
    [StructLayout(LayoutKind.Explicit, Size = 200)]
    public struct FWPM_FILTER0
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public FWP_BYTE_BLOB providerData;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public FWP_VALUE0 weight;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public IntPtr filterCondition;
        [FieldOffset(128)] public FWPM_ACTION0 action;
        [FieldOffset(152)] public ulong rawContext;
        [FieldOffset(168)] public IntPtr reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public FWP_VALUE0 effectiveWeight;
    }

    // enumType is a UINT32 enum, not a GUID; declaring a GUID shifts the rest and breaks enumeration.
    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_ENUM_TEMPLATE0
    {
        public IntPtr providerKey;
        public Guid layerKey;
        public uint enumType;
        public uint flags;
        public IntPtr providerContextTemplate;
        public uint numFilterConditions;
        private readonly uint padding;
        public IntPtr filterCondition;
        public uint actionMask;
        private readonly uint padding2;
        public IntPtr calloutKey;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmEngineOpen0(
        string? serverName, uint authnService, IntPtr authIdentity, ref FWPM_SESSION0 session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmProviderAdd0(IntPtr engineHandle, ref FWPM_PROVIDER0 provider, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    // Both take the key as const GUID*, which `ref Guid` marshals to.
    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern void FwpmFreeMemory0(ref IntPtr p);
}
