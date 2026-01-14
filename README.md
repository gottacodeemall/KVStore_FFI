# KV Store FFI - High-Performance Cross-Language SDK Research

A research project exploring **zero-copy FFI** for building high-performance SDKs with a shared Rust core and bindings for C++, C#, and other languages.

## 🎯 Research Goals

Evaluate whether FFI is a viable approach for SDK teams to:
- Share a single high-performance core across multiple languages
- Achieve sub-microsecond overhead for cross-language calls
- Maintain zero-copy semantics for large data transfers

## 📊 Key Findings

| Metric | Target | Achieved | Status |
|--------|--------|----------|--------|
| Sync FFI Overhead (C++) | < 1 μs | ~200-640 ns | ✅ PASS |
| Sync FFI Overhead (C#) | < 1 μs | ~220-580 ns | ✅ PASS |
| Async FFI Overhead (C++) | < 5 μs | ~400-1000 ns | ✅ PASS |
| Async FFI Overhead (C#) | < 5 μs | ~800-4000 ns | ✅ PASS |
| Zero-Copy Verification | Value-size independent | Confirmed | ✅ PASS |

### ⚠️ Critical Limitations Discovered

| Challenge | Impact | Severity |
|-----------|--------|----------|
| **UDT/Complex Type FFI** | Cannot pass dictionaries, objects, nested types without serialization (100-200μs overhead) | 🔴 Blocker |
| **OpenSSL Compatibility** | Native library may link against different OpenSSL version than host app | 🔴 Blocker |
| **Memory Safety** | Manual lifetime management, use-after-free risks | 🟠 High |
| **ABI Stability** | Struct layout changes break compatibility silently | 🟠 High |

### 💡 Bottom Line

**FFI works well for simple, primitive-heavy APIs** with clear ownership semantics. For complex domain objects with dictionaries, nested types, or user-defined classes, the engineering investment may not justify the performance gains. See [findings/ffi_complex_types.md](findings/ffi_complex_types.md) for detailed analysis.

---

## Quick Start

### Prerequisites

- **Rust**: 1.70+ (`rustup` recommended)
- **CMake**: 3.15+
- **C++ Compiler**: MSVC 2019+, GCC 9+, or Clang 10+
- **Git**: For fetching dependencies

### Build

**Windows (PowerShell):**
```powershell
.\build.ps1
```

**Linux/macOS:**
```bash
chmod +x build.sh
./build.sh
```

### Run Benchmarks

1. **Start the KV Server** (Terminal 1):
   ```bash
   cd server
   cargo run --release
   ```

2. **Run Benchmarks** (Terminal 2):
   
   Windows:
   ```powershell
   .\bindings\cpp\build\Release\kv_benchmark.exe
   ```
   
   Linux/macOS:
   ```bash
   ./bindings/cpp/build/kv_benchmark
   ```

## Architecture

```
┌─────────────────┐     FFI (C ABI)      ┌─────────────────┐      HTTP       ┌─────────────────┐
│   C++ Binding   │ ──────────────────── │  Rust Core SDK  │ ─────────────── │ Rust Web Server │
│                 │   ZERO-COPY          │                 │                 │  (Source of     │
│  Get(key)       │   Pointer+Length     │  - Lock-free    │                 │   Truth)        │
│  Set(key, val)  │   No allocations     │    cache reads  │                 │                 │
└─────────────────┘                      └─────────────────┘                 └─────────────────┘
```

## Performance Goals

| Criteria | Target | Result |
|----------|--------|--------|
| Sync Cache Hit Latency | < 1 μs | ~200-640 ns ✅ |
| Async Cache Hit Latency | < 5 μs | ~400-4000 ns ✅ |
| Zero-Copy | Value-size independent | Confirmed ✅ |
| Thread Safety | Lock-free reads | Confirmed ✅ |

## 📁 Project Structure

```
ipc/
├── server/                 # Rust HTTP server (source of truth)
│   └── src/main.rs
│
├── core-sdk/               # Rust Core SDK with FFI
│   └── src/
│       ├── lib.rs
│       ├── ffi.rs          # Sync C ABI interface
│       ├── ffi_async.rs    # Async C ABI interface (callbacks)
│       ├── async_client.rs # Async HTTP client
│       ├── cache.rs        # moka-based caching
│       └── client.rs       # Sync HTTP client
│
├── bindings/
│   ├── cpp/                # C++ Binding
│   │   ├── include/
│   │   │   ├── kv_store.hpp       # Sync C++ wrapper
│   │   │   └── kv_store_async.hpp # Async C++ wrapper (std::future)
│   │   └── benchmarks/
│   │       ├── benchmark_main.cpp
│   │       └── async_ffi_overhead_benchmark.cpp
│   │
│   └── csharp/             # C# Binding
│       ├── KvStore/
│       │   ├── KvStoreClient.cs      # Sync client
│       │   └── KvStoreAsyncClient.cs # Async client (Task-based)
│       ├── KvStore.Benchmark/
│       └── KvStore.AsyncBenchmark/
│
├── findings/               # Research findings & analysis
│   ├── ffi_overhead_analysis.md      # C++ sync/async benchmarks
│   ├── ffi_overhead_analysis_csharp.md
│   ├── ffi_memory_management.md      # Memory ownership patterns
│   ├── ffi_complex_types.md          # UDT/collection analysis
│   └── async_ffi_pattern.md
│
└── README.md
```

## 📚 Research Findings

| Document | Description |
|----------|-------------|
| [ffi_overhead_analysis.md](findings/ffi_overhead_analysis.md) | C++ sync & async benchmark results |
| [ffi_overhead_analysis_csharp.md](findings/ffi_overhead_analysis_csharp.md) | C# P/Invoke benchmark results |
| [ffi_memory_management.md](findings/ffi_memory_management.md) | Memory ownership patterns across FFI |
| [ffi_complex_types.md](findings/ffi_complex_types.md) | **Why FFI fails for UDTs** - critical read |
| [async_ffi_pattern.md](findings/async_ffi_pattern.md) | Callback-based async FFI design |

---

## C++ Usage

```cpp
#include "kv_store.hpp"

int main() {
    // Configure
    kvstore::Config config;
    config.server_url = "http://127.0.0.1:8080";
    config.cache_ttl = std::chrono::milliseconds{5000};
    config.cache_capacity = 10000;
    
    // Create client
    kvstore::KvStoreClient client(config);
    
    // Set a value
    client.Set("user:123", "John Doe");
    
    // Get a value (copies string)
    std::string value = client.Get("user:123");
    
    // Zero-copy access (advanced)
    auto ref = client.GetRef("user:123");
    std::string_view view = ref.view();  // No allocation!
    
    // Check cache stats
    auto stats = client.GetCacheStats();
    std::cout << "Hit rate: " << stats.hit_rate() * 100 << "%\n";
    
    return 0;
}
```

## Zero-Copy Design

The key innovation is the `ValueRef` class which provides zero-copy access to cached values:

1. **Cache Storage**: Values are stored as `Arc<String>` in moka's lock-free cache
2. **FFI Return**: The FFI returns a pointer directly into the Arc's memory
3. **Lifetime Management**: `ValueRef` holds an Arc reference count, ensuring the value stays alive
4. **Release**: When `ValueRef` destructs, it decrements the Arc count

This means cache hits involve:
- ✅ No memory allocation
- ✅ No string copying
- ✅ Lock-free cache lookup (moka)
- ✅ Just a pointer + length returned across FFI

## Dependencies

### Rust (Battle-tested, production-ready)
- `axum` - High-performance async HTTP server
- `tokio` - Async runtime
- `moka` - Lock-free concurrent cache
- `reqwest` - HTTP client with connection pooling
- `dashmap` - Lock-free concurrent HashMap (server)

### C++ (Industry standard)
- `cpp-httplib` - Header-only HTTP client (benchmark baseline)
- `Google Benchmark` - Microbenchmarking framework
- `nlohmann/json` - JSON parsing

## License

MIT

---

## Conclusion

This research demonstrates that **zero-copy FFI is achievable for primitive types and simple structs** with sub-microsecond overhead. However, **FFI is not a silver bullet** for SDK teams:

- ✅ **Works well for:** Simple KV operations, primitive-heavy APIs, performance-critical hot paths
- ❌ **Does not work for:** Complex objects, dictionaries, nested types, user-defined classes

For complex domain models, consider:
1. **Native implementations** per language (more effort, better DX)
2. **gRPC/Protocol Buffers** (schema evolution, cross-process)
3. **Opaque handles + accessors** (keeps data in Rust, exposes field-by-field access)
