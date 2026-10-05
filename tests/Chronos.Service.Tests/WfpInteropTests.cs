using System.Runtime.InteropServices;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

public sealed class WfpInteropTests
{
    [Theory]
    [InlineData(typeof(WfpInterop.FWP_VALUE0), 16)]
    [InlineData(typeof(WfpInterop.FWP_BYTE_BLOB), 16)]
    [InlineData(typeof(WfpInterop.FWPM_DISPLAY_DATA0), 16)]
    [InlineData(typeof(WfpInterop.FWPM_ACTION0), 20)]
    [InlineData(typeof(WfpInterop.FWPM_FILTER_CONDITION0), 40)]
    [InlineData(typeof(WfpInterop.FWPM_SUBLAYER0), 72)]
    [InlineData(typeof(WfpInterop.FWPM_SESSION0), 72)]
    [InlineData(typeof(WfpInterop.FWPM_PROVIDER0), 64)]
    [InlineData(typeof(WfpInterop.FWPM_FILTER0), 200)]
    [InlineData(typeof(WfpInterop.FWPM_FILTER_ENUM_TEMPLATE0), 72)]
    public void EveryStructHasTheSizeThePlatformExpectsOnX64(Type type, int expected)
    {
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    [Theory]
    [InlineData("filterKey", 0)]
    [InlineData("displayData", 16)]
    [InlineData("flags", 32)]
    [InlineData("providerKey", 40)]
    [InlineData("providerData", 48)]
    [InlineData("layerKey", 64)]
    [InlineData("subLayerKey", 80)]
    [InlineData("weight", 96)]
    [InlineData("numFilterConditions", 112)]
    [InlineData("filterCondition", 120)]
    [InlineData("action", 128)]
    [InlineData("rawContext", 152)]
    [InlineData("reserved", 168)]
    [InlineData("filterId", 176)]
    [InlineData("effectiveWeight", 184)]
    public void FilterFieldsSitWhereThePlatformLooksForThem(string field, int expected)
    {
        Assert.Equal(expected, (int)Marshal.OffsetOf<WfpInterop.FWPM_FILTER0>(field));
    }

    [Theory]
    [InlineData("providerKey", 0)]
    [InlineData("layerKey", 8)]
    [InlineData("enumType", 24)]
    [InlineData("flags", 28)]
    [InlineData("providerContextTemplate", 32)]
    [InlineData("numFilterConditions", 40)]
    [InlineData("filterCondition", 48)]
    [InlineData("actionMask", 56)]
    [InlineData("calloutKey", 64)]
    public void EnumTemplateFieldsSitWhereThePlatformLooksForThem(string field, int expected)
    {
        // The one structure of this set whose shape nothing else pins down, and enumeration reads
        // every field of it: enumType declared as a GUID rather than a UINT32 shifts the four
        // fields after it and turns filter enumeration into nonsense.
        Assert.Equal(expected, (int)Marshal.OffsetOf<WfpInterop.FWPM_FILTER_ENUM_TEMPLATE0>(field));
    }

    [Fact]
    public void DataTypeConstantsAreTheOnesThatDoNotFault()
    {
        // 8 is FWP_INT64 and 6 is FWP_INT16. FWP_INT64 holds a pointer, so a wrong constant makes the platform dereference a value and kill the process.
        Assert.Equal(3u, WfpInterop.FWP_UINT32);
        Assert.Equal(4u, WfpInterop.FWP_UINT64);
    }
}
