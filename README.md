# KV Store - High-Performance Cached Key-Value Store

A high-performance, zero-copy cached key-value store with Rust backend and C++ bindings.

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

| Criteria | Target |
|----------|--------|
| Cache Hit Latency | < 500 ns |
| FFI Overhead | < 50 ns |
| Overhead vs Direct HTTP | < 1% |
| Thread Safety | Lock-free reads |

## Project Structure

```
ipc/
├── server/                 # Rust HTTP server (source of truth)
│   ├── Cargo.toml
│   └── src/main.rs
│
├── core-sdk/               # Rust Core SDK with FFI
│   ├── Cargo.toml
│   ├── cbindgen.toml
│   └── src/
│       ├── lib.rs
│       ├── ffi.rs          # C ABI interface
│       ├── cache.rs        # moka-based caching
│       ├── client.rs       # HTTP client
│       └── error.rs
│
├── bindings/cpp/           # C++ Binding
│   ├── CMakeLists.txt
│   ├── include/
│   │   ├── kv_store.h      # C header
│   │   └── kv_store.hpp    # C++ wrapper
│   └── benchmarks/
│       └── benchmark_main.cpp
│
├── build.ps1               # Windows build script
├── build.sh                # Unix build script
├── SPEC.md                 # Technical specification
└── README.md               # This file
```

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
