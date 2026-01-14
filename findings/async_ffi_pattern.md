# Async FFI Pattern - Non-Blocking Operations

## Overview

This document describes the async/non-blocking FFI pattern implemented for the KV Store SDK. The goal is to ensure that **no threads in C# or C++ are blocked** waiting for I/O operations.

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│   C# / C++      │     │   Rust FFI      │     │   Tokio        │
│   Application   │     │   (sync entry)  │     │   Runtime      │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │                       │                       │
         │ 1. call async         │                       │
         │────────────────────►  │                       │
         │                       │ 2. cache hit?         │
         │                       │────────────────────►  │
         │                       │    (fast path)        │
         │                       │                       │
         │ 3. callback (hit)     │                       │
         │◄────────────────────  │                       │
         │                       │                       │
         │    OR (cache miss)    │                       │
         │                       │ 4. spawn async task   │
         │ returns immediately   │────────────────────►  │
         │◄────────────────────  │                       │
         │                       │     ┌─────────────────┤
         │                       │     │ HTTP request    │
         │                       │     │ (non-blocking)  │
         │                       │     └─────────────────┤
         │                       │                       │
         │ 5. callback (miss)    │◄──────────────────────┤
         │◄────────────────────  │  worker thread        │
         │                       │                       │
```

## Rust Implementation

### Global Tokio Runtime

```rust
static ASYNC_RUNTIME: Lazy<Runtime> = Lazy::new(|| {
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(4)
        .enable_all()
        .build()
        .expect("Failed to create tokio runtime")
});
```

- Lazily initialized on first async call
- Multi-threaded with 4 worker threads
- Shared across all async clients

### Callback-Based FFI

```rust
pub type GetCallback = extern "C" fn(user_data: *mut (), result: KvGetResult);

#[no_mangle]
pub unsafe extern "C" fn kv_store_get_async(
    client: *mut KvStoreAsyncClient,
    key_ptr: *const u8,
    key_len: usize,
    callback: GetCallback,
    user_data: *mut (),
) {
    // Fast path: cache hit - call immediately
    if let Some(cached_value) = client.cache.get(&key) {
        callback(user_data, KvGetResult::success(cached_value));
        return;
    }

    // Slow path: spawn async task
    ASYNC_RUNTIME.spawn(async move {
        let result = http_client.get(&key).await;
        callback(user_data_ptr as *mut (), result);  // Called from worker thread
    });
}
```

### Key Design Decisions

1. **Cache hits are synchronous**: No overhead for the common case
2. **Cache misses spawn tasks**: Work is offloaded to tokio runtime
3. **Callbacks from worker threads**: C#/C++ must handle thread-safety
4. **User data pointer**: Allows passing context through the FFI boundary

## C# Implementation

### TaskCompletionSource Bridge

```csharp
public unsafe Task<AsyncValueRef> GetRefAsync(ReadOnlySpan<byte> key, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<AsyncValueRef>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    
    var context = new GetOperationContext
    {
        Client = _handle,
        TaskCompletionSource = tcs,
        CancellationRegistration = ct.Register(() => tcs.TrySetCanceled(ct))
    };
    
    var gcHandle = GCHandle.Alloc(context);
    
    NativeMethodsAsync.kv_store_get_async(
        _handle,
        keyPtr,
        keyLen,
        _getCallback,
        GCHandle.ToIntPtr(gcHandle));
    
    return tcs.Task;
}
```

### Callback Handler

```csharp
private static void OnGetComplete(IntPtr userData, KvGetResult result)
{
    var gcHandle = GCHandle.FromIntPtr(userData);
    var context = (GetOperationContext)gcHandle.Target!;
    
    try
    {
        context.CancellationRegistration.Dispose();
        var valueRef = new AsyncValueRef(context.Client, result);
        context.TaskCompletionSource.TrySetResult(valueRef);
    }
    finally
    {
        gcHandle.Free();
    }
}
```

### Key Design Decisions

1. **RunContinuationsAsynchronously**: Prevents callback from blocking tokio workers
2. **GCHandle tracking**: Prevents GC of pending operations
3. **CancellationToken support**: Proper async cancellation pattern
4. **Cached delegates**: Prevents repeated delegate allocation

## C++ Implementation

### std::future Bridge

```cpp
[[nodiscard]] std::future<AsyncValueRef> GetRefAsync(std::string_view key) {
    auto* ctx = new detail::GetContext(client_);
    auto future = ctx->promise.get_future();
    
    kv_store_get_async(
        client_,
        reinterpret_cast<const uint8_t*>(key.data()),
        key.size(),
        detail::get_callback,
        ctx
    );
    
    return future;
}
```

### Callback Handler

```cpp
inline void get_callback(void* user_data, KvGetResult result) {
    auto* ctx = static_cast<GetContext*>(user_data);
    try {
        AsyncValueRef ref(ctx->client, result);
        ctx->promise.set_value(std::move(ref));
    } catch (...) {
        ctx->promise.set_exception(std::current_exception());
    }
    delete ctx;
}
```

### Key Design Decisions

1. **std::promise/std::future**: Standard C++ async pattern
2. **Context allocation**: `new`/`delete` for context passing
3. **Exception propagation**: Via `set_exception`
4. **Move semantics**: Efficient transfer of AsyncValueRef

## Thread Safety Considerations

### Callback Thread

Callbacks are invoked from:
- **Cache hit**: Same thread as caller (synchronous)
- **Cache miss**: Tokio worker thread (different thread)

### C# Safety

```csharp
// TaskCreationOptions.RunContinuationsAsynchronously ensures
// continuations don't block the callback thread
var tcs = new TaskCompletionSource<T>(
    TaskCreationOptions.RunContinuationsAsynchronously);
