# FFI Memory Management - Allocation & Deallocation Patterns

**Date:** January 13, 2026  
**Scope:** C++ ↔ Rust ↔ C# memory ownership across FFI boundaries

---

## Executive Summary

Cross-language memory management follows a **golden rule**:

> **The allocator must be the deallocator.**

Memory allocated in Rust must be freed by Rust. Memory allocated in C++/C# must be freed by C++/C#. Violating this rule causes crashes, corruption, or leaks.

---

## The Two Directions of Data Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        FFI MEMORY OWNERSHIP                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   DIRECTION A: Binding → Rust (e.g., Set key/value)                        │
│   ════════════════════════════════════════════════                         │
│   • Binding allocates                                                       │
│   • Rust BORROWS (read-only view)                                          │
│   • Binding deallocates (after FFI call returns)                           │
│                                                                             │
│   DIRECTION B: Rust → Binding (e.g., Get value)                            │
│   ════════════════════════════════════════════════                         │
│   • Rust allocates (in cache)                                              │
│   • Binding BORROWS (zero-copy view)                                       │
│   • Rust deallocates (when binding calls release)                          │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Direction A: Allocated in Binding, Used in Rust

### Scenario: `Set(key, value)` Operation

The binding allocates the key and value strings, Rust reads them, and the binding frees them after the call.

### Memory Timeline Diagram

```
TIME ──────────────────────────────────────────────────────────────────────►

C++ SIDE                          │ FFI BOUNDARY │           RUST SIDE
                                  │              │
┌─────────────────┐               │              │
│ std::string key │               │              │
│ = "user:123"    │ ─────────────►│──────────────│──► ptr + len received
│                 │               │              │    (borrowed view)
│ std::string val │               │              │         │
│ = "John Doe"    │ ─────────────►│──────────────│──► ptr + len received
└────────┬────────┘               │              │         │
         │                        │              │         ▼
         │                        │              │  ┌──────────────────┐
         │  Call kv_store_set()   │              │  │ Create owned     │
         │ ───────────────────────│──────────────│─►│ String copies    │
         │                        │              │  │ for cache        │
         │                        │              │  └──────────────────┘
         │                        │              │         │
         │  Function returns      │              │         │
         │ ◄──────────────────────│──────────────│─────────┘
         │                        │              │
         ▼                        │              │
┌─────────────────┐               │              │
│ ~std::string()  │               │              │
│ Destructor runs │               │              │
│ Memory freed    │               │              │
└─────────────────┘               │              │

OWNERSHIP: C++ ████████████████████████████████████░░░░░░░░░░░░░░░░░░░░░░░░
           Rust (borrow) ░░░░░░░░░░░░░░░░░░████████░░░░░░░░░░░░░░░░░░░░░░░░
           Rust (owned copy) ░░░░░░░░░░░░░░░░░░░░░░░████████████████████████
```

### Code Flow

**C++ Side:**
```cpp
void KvStoreClient::Set(std::string_view key, std::string_view value) {
    // key and value point to memory owned by CALLER
    
    KvSetResult result = kv_store_set(
        client_,
        reinterpret_cast<const uint8_t*>(key.data()),   // Pointer to C++ memory
        key.size(),
        reinterpret_cast<const uint8_t*>(value.data()), // Pointer to C++ memory
        value.size()
    );
    
    // FFI call is SYNCHRONOUS - Rust is done with pointers when this returns
    // C++ can now safely free/reuse its memory
}
```

**C# Side:**
```csharp
public unsafe void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) {
    // Spans point to memory on C# stack or heap
    
    fixed (byte* keyPtr = key)      // Pin memory - prevent GC movement
    fixed (byte* valuePtr = value)  // Pin memory - prevent GC movement
    {
        var result = NativeMethods.kv_store_set(
            _handle,
            (IntPtr)keyPtr,         // Pointer to C# memory
            (nuint)key.Length,
            (IntPtr)valuePtr,       // Pointer to C# memory
            (nuint)value.Length
        );
        
        // FFI returns - Rust is done with pointers
    }
    // 'fixed' block ends - memory can be unpinned/moved by GC
}
```

