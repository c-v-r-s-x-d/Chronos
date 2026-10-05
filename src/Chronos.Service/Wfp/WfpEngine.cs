using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Wfp;

/// <summary>Transport scope of a block.</summary>
public enum WfpProtocol
{
    AnyTransport,

    /// <summary>UDP/443 only (QUIC).</summary>
    QuicUdp443,

    /// <summary>TCP/443 only, leaving port 53 and the rest open.</summary>
    Tcp443,
}

public readonly record struct WfpFilterInfo(ulong Id, string Name);

/// <summary>
/// What a full removal took off the machine. The provider and sub-layer are reported too, so
/// `chronos clean` does not claim nothing was removed when it deleted them.
/// </summary>
public readonly record struct WfpRemoval(int Filters, bool ProviderRemoved, bool SubLayerRemoved)
{
    public bool AnythingRemoved => Filters > 0 || ProviderRemoved || SubLayerRemoved;
}

/// <summary>
/// The filter engine as the enforcer uses it; a seam for testing without administrator rights.
/// <see cref="WfpEngine.Open"/> is absent: the session belongs to whoever created the engine.
/// </summary>
public interface IWfpEngine : IDisposable
{
    void EnsureObjects();

    ulong AddBlock(IPAddress address, WfpProtocol protocol, string name);

    IReadOnlyList<WfpFilterInfo> ListOwnFilters();

    void RemoveFilters(IEnumerable<ulong> ids);

    WfpRemoval RemoveEverything();
}

/// <summary>
/// Which provider and sub-layer a <see cref="WfpEngine"/> files its objects under; also what
/// <see cref="WfpEngine.ListOwnFilters"/> and <see cref="WfpEngine.RemoveEverything"/> select by.
/// </summary>
internal sealed record WfpObjectIdentity(
    Guid ProviderKey, Guid SubLayerKey, string ProviderName, string SubLayerName)
{
    /// <summary>The identity the service and the CLI share, so the CLI cleanup can find the service's objects.</summary>
    public static WfpObjectIdentity Production { get; } = new(
        new Guid("0c47eea0-25e2-4eec-9a72-fb546aafd643"),
        new Guid("ec1cae1b-490e-4c9d-8d97-74f3a33d7ee1"),
        "Chronos",
        "Chronos site block");
}

/// <summary>
/// Owns every WFP object Chronos creates: engine handle, provider, sub-layer and filters. Nothing
/// above this type touches <see cref="WfpInterop"/>, so all handles and allocations are released here.
/// </summary>
public sealed class WfpEngine : IWfpEngine
{
    // FWPM_CONDITION_IP_PROTOCOL takes the IANA protocol number, not a WFP constant.
    private const byte ProtocolTcp = 6;
    private const byte ProtocolUdp = 17;
    private const ushort PortHttps = 443;

    // FWP_DATA_TYPE values, from the enumeration FWP_EMPTY, UINT8, UINT16, UINT32, UINT64, INT8,
    // INT16, INT32, INT64, ... BYTE_ARRAY16_TYPE (11). UINT8/UINT16 are embedded in the union, not pointers.
    private const uint FwpUint8 = 1;
    private const uint FwpUint16 = 2;
    private const uint FwpByteArray16Type = 11;

    // Maximum, so a Chronos block outranks other filters at the same layer.
    private const ulong FilterWeight = ulong.MaxValue;

    private readonly bool _persistent;
    private readonly ILogger<WfpEngine> _logger;
    private readonly WfpObjectIdentity _identity;
    private IntPtr _engineHandle;

    public WfpEngine(bool persistent, ILogger<WfpEngine> logger)
        : this(persistent, logger, WfpObjectIdentity.Production)
    {
    }

    // Internal, for tests that need their own identity. Selection is by provider key, not session,
    // so tests sharing the production key would list and delete a live Chronos session's filters.
    internal WfpEngine(bool persistent, ILogger<WfpEngine> logger, WfpObjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        _persistent = persistent;
        _logger = logger;
        _identity = identity;
    }

    public void Open()
    {
        var session = new WfpInterop.FWPM_SESSION0
        {
            sessionKey = Guid.NewGuid(),
            flags = _persistent ? 0u : WfpInterop.FWPM_SESSION_FLAG_DYNAMIC,
        };

        var status = WfpInterop.FwpmEngineOpen0(
            null, WfpInterop.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out var handle);
        ThrowIfFailed(status, nameof(WfpInterop.FwpmEngineOpen0));

        _engineHandle = handle;
        _logger.LogDebug(
            "WFP engine session opened ({Session}).", _persistent ? "persistent" : "dynamic");
    }

