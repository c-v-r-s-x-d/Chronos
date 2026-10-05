using System.Runtime.InteropServices;

namespace Chronos.Service.Sites;

public sealed class WindowsDnsCache : IDnsCache
{
    public bool Flush()
    {
        try
        {
            return DnsFlushResolverCache();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // The block is already written; a stale cache entry just expires with its TTL.
            return false;
        }
    }

    // Not LibraryImport: its generator emits unsafe code, not worth AllowUnsafeBlocks for one call.
#pragma warning disable SYSLIB1054
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
#pragma warning restore SYSLIB1054
}
