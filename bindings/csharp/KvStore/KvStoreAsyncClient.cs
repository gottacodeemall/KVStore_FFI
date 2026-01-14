// KvStoreAsyncClient.cs - Async KV Store client with native async/await support
//
// Uses TaskCompletionSource to bridge Rust callback-based async to C# async/await.
// Operations do NOT block the calling thread - work is done on Rust's tokio runtime.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using KvStore.Native;

namespace KvStore;

/// <summary>
/// Async-capable value reference for non-blocking operations.
/// </summary>
public sealed class AsyncValueRef : IDisposable
{
    private readonly IntPtr _client;
    private KvGetResult _result;
    private bool _disposed;

    internal AsyncValueRef(IntPtr client, KvGetResult result)
    {
        _client = client;
        _result = result;
        _disposed = false;
    }

    /// <summary>
    /// Gets whether the operation was successful.
    /// </summary>
    public bool IsSuccess => _result.ErrorCode == 0;

    /// <summary>
    /// Gets the error code (0 = success).
    /// </summary>
    public ErrorCode Error => (ErrorCode)_result.ErrorCode;

    /// <summary>
    /// Gets a zero-copy span view of the value.
    /// WARNING: This span is only valid while the AsyncValueRef is not disposed!
    /// </summary>
    public unsafe ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfDisposed();
        if (!IsSuccess || _result.Value.Ptr == IntPtr.Zero)
            return ReadOnlySpan<byte>.Empty;

        return new ReadOnlySpan<byte>(
            (void*)_result.Value.Ptr,
            (int)_result.Value.Len);
    }

    /// <summary>
    /// Gets a string copy using UTF-8 decoding.
    /// </summary>
    public string AsString()
    {
        var span = AsSpan();
        if (span.IsEmpty)
            return string.Empty;
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Gets the length of the value in bytes.
    /// </summary>
    public int Length => IsSuccess ? (int)_result.Value.Len : 0;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_client != IntPtr.Zero)
            {
                NativeMethodsAsync.kv_store_async_release_get(_client, ref _result);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AsyncValueRef));
    }
}