    public void EnsureObjects()
    {
        EnsureOpen();
        AddProviderIfMissing();
        AddSubLayerIfMissing();
    }

    public ulong AddBlock(IPAddress address, WfpProtocol protocol, string name)
    {
        EnsureOpen();

        var allocations = new List<IntPtr>();
        try
        {
            var conditions = BuildConditions(address, protocol, allocations);
            var conditionsPtr = MarshalConditions(conditions, allocations);
            var providerKeyPtr = AllocGuid(_identity.ProviderKey, allocations);
            var namePtr = AllocString(name, allocations);
            var weightPtr = AllocWeight(FilterWeight, allocations);

            var filter = new WfpInterop.FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new WfpInterop.FWPM_DISPLAY_DATA0 { name = namePtr },
                flags = _persistent ? WfpInterop.FWPM_FILTER_FLAG_PERSISTENT : 0u,
                providerKey = providerKeyPtr,
                layerKey = address.AddressFamily == AddressFamily.InterNetworkV6
                    ? WfpInterop.LayerAleAuthConnectV6
                    : WfpInterop.LayerAleAuthConnectV4,
                subLayerKey = _identity.SubLayerKey,
                weight = new WfpInterop.FWP_VALUE0 { type = WfpInterop.FWP_UINT64, value = (ulong)weightPtr },
                numFilterConditions = (uint)conditions.Length,
                filterCondition = conditionsPtr,
                action = new WfpInterop.FWPM_ACTION0 { type = WfpInterop.FWP_ACTION_BLOCK },
            };

            var status = WfpInterop.FwpmFilterAdd0(_engineHandle, ref filter, IntPtr.Zero, out var id);
            ThrowIfFailed(status, nameof(WfpInterop.FwpmFilterAdd0));
            return id;
        }
        finally
        {
            FreeAll(allocations);
        }
    }

    // The layerKey of the enum template is required: GUID_NULL fails with FWP_E_INVALID_PARAMETER
    // (0x80320004), so each layer is enumerated in turn.
    private static readonly Guid[] OwnLayers = [WfpInterop.LayerAleAuthConnectV4, WfpInterop.LayerAleAuthConnectV6];

    public IReadOnlyList<WfpFilterInfo> ListOwnFilters()
    {
        EnsureOpen();

        var results = new List<WfpFilterInfo>();
        foreach (var layer in OwnLayers)
        {
            results.AddRange(ListOwnFiltersInLayer(layer));
        }

        return results;
    }

    private List<WfpFilterInfo> ListOwnFiltersInLayer(Guid layerKey)
    {
        var results = new List<WfpFilterInfo>();
        var providerKeyPtr = IntPtr.Zero;
        var templatePtr = IntPtr.Zero;
        var enumHandle = IntPtr.Zero;
        try
        {
            providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(_identity.ProviderKey, providerKeyPtr, false);

            var template = new WfpInterop.FWPM_FILTER_ENUM_TEMPLATE0
            {
                providerKey = providerKeyPtr,
                layerKey = layerKey,
                actionMask = uint.MaxValue,
            };
            templatePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WfpInterop.FWPM_FILTER_ENUM_TEMPLATE0>());
            Marshal.StructureToPtr(template, templatePtr, false);

            var openStatus = WfpInterop.FwpmFilterCreateEnumHandle0(_engineHandle, templatePtr, out enumHandle);
            ThrowIfFailed(openStatus, nameof(WfpInterop.FwpmFilterCreateEnumHandle0));

            const uint batchSize = 128;
            uint returned;
            do
            {
                var entriesPtr = IntPtr.Zero;
                try
                {
                    var enumStatus = WfpInterop.FwpmFilterEnum0(
                        _engineHandle, enumHandle, batchSize, out entriesPtr, out returned);
                    ThrowIfFailed(enumStatus, nameof(WfpInterop.FwpmFilterEnum0));

                    for (var i = 0; i < returned; i++)
                    {
                        var entryPtr = Marshal.ReadIntPtr(entriesPtr, i * IntPtr.Size);
                        var filter = Marshal.PtrToStructure<WfpInterop.FWPM_FILTER0>(entryPtr);

                        var name = Marshal.PtrToStringUni(filter.displayData.name) ?? string.Empty;
                        results.Add(new WfpFilterInfo(filter.filterId, name));
                    }
                }
                finally
                {
                    if (entriesPtr != IntPtr.Zero)
                    {
                        WfpInterop.FwpmFreeMemory0(ref entriesPtr);
                    }
                }
            }
            while (returned == batchSize);
        }
        finally
        {
            if (enumHandle != IntPtr.Zero)
            {
                WfpInterop.FwpmFilterDestroyEnumHandle0(_engineHandle, enumHandle);
            }

            if (templatePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(templatePtr);
            }

            if (providerKeyPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(providerKeyPtr);
            }
        }

        return results;
    }

    public void RemoveFilters(IEnumerable<ulong> ids)
    {
        EnsureOpen();

        foreach (var id in ids)
        {
            var status = WfpInterop.FwpmFilterDeleteById0(_engineHandle, id);
            ThrowIfFailed(status, nameof(WfpInterop.FwpmFilterDeleteById0));
        }
    }

    public WfpRemoval RemoveEverything()
    {
        var filters = ListOwnFilters();
        RemoveFilters(filters.Select(filter => filter.Id));

        // The persistent provider and sub-layer are removed too. Dependency order: filters, then
        // the sub-layer, then the provider.
        var subLayerKey = _identity.SubLayerKey;
        var subLayerRemoved = ThrowUnlessAbsent(
            WfpInterop.FwpmSubLayerDeleteByKey0(_engineHandle, ref subLayerKey),
            nameof(WfpInterop.FwpmSubLayerDeleteByKey0),
            WfpInterop.FWP_E_SUBLAYER_NOT_FOUND);

        var providerKey = _identity.ProviderKey;
        var providerRemoved = ThrowUnlessAbsent(
            WfpInterop.FwpmProviderDeleteByKey0(_engineHandle, ref providerKey),
            nameof(WfpInterop.FwpmProviderDeleteByKey0),
            WfpInterop.FWP_E_PROVIDER_NOT_FOUND);

        return new WfpRemoval(filters.Count, providerRemoved, subLayerRemoved);
    }

    public void Dispose()
    {
        if (_engineHandle != IntPtr.Zero)
        {
            WfpInterop.FwpmEngineClose0(_engineHandle);
            _engineHandle = IntPtr.Zero;
        }
    }

    private void EnsureOpen()
    {
        if (_engineHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The WFP engine session is not open; call Open() first.");
        }
    }

    private void AddProviderIfMissing()
    {
        var namePtr = Marshal.StringToHGlobalUni(_identity.ProviderName);
        try
        {
            var provider = new WfpInterop.FWPM_PROVIDER0
            {
                providerKey = _identity.ProviderKey,
                displayData = new WfpInterop.FWPM_DISPLAY_DATA0 { name = namePtr },
                flags = _persistent ? WfpInterop.FWPM_PROVIDER_FLAG_PERSISTENT : 0u,
            };

            var status = WfpInterop.FwpmProviderAdd0(_engineHandle, ref provider, IntPtr.Zero);
            ThrowUnlessAlreadyExists(status, nameof(WfpInterop.FwpmProviderAdd0));
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private void AddSubLayerIfMissing()
    {
        var namePtr = Marshal.StringToHGlobalUni(_identity.SubLayerName);
        var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(_identity.ProviderKey, providerKeyPtr, false);

            var subLayer = new WfpInterop.FWPM_SUBLAYER0
            {
                subLayerKey = _identity.SubLayerKey,
                displayData = new WfpInterop.FWPM_DISPLAY_DATA0 { name = namePtr },
                flags = _persistent ? WfpInterop.FWPM_SUBLAYER_FLAG_PERSISTENT : 0u,
                providerKey = providerKeyPtr,
            };

            var status = WfpInterop.FwpmSubLayerAdd0(_engineHandle, ref subLayer, IntPtr.Zero);
            ThrowUnlessAlreadyExists(status, nameof(WfpInterop.FwpmSubLayerAdd0));
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }

    private static WfpInterop.FWPM_FILTER_CONDITION0[] BuildConditions(
        IPAddress address, WfpProtocol protocol, List<IntPtr> allocations)
    {
        var conditions = new List<WfpInterop.FWPM_FILTER_CONDITION0> { BuildAddressCondition(address, allocations) };

        // Equality against single values only, never a range or mask.
        if (TransportOf(protocol) is { } transport)
        {
            conditions.Add(new WfpInterop.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpInterop.ConditionIpProtocol,
                matchType = WfpInterop.FWP_MATCH_EQUAL,
                conditionValue = new WfpInterop.FWP_VALUE0 { type = FwpUint8, value = transport },
            });
            conditions.Add(new WfpInterop.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpInterop.ConditionIpRemotePort,
                matchType = WfpInterop.FWP_MATCH_EQUAL,
                conditionValue = new WfpInterop.FWP_VALUE0 { type = FwpUint16, value = PortHttps },
            });
        }

        return [.. conditions];
    }

    private static byte? TransportOf(WfpProtocol protocol) => protocol switch
    {
        WfpProtocol.QuicUdp443 => ProtocolUdp,
        WfpProtocol.Tcp443 => ProtocolTcp,
        _ => null,
    };

    private static WfpInterop.FWPM_FILTER_CONDITION0 BuildAddressCondition(IPAddress address, List<IntPtr> allocations)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes(); // raw 16 bytes; no byte order
            var bytesPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
            allocations.Add(bytesPtr);

            return new WfpInterop.FWPM_FILTER_CONDITION0
            {
                fieldKey = WfpInterop.ConditionIpRemoteAddress,
                matchType = WfpInterop.FWP_MATCH_EQUAL,
                conditionValue = new WfpInterop.FWP_VALUE0 { type = FwpByteArray16Type, value = (ulong)bytesPtr },
            };
        }

        // GetAddressBytes is network byte order; the platform wants a UINT32 in host byte order.
        var octets = address.GetAddressBytes();
        var hostOrder = ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];

        return new WfpInterop.FWPM_FILTER_CONDITION0
        {
            fieldKey = WfpInterop.ConditionIpRemoteAddress,
            matchType = WfpInterop.FWP_MATCH_EQUAL,
            conditionValue = new WfpInterop.FWP_VALUE0 { type = WfpInterop.FWP_UINT32, value = hostOrder },
        };
    }

    private static IntPtr MarshalConditions(WfpInterop.FWPM_FILTER_CONDITION0[] conditions, List<IntPtr> allocations)
    {
        var elementSize = Marshal.SizeOf<WfpInterop.FWPM_FILTER_CONDITION0>();
        var arrayPtr = Marshal.AllocHGlobal(elementSize * conditions.Length);
        allocations.Add(arrayPtr);

        for (var i = 0; i < conditions.Length; i++)
        {
            Marshal.StructureToPtr(conditions[i], arrayPtr + (i * elementSize), false);
        }

        return arrayPtr;
    }

    private static IntPtr AllocGuid(Guid value, List<IntPtr> allocations)
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(value, ptr, false);
        allocations.Add(ptr);
        return ptr;
    }

    private static IntPtr AllocString(string value, List<IntPtr> allocations)
    {
        var ptr = Marshal.StringToHGlobalUni(value);
        allocations.Add(ptr);
        return ptr;
    }

    /// <summary>FWP_UINT64 stores a pointer to the value, not the value itself.</summary>
    private static IntPtr AllocWeight(ulong value, List<IntPtr> allocations)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(ulong));
        Marshal.WriteInt64(ptr, unchecked((long)value));
        allocations.Add(ptr);
        return ptr;
    }

    private static void FreeAll(List<IntPtr> allocations)
    {
        foreach (var ptr in allocations)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void ThrowIfFailed(uint status, string entryPoint)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{entryPoint} failed with 0x{status:X8}.");
        }
    }

    /// <summary>
    /// "Already gone" is success for a delete. The generic FWP_E_NOT_FOUND is accepted with the
    /// object's own code, since the platform does not document which it returns. Returns whether
    /// something was deleted.
    /// </summary>
    private static bool ThrowUnlessAbsent(uint status, string entryPoint, uint absent)
    {
        if (status == 0)
        {
            return true;
        }

        if (status == absent || status == WfpInterop.FWP_E_NOT_FOUND)
        {
            return false;
        }

        throw new InvalidOperationException($"{entryPoint} failed with 0x{status:X8}.");
    }

    /// <summary>FWP_E_ALREADY_EXISTS is expected when EnsureObjects runs again; any other failure throws.</summary>
    private static void ThrowUnlessAlreadyExists(uint status, string entryPoint)
    {
        if (status == 0 || status == WfpInterop.FWP_E_ALREADY_EXISTS)
        {
            return;
        }

        throw new InvalidOperationException($"{entryPoint} failed with 0x{status:X8}.");
    }
}