**Rust Side:**
```rust
pub unsafe extern "C" fn kv_store_set(
    client: *mut KvStoreClient,
    key_ptr: *const u8,
    key_len: usize,
    value_ptr: *const u8,
    value_len: usize,
) -> KvSetResult {
    // Create BORROWED views (no allocation)
    let key: &str = std::str::from_utf8(
        std::slice::from_raw_parts(key_ptr, key_len)  // View into C++/C# memory
    ).unwrap();
    
    let value: &str = std::str::from_utf8(
        std::slice::from_raw_parts(value_ptr, value_len)  // View into C++/C# memory
    ).unwrap();
    
    // NOW we create owned copies for storage
    let owned_key: String = key.to_string();      // ALLOCATION happens here
    let owned_value: String = value.to_string();  // ALLOCATION happens here
    
    // Store in cache (Rust now owns this memory)
    client.cache.insert(owned_key, owned_value);
    
    KvSetResult::success()
    // Function returns - C++/C# can free their memory
}
```

### Key Points

| Aspect | Details |
|--------|---------|
| **Who allocates?** | C++ (`std::string`) or C# (managed array/string) |
| **Who owns during call?** | Binding owns, Rust borrows |
| **Who copies?** | Rust - creates owned `String` for cache storage |
| **Who deallocates binding memory?** | Binding (automatic via RAII/GC) |
| **Who deallocates Rust copy?** | Rust (when cache evicts) |
| **Safety requirement** | Call must be synchronous; Rust cannot hold pointer after return |

### C# GC Consideration: Pinning

```csharp
// WRONG - GC can move array DURING FFI call!
byte[] key = Encoding.UTF8.GetBytes("user:123");
NativeMethods.kv_store_set(handle, key, key.Length, ...);  // UNSAFE!

// CORRECT - Pin memory to prevent GC movement
fixed (byte* keyPtr = key) {
    NativeMethods.kv_store_set(handle, (IntPtr)keyPtr, key.Length, ...);
}
```

---

## Direction B: Allocated in Rust, Used in Binding

### Scenario: `Get(key)` Operation with Zero-Copy

Rust returns a pointer to cached data. The binding reads it without copying. Rust must keep data alive until the binding is done.

### Memory Timeline Diagram

```
TIME ──────────────────────────────────────────────────────────────────────►

C++ SIDE                          │ FFI BOUNDARY │           RUST SIDE
                                  │              │
                                  │              │  ┌──────────────────────┐
                                  │              │  │ Cache contains:      │
                                  │              │  │ Arc<String> ─────────┤
                                  │              │  │ ref_count = 1        │
                                  │              │  │ data = "John Doe"    │
                                  │              │  └──────────────────────┘
         │                        │              │            │
         │  Call kv_store_get()   │              │            │
         │ ───────────────────────│──────────────│───────────►│
         │                        │              │            │
         │                        │              │  ┌─────────▼────────────┐
         │                        │              │  │ Arc::into_raw()      │
         │                        │              │  │ ref_count = 1        │
         │                        │              │  │ (Arc "leaked")       │
         │                        │              │  └──────────────────────┘
         │                        │              │            │
         │  KvGetResult returned  │              │            │
         │ ◄──────────────────────│──────────────│────────────┘
         │  { ptr, len, handle }  │              │
         ▼                        │              │
┌─────────────────┐               │              │
│ ValueRef holds  │               │              │
│ KvGetResult     │               │              │
│                 │               │              │
│ string_view ────│───────────────│──────────────│──► Points to "John Doe"
│ reads data      │               │              │    in Rust heap
└────────┬────────┘               │              │
         │                        │              │
         │ (Cache may evict       │              │  ┌──────────────────────┐
         │  original Arc here -   │              │  │ Cache eviction:      │
         │  but data is SAFE      │              │  │ Arc dropped          │
         │  because we hold       │              │  │ BUT ref_count = 1    │
         │  a leaked Arc ref)     │              │  │ Data NOT freed!      │
         │                        │              │  └──────────────────────┘
         │                        │              │
         ▼                        │              │
┌─────────────────┐               │              │
│ ~ValueRef()     │               │              │
│ destructor      │               │              │
│                 │               │              │
│ Calls release() │               │              │
│ ────────────────│──────────────►│              │
└─────────────────┘               │              │
                                  │              │  ┌──────────────────────┐
                                  │              │  │ Arc::from_raw()      │
                                  │              │  │ ref_count = 0        │
                                  │              │  │ Memory FREED         │
                                  │              │  └──────────────────────┘

RUST OWNS DATA: ████████████████████████████████████████████████████████████
BINDING BORROWS: ░░░░░░░░░░░░░░░░░░░████████████████████████████░░░░░░░░░░░░
ARC REF COUNT:   1  1  1  1  1  1  1  1  1  1  1  1  1  1  1  1  0 (freed)
```

### Code Flow

