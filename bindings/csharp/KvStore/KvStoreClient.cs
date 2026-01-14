// KvStoreClient.cs - Main client class for the KV Store SDK
//
// Provides both zero-copy (GetRef) and convenience (Get) APIs.

using System.Runtime.InteropServices;
using System.Text;
using KvStore.Native;

namespace KvStore;

/// <summary>
/// Configuration for the KV Store client.
/// </summary>
public sealed class KvStoreConfig
{
    /// <summary>Server URL (e.g., "http://127.0.0.1:8080")</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
    
    /// <summary>Cache TTL in milliseconds</summary>
    public ulong CacheTtlMs { get; set; } = 5000;
    
    /// <summary>Maximum cache capacity</summary>
    public ulong CacheCapacity { get; set; } = 10000;
    
    /// <summary>HTTP connection pool size</summary>
    public uint ConnectionPoolSize { get; set; } = 10;
}

/// <summary>
/// High-performance KV Store client with caching.
/// Thread-safe for concurrent use.
/// </summary>
public sealed class KvStoreClient : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new KV Store client.
    /// </summary>
    public KvStoreClient(KvStoreConfig? config = null)
    {
        config ??= new KvStoreConfig();
        
        // Marshal the server URL to native memory
        var serverUrlBytes = Encoding.UTF8.GetBytes(config.ServerUrl + "\0");
        var serverUrlPtr = Marshal.AllocHGlobal(serverUrlBytes.Length);
        
        try
        {
            Marshal.Copy(serverUrlBytes, 0, serverUrlPtr, serverUrlBytes.Length);
            
            var nativeConfig = new Native.KvStoreConfig
            {
                ServerUrl = serverUrlPtr,
                CacheTtlMs = config.CacheTtlMs,
                CacheCapacity = config.CacheCapacity,
                ConnectionPoolSize = config.ConnectionPoolSize,
                Padding = 0
            };
            
            _handle = NativeMethods.kv_store_init(ref nativeConfig);
            
            if (_handle == IntPtr.Zero)
            {
                throw new KvStoreException("Failed to initialize KV Store client", ErrorCode.InternalError);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(serverUrlPtr);
        }
    }

    /// <summary>
    /// Gets a zero-copy reference to a value.
    /// The returned ValueRef MUST be disposed when done.
    /// </summary>
    /// <remarks>
    /// This is the most efficient way to read values - no memory allocation
    /// for the value data itself.
    /// </remarks>
    public unsafe ValueRef GetRef(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        
        fixed (byte* keyPtr = key)
        {
            var result = NativeMethods.kv_store_get(
                _handle,
                (IntPtr)keyPtr,
                (nuint)key.Length);
            
            return new ValueRef(_handle, result);
        }
    }

    /// <summary>
    /// Gets a zero-copy reference to a value using a string key.
    /// </summary>
    public ValueRef GetRef(string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return GetRef(keyBytes);
    }

    /// <summary>
    /// Gets a value as a string (creates a copy).
    /// </summary>
    /// <exception cref="KeyNotFoundException">If the key doesn't exist.</exception>
    public string Get(string key)
    {
        using var valueRef = GetRef(key);
        
        if (!valueRef.IsSuccess)
        {
            if (valueRef.Error == ErrorCode.KeyNotFound)
                throw new KeyNotFoundException(key);
            throw new KvStoreException($"Get failed: {valueRef.Error}", valueRef.Error);
        }
        
        return valueRef.AsString();
    }

    /// <summary>
    /// Tries to get a value, returning null if not found.
    /// </summary>
    public string? TryGet(string key)
    {
        using var valueRef = GetRef(key);
        return valueRef.IsSuccess ? valueRef.AsString() : null;
    }

    /// <summary>
    /// Sets a key-value pair.
    /// </summary>
    public unsafe void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        
        fixed (byte* keyPtr = key)
        fixed (byte* valuePtr = value)
        {
            var result = NativeMethods.kv_store_set(
                _handle,
                (IntPtr)keyPtr,
                (nuint)key.Length,
                (IntPtr)valuePtr,
                (nuint)value.Length);
            
            if (result.ErrorCode != 0)
            {
                throw new KvStoreException($"Set failed: {(ErrorCode)result.ErrorCode}", (ErrorCode)result.ErrorCode);
            }
        }
    }

    /// <summary>
    /// Sets a key-value pair using strings.
    /// </summary>
    public void Set(string key, string value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Set(keyBytes, valueBytes);
    }

    /// <summary>
    /// Clears the cache.
    /// </summary>
    public void ClearCache()
    {
        ThrowIfDisposed();
        NativeMethods.kv_store_cache_clear(_handle);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public KvCacheStats GetCacheStats()
    {
        ThrowIfDisposed();
        return NativeMethods.kv_store_cache_stats(_handle);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (_handle != IntPtr.Zero)
                    {
                        NativeMethods.kv_store_destroy(_handle);
                        _handle = IntPtr.Zero;
                    }
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KvStoreClient));
    }
}
