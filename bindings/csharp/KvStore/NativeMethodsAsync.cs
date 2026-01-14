// NativeMethodsAsync.cs - P/Invoke declarations for async Rust Core SDK
//
// These callbacks are invoked from Rust tokio worker threads.
// We use TaskCompletionSource to bridge to C# async/await.

using System.Runtime.InteropServices;

namespace KvStore.Native;

/// <summary>
/// Configuration for the async client.
/// Maps to Rust's KvStoreAsyncConfig.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KvStoreAsyncConfig
{
    public IntPtr ServerUrl;      // const char*
    public ulong CacheTtlMs;
    public ulong CacheCapacity;
    public uint ConnectionPoolSize;
    public uint Padding;
}

/// <summary>
/// Callback delegate for async Get completion.
/// Called from Rust worker thread.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void GetCallback(IntPtr userData, KvGetResult result);

/// <summary>
/// Callback delegate for async Set completion.
/// Called from Rust worker thread.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetCallback(IntPtr userData, int errorCode);

/// <summary>
/// P/Invoke declarations for the async Rust Core SDK.
/// </summary>
internal static class NativeMethodsAsync
{
    private const string DllName = "kv_core_sdk";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr kv_store_async_init(ref KvStoreAsyncConfig config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_async_destroy(IntPtr client);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_get_async(
        IntPtr client,
        IntPtr keyPtr,
        nuint keyLen,
        GetCallback callback,
        IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_set_async(
        IntPtr client,
        IntPtr keyPtr,
        nuint keyLen,
        IntPtr valuePtr,
        nuint valueLen,
        SetCallback callback,
        IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_async_release_get(
        IntPtr client,
        ref KvGetResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void kv_store_async_cache_clear(IntPtr client);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern KvCacheStats kv_store_async_cache_stats(IntPtr client);
}