**Rust Side - Allocate and Leak Arc:**
```rust
pub unsafe extern "C" fn kv_store_get(
    client: *mut KvStoreClient,
    key_ptr: *const u8,
    key_len: usize,
) -> KvGetResult {
    let key = std::str::from_utf8(
        std::slice::from_raw_parts(key_ptr, key_len)
    ).unwrap();
    
    // Get Arc<String> from cache
    let cached_value: Arc<String> = client.cache.get(key)?;
    
    // Extract pointer BEFORE leaking (Arc still valid)
    let ptr = cached_value.as_ptr();
    let len = cached_value.len();
    
    // CRITICAL: Leak the Arc to prevent deallocation
    // This increments strong_count conceptually by "forgetting" to drop
    let arc_handle = Arc::into_raw(cached_value);
    
    KvGetResult {
        value: KvStringRef { ptr, len },
        error_code: 0,
        _padding: 0,
        _arc_handle: arc_handle as *const (),  // Store for later release
    }
}
```

**Rust Side - Reclaim and Free:**
```rust
pub unsafe extern "C" fn kv_store_release_get(
    _client: *mut KvStoreClient,
    result: *mut KvGetResult,
) {
    let result = &mut *result;
    
    if !result._arc_handle.is_null() {
        // Reconstruct the Arc - this decrements ref count
        let arc: Arc<String> = Arc::from_raw(
            result._arc_handle as *const String
        );
        
        // Arc is dropped here - if ref_count reaches 0, memory is freed
        drop(arc);
        
        // Clear handle to prevent double-free
        result._arc_handle = std::ptr::null();
    }
}
```

**C++ Side - RAII Wrapper:**
```cpp
class ValueRef {
private:
    KvStoreClient* client_;
    KvGetResult result_;
    
public:
    ValueRef(KvStoreClient* client, KvGetResult result)
        : client_(client), result_(result) {}
    
    // RAII: Destructor automatically releases
    ~ValueRef() {
        if (client_) {
            kv_store_release_get(client_, &result_);  // Tell Rust we're done
        }
    }
    
    // Zero-copy access
    std::string_view view() const {
        return std::string_view(
            reinterpret_cast<const char*>(result_.value.ptr),
            result_.value.len
        );
    }
    
    // Move semantics to transfer ownership
    ValueRef(ValueRef&& other) noexcept
        : client_(other.client_), result_(other.result_) {
        other.client_ = nullptr;  // Prevent double-release
    }
    
    // Prevent copying (would cause double-release)
    ValueRef(const ValueRef&) = delete;
    ValueRef& operator=(const ValueRef&) = delete;
};
```

**C# Side - IDisposable:**
```csharp
public sealed class ValueRef : IDisposable {
    private IntPtr _client;
    private KvGetResult _result;
    private bool _disposed;
    
    internal ValueRef(IntPtr client, KvGetResult result) {
        _client = client;
        _result = result;
        _disposed = false;
    }
    
    // Zero-copy access
    public unsafe ReadOnlySpan<byte> AsSpan() {
        if (_disposed) throw new ObjectDisposedException(nameof(ValueRef));
        return new ReadOnlySpan<byte>(
            (void*)_result.Value.Ptr,
            (int)_result.Value.Len
        );
    }
    
    // IDisposable: Release Rust memory
    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            if (_client != IntPtr.Zero) {
                NativeMethods.kv_store_release_get(_client, ref _result);
            }
        }
    }
    
    // Ensure cleanup even if Dispose not called
    ~ValueRef() {
        Dispose();
    }
}
```

### Key Points

| Aspect | Details |
|--------|---------|
| **Who allocates?** | Rust (String in cache) |
| **Who owns?** | Rust (via Arc reference counting) |
| **Who borrows?** | C++/C# (via raw pointer) |
| **How is data kept alive?** | `Arc::into_raw()` prevents drop |
| **Who deallocates?** | Rust (via `Arc::from_raw()` when release called) |
| **Safety mechanism** | RAII destructor / IDisposable ensures release |

---