```

### C++ Safety

```cpp
// std::promise is thread-safe for set_value/set_exception
// called from any thread
ctx->promise.set_value(std::move(ref));
```

## Memory Management

### Zero-Copy Semantics

The async pattern preserves zero-copy:
1. Cache returns `Arc<String>`
2. FFI leaks Arc via `Arc::into_raw()`
3. Binding holds reference to raw pointer
4. Binding calls `release` to reclaim Arc

### Lifecycle

```
Rust: Arc::into_raw() ──────────────────────────────────────► Arc::from_raw()
                       │                                     ▲
                       │ callback(result)                    │ release(result)
                       ▼                                     │
C#/C++:            AsyncValueRef created ─────────────────► Dispose()/~Dtor()
```

## Performance Characteristics

### Benchmark Results (January 13, 2026)

#### C++ Async FFI Overhead

| Value Size | 1 Task | 16 Tasks | 64 Tasks | 256 Tasks |
|------------|--------|----------|----------|-----------|
| 64B        | 445 ns | 829 ns   | 815 ns   | 784 ns    |
| 1KB        | 398 ns | 811 ns   | 714 ns   | 825 ns    |
| 16KB       | 413 ns | 653 ns   | 798 ns   | 1,017 ns  |
| 256KB      | 418 ns | 675 ns   | 844 ns   | N/A       |

**Verdict:** ✅ PASS - All under 5μs target

#### C# Async FFI Overhead

| Value Size | 1 Task | 16 Tasks | 64 Tasks | 256 Tasks |
|------------|--------|----------|----------|-----------|
| 64B        | 817 ns | 3,752 ns | 2,939 ns | 4,065 ns  |
| 1KB        | 859 ns | 2,705 ns | 2,532 ns | 19,349 ns |
| 16KB       | 1,317 ns| 2,292 ns| 4,207 ns | 3,174 ns  |
| 256KB      | 804 ns | 2,174 ns | 2,585 ns | N/A       |

**Verdict:** ⚠️ PARTIAL PASS - Most under 5μs, some spikes at extreme concurrency

#### Async vs Sync Comparison

| Language | Single-Thread Async | Single-Thread Sync | Overhead |
|----------|--------------------|--------------------|----------|
| C++      | ~400 ns            | ~200 ns            | ~2x      |
| C#       | ~850 ns            | ~250 ns            | ~3.4x    |

| Language | High Concurrency Async | High Concurrency Sync | Overhead |
|----------|------------------------|-----------------------|----------|
| C++      | ~800-1000 ns           | ~500-600 ns           | ~1.5-2x  |
| C#       | ~3000-4000 ns          | ~500-600 ns           | ~5-7x    |

### Key Findings

1. **C++ async is much faster than C#**: ~2x lower overhead due to lighter runtime
2. **C# async has higher variance**: TaskCompletionSource and GC add unpredictability
3. **Both maintain zero-copy**: Value size doesn't affect async latency
4. **Cache hits are synchronous**: Fast path avoids async scheduling overhead

| Scenario | Latency | Thread Blocking |
|----------|---------|-----------------|
| Cache hit | ~400-850ns | None |
| Cache miss (callback) | ~1-5ms (HTTP) | None |
| Memory overhead | 1 GCHandle/context per op | Cleaned on completion |

## Usage Examples

### C# async/await

```csharp
var client = new KvStoreAsyncClient(config);

// Non-blocking Get
string value = await client.GetAsync("mykey");

// Parallel requests
var tasks = keys.Select(k => client.GetAsync(k));
var values = await Task.WhenAll(tasks);

// Zero-copy pattern
await using var valueRef = await client.GetRefAsync("mykey");
ProcessData(valueRef.AsSpan());  // No copy!
```

### C++ std::future

```cpp
kvstore::KvStoreAsyncClient client(config);

// Non-blocking Get
auto future = client.GetRefAsync("mykey");
// ... do other work ...
auto ref = future.get();  // Wait only when needed
std::cout << ref.view() << std::endl;

// Fire-and-forget Set
kvstore::FireAndForget ff(client);
ff.Set("key", "value");  // Returns immediately, no waiting
```

## Comparison: Blocking vs Async

### Blocking Pattern

```
Thread 1: |──Get──────────────────────────────────────────|──Continue──|
                    ↑ blocked during HTTP request ↑
```

### Async Pattern

```
Thread 1: |──GetAsync(start)──|──Other work──────|──Get result──|
Tokio:                        |──HTTP request────|
                              ↑ no blocking ↑
```

## Best Practices

1. **Use async for I/O-bound operations** - Prefer async when latency tolerance allows
2. **Use sync for cache-hot paths** - If you know data is cached, sync may be faster
3. **Dispose/Release promptly** - Don't hold AsyncValueRef longer than needed
4. **Handle cancellation** - Use CancellationToken in C#
5. **Don't block on futures in callbacks** - Can cause deadlocks
