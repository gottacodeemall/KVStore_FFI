// NativeMethods.cs - P/Invoke declarations for Rust Core SDK
//
// CRITICAL: These structs must match the Rust #[repr(C)] definitions exactly.
// Any mismatch will cause memory corruption or crashes.

using System.Runtime.InteropServices;

namespace KvStore.Native;

/// <summary>
/// String reference - zero-copy view into Rust memory.
/// Maps to Rust's KvStringRef.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KvStringRef
{
    public IntPtr Ptr;
    public nuint Len;
}

/// <summary>
/// Get operation result - zero-copy.
/// Maps to Rust's KvGetResult.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KvGetResult
{
    public KvStringRef Value;
    public int ErrorCode;
    public int Padding;
    public IntPtr ArcHandle;  // Internal: holds Arc to keep value alive
}

/// <summary>
/// Set operation result.
/// Maps to Rust's KvSetResult.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KvSetResult
{
    public int ErrorCode;
    public int Padding;
}

/// <summary>
/// Configuration for the client.
/// Maps to Rust's KvStoreConfig.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KvStoreConfig
{
    public IntPtr ServerUrl;      // const char*
    public ulong CacheTtlMs;
    public ulong CacheCapacity;
    public uint ConnectionPoolSize;
    public uint Padding;
}

/// <summary>
/// Cache statistics.
/// Maps to Rust's KvCacheStats.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KvCacheStats
{
    public ulong Hits;
    public ulong Misses;
    public ulong CurrentSize;

    public double HitRate => Hits + Misses > 0 
        ? (double)Hits / (Hits + Misses) 
        : 0.0;
}

/// <summary>
/// P/Invoke declarations for the Rust Core SDK.
/// </summary>
internal static class NativeMethods
{
    private const string DllName = "kv_core_sdk";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr kv_store_init(ref KvStoreConfig config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_destroy(IntPtr client);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern KvGetResult kv_store_get(
        IntPtr client,
        IntPtr keyPtr,
        nuint keyLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_release_get(
        IntPtr client,
        ref KvGetResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern KvSetResult kv_store_set(
        IntPtr client,
        IntPtr keyPtr,
        nuint keyLen,
        IntPtr valuePtr,
        nuint valueLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_cache_clear(IntPtr client);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern KvCacheStats kv_store_cache_stats(IntPtr client);
}