## The Arc Reference Counting Dance

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     ARC REFERENCE COUNT LIFECYCLE                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  Step 1: Value in Cache                                                     │
│  ┌─────────────┐     ┌─────────────────────────────┐                       │
│  │   Cache     │────►│  Arc<String>                │                       │
│  │             │     │  strong_count = 1           │                       │
│  └─────────────┘     │  data = "Hello World"       │                       │
│                      └─────────────────────────────┘                       │
│                                                                             │
│  Step 2: kv_store_get() - Clone Arc for return                             │
│  ┌─────────────┐     ┌─────────────────────────────┐                       │
│  │   Cache     │────►│  Arc<String>                │                       │
│  │             │     │  strong_count = 2  ◄────────│───── Arc::clone()     │
│  └─────────────┘     │  data = "Hello World"       │                       │
│                      └──────────────▲──────────────┘                       │
│                                     │                                       │
│                      ┌──────────────┴──────────────┐                       │
│                      │  Cloned Arc (for return)    │                       │
│                      │  (shares same data)         │                       │
│                      └─────────────────────────────┘                       │
│                                                                             │
│  Step 3: Arc::into_raw() - "Leak" the Arc                                  │
│  ┌─────────────┐     ┌─────────────────────────────┐                       │
│  │   Cache     │────►│  Arc<String>                │                       │
│  │             │     │  strong_count = 2           │◄── Count NOT changed  │
│  └─────────────┘     │  data = "Hello World"       │    but Arc "forgotten"│
│                      └──────────────▲──────────────┘                       │
│                                     │                                       │
│                      ┌──────────────┴──────────────┐                       │
│                      │  Raw pointer returned       │──► To C++/C#          │
│                      │  Arc is "leaked" (not drop) │                       │
│                      └─────────────────────────────┘                       │
│                                                                             │
│  Step 4: Cache Eviction (data still safe!)                                 │
│  ┌─────────────┐     ┌─────────────────────────────┐                       │
│  │   Cache     │     │  Arc<String>                │                       │
│  │  (evicted)  │     │  strong_count = 1  ◄────────│───── Cache dropped    │
│  └─────────────┘     │  data = "Hello World"       │      its reference    │
│                      └──────────────▲──────────────┘                       │
│                                     │                                       │
│                      ┌──────────────┴──────────────┐                       │
│                      │  C++/C# still holds pointer │◄── Data STILL VALID   │
│                      │  Leaked Arc keeps data alive│                       │
│                      └─────────────────────────────┘                       │
│                                                                             │
│  Step 5: kv_store_release_get() - Reclaim and drop                         │
│                      ┌─────────────────────────────┐                       │
│                      │  Arc<String>                │                       │
│                      │  strong_count = 0  ◄────────│───── Arc::from_raw()  │
│                      │  data = "Hello World"       │      then drop()      │
│                      └─────────────────────────────┘                       │
│                                     │                                       │
│                                     ▼                                       │
│                      ┌─────────────────────────────┐                       │
│                      │       MEMORY FREED          │                       │
│                      │   (Rust deallocator runs)   │                       │
│                      └─────────────────────────────┘                       │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Common Pitfalls and Solutions

### Pitfall 1: Forgetting to Release (Memory Leak)

```cpp
// WRONG - Memory leak!
void bad_example() {
    KvGetResult result = kv_store_get(client, key, len);
    process(result.value.ptr, result.value.len);
    // Forgot kv_store_release_get() - Arc leaked forever!
}

// CORRECT - Use RAII wrapper
void good_example() {
    ValueRef ref = client.GetRef(key);  // RAII manages lifetime
    process(ref.view());
}  // ~ValueRef() automatically calls release
```

### Pitfall 2: Use-After-Free

```cpp
// WRONG - Dangling pointer!
std::string_view bad_example() {
    ValueRef ref = client.GetRef(key);
    return ref.view();  // ref destroyed, view now dangling!
}

// CORRECT - Copy if needed beyond scope
std::string good_example() {
    ValueRef ref = client.GetRef(key);
    return ref.to_string();  // Copy data, safe to return
}
```

### Pitfall 3: Double Release

```cpp
// WRONG - Double free!
void bad_example() {
    ValueRef ref1 = client.GetRef(key);
    ValueRef ref2 = ref1;  // Copy (if allowed) - both will release!
}

// CORRECT - Move semantics
void good_example() {
    ValueRef ref1 = client.GetRef(key);
    ValueRef ref2 = std::move(ref1);  // Transfer ownership
}  // Only ref2 releases
```

### Pitfall 4: GC Moving Pinned Memory (C#)

```csharp
// WRONG - GC can move array!
void BadExample() {
    byte[] key = GetKey();
    // GC might compact heap here, moving 'key'
    NativeMethods.kv_store_get(handle, key, key.Length);  // Pointer invalid!
}

// CORRECT - Pin memory
void GoodExample() {
    byte[] key = GetKey();
    fixed (byte* keyPtr = key) {  // Pin - GC cannot move
        NativeMethods.kv_store_get(handle, (IntPtr)keyPtr, key.Length);
    }
}
```

---