/// <summary>
/// High-performance async KV Store client.
/// All operations are non-blocking - work is offloaded to Rust's tokio runtime.
/// Thread-safe for concurrent use.
/// </summary>
public sealed class KvStoreAsyncClient : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;
    private readonly object _lock = new();
    
    // Track pending operations to prevent GC of callbacks
    private readonly ConcurrentDictionary<long, GCHandle> _pendingOperations = new();
    private long _operationId;

    // Cached delegates to prevent GC
    private readonly GetCallback _getCallback;
    private readonly SetCallback _setCallback;

    /// <summary>
    /// Creates a new async KV Store client.
    /// </summary>
    public KvStoreAsyncClient(KvStoreConfig? config = null)
    {
        config ??= new KvStoreConfig();
        
        // Cache delegates
        _getCallback = OnGetComplete;
        _setCallback = OnSetComplete;
        
        // Marshal the server URL to native memory
        var serverUrlBytes = Encoding.UTF8.GetBytes(config.ServerUrl + "\0");
        var serverUrlPtr = Marshal.AllocHGlobal(serverUrlBytes.Length);
        
        try
        {
            Marshal.Copy(serverUrlBytes, 0, serverUrlPtr, serverUrlBytes.Length);
            
            var nativeConfig = new KvStoreAsyncConfig
            {
                ServerUrl = serverUrlPtr,
                CacheTtlMs = config.CacheTtlMs,
                CacheCapacity = config.CacheCapacity,
                ConnectionPoolSize = config.ConnectionPoolSize,
                Padding = 0
            };
            
            _handle = NativeMethodsAsync.kv_store_async_init(ref nativeConfig);
            
            if (_handle == IntPtr.Zero)
            {
                throw new KvStoreException("Failed to initialize async KV Store client", ErrorCode.InternalError);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(serverUrlPtr);
        }
    }

    /// <summary>
    /// Gets a value asynchronously with zero-copy semantics.
    /// The returned AsyncValueRef MUST be disposed when done.
    /// </summary>
    /// <remarks>
    /// This method does NOT block the calling thread.
    /// On cache hit: returns immediately via task completion.
    /// On cache miss: HTTP fetch is done on Rust's tokio runtime.
    /// </remarks>
    public unsafe Task<AsyncValueRef> GetRefAsync(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var tcs = new TaskCompletionSource<AsyncValueRef>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Register cancellation
        var registration = cancellationToken.Register(() => 
            tcs.TrySetCanceled(cancellationToken));
        
        // Create operation context
        var context = new GetOperationContext
        {
            Client = _handle,
            TaskCompletionSource = tcs,
            CancellationRegistration = registration
        };
        
        // Allocate and track the operation
        var opId = Interlocked.Increment(ref _operationId);
        var gcHandle = GCHandle.Alloc(context);
        _pendingOperations[opId] = gcHandle;
        
        // Store opId in context for cleanup
        context.OperationId = opId;
        context.PendingOperations = _pendingOperations;
        
        fixed (byte* keyPtr = key)
        {
            NativeMethodsAsync.kv_store_get_async(
                _handle,
                (IntPtr)keyPtr,
                (nuint)key.Length,
                _getCallback,
                GCHandle.ToIntPtr(gcHandle));
        }
        
        return tcs.Task;
    }

    /// <summary>
    /// Gets a value asynchronously using a string key.
    /// </summary>
    public Task<AsyncValueRef> GetRefAsync(string key, CancellationToken cancellationToken = default)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return GetRefAsync(keyBytes, cancellationToken);
    }

    /// <summary>
    /// Gets a value as a string asynchronously.
    /// </summary>
    /// <exception cref="KeyNotFoundException">If the key doesn't exist.</exception>
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var valueRef = await GetRefAsync(key, cancellationToken);
        
        if (!valueRef.IsSuccess)
        {
            if (valueRef.Error == ErrorCode.KeyNotFound)
                throw new KeyNotFoundException(key);
            throw new KvStoreException($"Get failed: {valueRef.Error}", valueRef.Error);
        }
        
        return valueRef.AsString();
    }

    /// <summary>
    /// Tries to get a value asynchronously, returning null if not found.
    /// </summary>
    public async Task<string?> TryGetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var valueRef = await GetRefAsync(key, cancellationToken);
        return valueRef.IsSuccess ? valueRef.AsString() : null;
    }

    /// <summary>
    /// Sets a key-value pair asynchronously.
    /// </summary>
    /// <remarks>
    /// This method does NOT block the calling thread.
    /// The HTTP write is done on Rust's tokio runtime.
    /// </remarks>
    public unsafe Task SetAsync(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Register cancellation
        var registration = cancellationToken.Register(() => 
            tcs.TrySetCanceled(cancellationToken));
        
        // Create operation context
        var context = new SetOperationContext
        {
            TaskCompletionSource = tcs,
            CancellationRegistration = registration
        };
        
        // Allocate and track the operation
        var opId = Interlocked.Increment(ref _operationId);
        var gcHandle = GCHandle.Alloc(context);
        _pendingOperations[opId] = gcHandle;
        
        // Store opId in context for cleanup
        context.OperationId = opId;
        context.PendingOperations = _pendingOperations;
        
        fixed (byte* keyPtr = key)
        fixed (byte* valuePtr = value)
        {
            NativeMethodsAsync.kv_store_set_async(
                _handle,
                (IntPtr)keyPtr,
                (nuint)key.Length,
                (IntPtr)valuePtr,
                (nuint)value.Length,
                _setCallback,
                GCHandle.ToIntPtr(gcHandle));
        }
        
        return tcs.Task;
    }

    /// <summary>
    /// Sets a key-value pair using strings asynchronously.
    /// </summary>
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        return SetAsync(keyBytes, valueBytes, cancellationToken);
    }

    /// <summary>
    /// Clears the cache.
    /// </summary>
    public void ClearCache()
    {
        ThrowIfDisposed();
        NativeMethodsAsync.kv_store_async_cache_clear(_handle);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public KvCacheStats GetCacheStats()
    {
        ThrowIfDisposed();
        return NativeMethodsAsync.kv_store_async_cache_stats(_handle);
    }

    // Callback invoked from Rust when async Get completes
    private static void OnGetComplete(IntPtr userData, KvGetResult result)
    {
        var gcHandle = GCHandle.FromIntPtr(userData);
        var context = (GetOperationContext)gcHandle.Target!;
        
        try
        {
            // Dispose cancellation registration
            context.CancellationRegistration.Dispose();
            
            // Complete the task
            var valueRef = new AsyncValueRef(context.Client, result);
            context.TaskCompletionSource.TrySetResult(valueRef);
        }
        finally
        {
            // Cleanup tracking
            context.PendingOperations?.TryRemove(context.OperationId, out _);
            gcHandle.Free();
        }
    }

    // Callback invoked from Rust when async Set completes
    private static void OnSetComplete(IntPtr userData, int errorCode)
    {
        var gcHandle = GCHandle.FromIntPtr(userData);
        var context = (SetOperationContext)gcHandle.Target!;
        
        try
        {
            // Dispose cancellation registration
            context.CancellationRegistration.Dispose();
            
            // Complete the task
            if (errorCode == 0)
            {
                context.TaskCompletionSource.TrySetResult(true);
            }
            else
            {
                var error = (ErrorCode)errorCode;
                context.TaskCompletionSource.TrySetException(
                    new KvStoreException($"Set failed: {error}", error));
            }
        }
        finally
        {
            // Cleanup tracking
            context.PendingOperations?.TryRemove(context.OperationId, out _);
            gcHandle.Free();
        }
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
                    
                    // Free any remaining pending operations
                    foreach (var kvp in _pendingOperations)
                    {
                        if (kvp.Value.IsAllocated)
                            kvp.Value.Free();
                    }
                    _pendingOperations.Clear();
                    
                    if (_handle != IntPtr.Zero)
                    {
                        NativeMethodsAsync.kv_store_async_destroy(_handle);
                        _handle = IntPtr.Zero;
                    }
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KvStoreAsyncClient));
    }

    // Internal context for Get operations
    private class GetOperationContext
    {
        public IntPtr Client;
        public TaskCompletionSource<AsyncValueRef> TaskCompletionSource = null!;
        public CancellationTokenRegistration CancellationRegistration;
        public long OperationId;
        public ConcurrentDictionary<long, GCHandle>? PendingOperations;
    }

    // Internal context for Set operations
    private class SetOperationContext
    {
        public TaskCompletionSource<bool> TaskCompletionSource = null!;
        public CancellationTokenRegistration CancellationRegistration;
        public long OperationId;
        public ConcurrentDictionary<long, GCHandle>? PendingOperations;
    }
}