## Complete Memory Ownership Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    MEMORY OWNERSHIP RULES                                   │
├──────────────────┬──────────────────────────────────────────────────────────┤
│ SCENARIO         │ ALLOCATOR      │ OWNER     │ DEALLOCATOR   │ MECHANISM  │
├──────────────────┼────────────────┼───────────┼───────────────┼────────────┤
│ Key passed to    │ C++/C#         │ C++/C#    │ C++/C#        │ RAII/GC    │
│ Set()            │                │           │               │            │
├──────────────────┼────────────────┼───────────┼───────────────┼────────────┤
│ Value passed to  │ C++/C#         │ C++/C#    │ C++/C#        │ RAII/GC    │
│ Set()            │                │           │               │            │
├──────────────────┼────────────────┼───────────┼───────────────┼────────────┤
│ Value copy in    │ Rust           │ Rust      │ Rust          │ Arc/Cache  │
│ cache            │                │           │               │            │
├──────────────────┼────────────────┼───────────┼───────────────┼────────────┤
│ Value returned   │ Rust           │ Rust      │ Rust          │ Arc leak/  │
│ from Get()       │                │ (Arc ref) │ (on release)  │ release    │
├──────────────────┼────────────────┼───────────┼───────────────┼────────────┤
│ Error messages   │ Rust (static)  │ Rust      │ Never freed   │ 'static    │
│                  │                │           │               │ lifetime   │
└──────────────────┴────────────────┴───────────┴───────────────┴────────────┘
```

---

## Why This Design is Safe

### 1. **Rust Side Safety**
- `Arc::into_raw()` prevents premature deallocation
- `Arc::from_raw()` ensures proper cleanup
- Cache eviction doesn't affect outstanding references

### 2. **C++ Side Safety**
- RAII (`~ValueRef()`) guarantees release is called
- Move semantics prevent double-release
- Deleted copy constructor prevents accidental copies

### 3. **C# Side Safety**
- `IDisposable` pattern ensures cleanup
- `using` statement provides deterministic disposal
- Finalizer as safety net for forgotten `Dispose()`
- `fixed` blocks prevent GC from moving pinned memory

### 4. **Cross-Language Guarantee**
- Synchronous calls mean borrowed memory is valid for call duration
- Returned pointers are protected by Arc reference count
- Explicit release function ensures Rust controls final deallocation

---

## Debug Helpers

### Rust - Track Outstanding References

```rust
#[cfg(debug_assertions)]
use std::sync::atomic::{AtomicUsize, Ordering};

#[cfg(debug_assertions)]
static OUTSTANDING_REFS: AtomicUsize = AtomicUsize::new(0);

pub unsafe extern "C" fn kv_store_get(...) -> KvGetResult {
    #[cfg(debug_assertions)]
    OUTSTANDING_REFS.fetch_add(1, Ordering::SeqCst);
    
    // ... normal code ...
}

pub unsafe extern "C" fn kv_store_release_get(...) {
    #[cfg(debug_assertions)]
    OUTSTANDING_REFS.fetch_sub(1, Ordering::SeqCst);
    
    // ... normal code ...
}

pub unsafe extern "C" fn kv_store_destroy(client: *mut KvStoreClient) {
    #[cfg(debug_assertions)]
    {
        let refs = OUTSTANDING_REFS.load(Ordering::SeqCst);
        if refs > 0 {
            eprintln!("WARNING: Destroying client with {} outstanding refs!", refs);
        }
    }
    
    // ... normal code ...
}
```

### C# - Debug Tracking

```csharp
public sealed class ValueRef : IDisposable {
    #if DEBUG
    private static int _outstandingRefs = 0;
    private readonly string _stackTrace;
    #endif
    
    internal ValueRef(IntPtr client, KvGetResult result) {
        #if DEBUG
        Interlocked.Increment(ref _outstandingRefs);
        _stackTrace = Environment.StackTrace;
        #endif
        // ...
    }
    
    public void Dispose() {
        if (!_disposed) {
            #if DEBUG
            Interlocked.Decrement(ref _outstandingRefs);
            #endif
            // ...
        }
    }
    
    #if DEBUG
    ~ValueRef() {
        if (!_disposed) {
            Console.Error.WriteLine($"ValueRef not disposed! Created at:\n{_stackTrace}");
        }
        Dispose();
    }
    #endif
}
```

---

## Conclusion

| Direction | Allocation | Ownership | Deallocation | Safety Mechanism |
|-----------|------------|-----------|--------------|------------------|
| **Binding → Rust** | Binding | Binding (Rust borrows) | Binding (after FFI returns) | Synchronous call, `fixed` blocks |
| **Rust → Binding** | Rust | Rust (Arc) | Rust (on release call) | Arc leak/reclaim, RAII/IDisposable |

**The Golden Rule:** Memory crosses the FFI boundary as a *loan*, not a *transfer*. The original allocator always remains responsible for deallocation.
