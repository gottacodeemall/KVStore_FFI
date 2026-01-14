# Cached KV Store - Technical Specification

## Phase 1: C++ Binding, Rust Core SDK, and Rust Web Server

---

## 1. Overview

This specification defines a high-performance cached key-value store system consisting of:
- **C++ Bindings**: Native C++ interface for client applications
- **Rust Core SDK**: High-performance core logic with caching
- **Rust Web Server**: Source of truth for key-value storage

### Critical Success Criteria

| Criteria | Requirement | Failure Condition |
|----------|-------------|-------------------|
| **ABI Stability** | Stable C ABI, forward/backward compatible | Any ABI breakage = FAIL |
| **Zero-Copy FFI** | No allocations at interop boundary for reads | Any allocation at FFI = FAIL |
| **Performance Overhead** | < 1% vs direct HTTP under 16x vCPU concurrency | > 1% overhead = FAIL |
| **Thread Safety** | Lock-free reads from cache | Contention bottlenecks = FAIL |

### Architecture Diagram

```
┌─────────────────┐     FFI (C ABI)      ┌─────────────────┐      HTTP       ┌─────────────────┐
│   C++ Binding   │ ──────────────────── │  Rust Core SDK  │ ─────────────── │ Rust Web Server │
│                 │   ZERO-COPY          │                 │                 │  (Source of     │
│  Get(key)       │   Pointer+Length     │  - Lock-free    │                 │   Truth)        │
│  Set(key, val)  │   No allocations     │    cache reads  │                 │                 │
│                 │                      │  - Connection   │                 │                 │
└─────────────────┘                      │    pooling      │                 └─────────────────┘
                                         └─────────────────┘
```

### Battle-Tested Dependencies (No Custom Core Components)

| Component | Library | Rationale |
|-----------|---------|-----------|
| HTTP Server | `axum` + `tokio` | Production-proven, async, high throughput |
| Cache | `moka` | Lock-free concurrent cache, used in production |
| HTTP Client | `reqwest` | Mature, connection pooling built-in |
| FFI Safety | `abi_stable` | Guarantees stable ABI across Rust versions |
| C++ HTTP (benchmark) | `cpp-httplib` | Header-only, minimal overhead baseline |
| C++ Benchmarking | `Google Benchmark` | Industry standard microbenchmarking |

---

## 2. Component Specifications

### 2.1 Rust Web Server

#### 2.1.1 Purpose
Acts as the source of truth for all key-value data. Provides a simple HTTP REST API.

#### 2.1.2 API Endpoints

| Method | Endpoint | Request Body | Response | Description |
|--------|----------|--------------|----------|-------------|
| GET | `/kv/{key}` | - | `{ "value": "..." }` | Retrieve value for key |
| PUT | `/kv/{key}` | `{ "value": "..." }` | `{ "success": true }` | Set value for key |
| DELETE | `/kv/{key}` | - | `{ "success": true }` | Delete key |
| GET | `/health` | - | `{ "status": "ok" }` | Health check |

#### 2.1.3 Response Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 404 | Key not found |
| 400 | Bad request |
| 500 | Internal server error |

#### 2.1.4 Technology Stack
- **Framework**: `axum` (high-performance async web framework)
- **Runtime**: `tokio` (async runtime)
- **Storage**: In-memory `HashMap` with `RwLock` (can be extended to persistent storage)

#### 2.1.5 Configuration

```toml
[server]
host = "127.0.0.1"
port = 8080
max_connections = 1000
```

---

### 2.2 Rust Core SDK

#### 2.2.1 Purpose
Provides the core business logic, caching layer, and exposes a C-compatible FFI interface for language bindings.

#### 2.2.2 Core Features

1. **Caching Layer**
   - LRU cache with configurable capacity
   - TTL (Time-To-Live) based expiration
   - Thread-safe access

2. **Connection Pooling**
   - Reusable HTTP connections
   - Configurable pool size

3. **FFI Interface**
   - C-compatible ABI for cross-language interop
   - Zero-copy string passing where possible
   - Explicit memory management

#### 2.2.3 FFI Interface Definition (Stable ABI)

**Design Principles**:
1. **Pointer + Length**: Never use null-terminated strings across FFI
2. **Pinned Memory**: Cache entries are pinned, pointers remain valid
3. **No Hidden Allocations**: All allocations are explicit and documented
4. **Stable ABI**: Uses `#[repr(C)]` and fixed-size types only

```c
// C Header (kv_store.h) - STABLE ABI CONTRACT

#ifndef KV_STORE_H
#define KV_STORE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// ABI STABILITY GUARANTEES:
// - All structs are #[repr(C)] with explicit padding
// - All integers are fixed-width (int32_t, uint64_t, etc.)
// - Strings are always pointer + length (never null-terminated)
// - Opaque handles are always pointers (stable size)
// ============================================================================

// Opaque handle to the KV store client
typedef struct KvStoreClient KvStoreClient;

// String slice - ZERO COPY reference to Rust-owned memory
// SAFETY: Valid only until the next FFI call or explicit free
typedef struct {
    const char* ptr;    // Pointer to UTF-8 data (NOT null-terminated)
    size_t len;         // Length in bytes
} KvStringRef;

// Result for Get operations - ZERO COPY
typedef struct {
    KvStringRef value;      // Valid if error_code == 0
    int32_t error_code;     // 0 = success
    int32_t _padding;       // Explicit padding for ABI stability
} KvGetResult;

// Result for Set operations
typedef struct {
    int32_t error_code;     // 0 = success
    int32_t _padding;       // Explicit padding
} KvSetResult;

// Configuration - all fixed-size types
typedef struct {
    const char* server_url;     // Null-terminated (init only, copied)
    uint64_t cache_ttl_ms;      // Cache TTL in milliseconds
    uint64_t cache_capacity;    // Max entries (use uint64_t for alignment)
    uint32_t connection_pool_size;
    uint32_t _padding;          // Explicit padding
} KvStoreConfig;

// ============================================================================
// LIFECYCLE FUNCTIONS
// ============================================================================

// Initialize client. Returns NULL on failure.
// The config strings are COPIED - caller can free after this returns.
KvStoreClient* kv_store_init(const KvStoreConfig* config);

// Destroy client. All outstanding KvGetResult become INVALID.
void kv_store_destroy(KvStoreClient* client);

// ============================================================================
// CORE OPERATIONS - ZERO COPY
// ============================================================================

// Get value by key.
// ZERO-COPY: Result.value.ptr points directly into cache memory.
// LIFETIME: Result is valid until kv_store_release_get() is called.
// THREAD-SAFETY: Each thread must call release before next get.
KvGetResult kv_store_get(
    KvStoreClient* client,
    const char* key_ptr,
    size_t key_len
);

// Release the get result, allowing cache to potentially evict.
// MUST be called after each kv_store_get().
void kv_store_release_get(KvStoreClient* client, KvGetResult* result);

// Set key-value pair.
// This WILL allocate to send data to server.
KvSetResult kv_store_set(
    KvStoreClient* client,
    const char* key_ptr,
    size_t key_len,
    const char* value_ptr,
    size_t value_len
);

// ============================================================================
// CACHE MANAGEMENT
// ============================================================================

void kv_store_cache_clear(KvStoreClient* client);

typedef struct {
    uint64_t hits;
    uint64_t misses;
    uint64_t current_size;
} KvCacheStats;

KvCacheStats kv_store_cache_stats(const KvStoreClient* client);

#ifdef __cplusplus
}
#endif

#endif // KV_STORE_H
```

#### 2.2.4 Error Codes

| Code | Name | Description |
|------|------|-------------|
| 0 | SUCCESS | Operation completed successfully |
| 1 | KEY_NOT_FOUND | Key does not exist |
| 2 | CONNECTION_ERROR | Failed to connect to server |
| 3 | TIMEOUT | Operation timed out |
| 4 | INVALID_INPUT | Invalid key or value |
| 5 | INTERNAL_ERROR | Internal SDK error |

#### 2.2.5 Caching Strategy

```
┌─────────────────────────────────────────────────────────────┐
│                        GET Operation                         │
├─────────────────────────────────────────────────────────────┤
│  1. Check cache for key                                      │
│     ├── Cache HIT + Not Expired → Return cached value       │
│     └── Cache MISS or Expired → Continue to step 2          │
│  2. Fetch from server                                        │
│  3. Store in cache with TTL                                  │
│  4. Return value                                             │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                        SET Operation                         │
├─────────────────────────────────────────────────────────────┤
│  1. Send to server (write-through)                          │
│  2. On success: Update cache with new value                 │
│  3. On failure: Invalidate cache entry if exists            │
└─────────────────────────────────────────────────────────────┘
```

#### 2.2.6 Technology Stack
- **HTTP Client**: `reqwest` with connection pooling
- **Cache**: `moka` (high-performance concurrent cache)
- **Serialization**: `serde` + `serde_json`
- **FFI**: `cbindgen` for header generation

---

### 2.3 C++ Binding

#### 2.3.1 Purpose
Provides an idiomatic C++ interface wrapping the Rust Core SDK FFI.

#### 2.3.2 C++ Interface

```cpp
// kv_store.hpp

#pragma once

#include <string>
#include <string_view>
#include <optional>
#include <stdexcept>
#include <memory>
#include <chrono>

namespace kvstore {

// Exception types
class KvStoreException : public std::runtime_error {
public:
    explicit KvStoreException(const std::string& msg, int error_code)
        : std::runtime_error(msg), error_code_(error_code) {}
    
    int error_code() const noexcept { return error_code_; }
    
private:
    int error_code_;
};

class KeyNotFoundException : public KvStoreException {
public:
    explicit KeyNotFoundException(const std::string& key)
        : KvStoreException("Key not found: " + key, 1) {}
};

class ConnectionException : public KvStoreException {
public:
    explicit ConnectionException(const std::string& msg)
        : KvStoreException(msg, 2) {}
};

// Configuration
struct Config {
    std::string server_url = "http://127.0.0.1:8080";
    std::chrono::milliseconds cache_ttl{5000};
    size_t cache_capacity = 10000;
    uint32_t connection_pool_size = 10;
};

// Cache statistics
struct CacheStats {
    uint64_t hits;
    uint64_t misses;
    size_t current_size;
    
    double hit_rate() const {
        auto total = hits + misses;
        return total > 0 ? static_cast<double>(hits) / total : 0.0;
    }
};

// Main client class
class KvStoreClient {
public:
    explicit KvStoreClient(const Config& config = Config{});
    ~KvStoreClient();
    
    // Non-copyable, movable
    KvStoreClient(const KvStoreClient&) = delete;
    KvStoreClient& operator=(const KvStoreClient&) = delete;
    KvStoreClient(KvStoreClient&&) noexcept;
    KvStoreClient& operator=(KvStoreClient&&) noexcept;
    
    // Core operations
    
    /// Get value for key
    /// @throws KeyNotFoundException if key doesn't exist
    /// @throws ConnectionException on network errors
    /// @throws KvStoreException on other errors
    std::string Get(std::string_view key);
    
    /// Get value for key, returns nullopt if not found
    std::optional<std::string> TryGet(std::string_view key);
    
    /// Set key-value pair
    /// @throws ConnectionException on network errors
    /// @throws KvStoreException on other errors
    void Set(std::string_view key, std::string_view value);
    
    // Cache management
    void ClearCache();
    CacheStats GetCacheStats() const;
    
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace kvstore
```

#### 2.3.3 Usage Example

```cpp
#include "kv_store.hpp"
#include <iostream>

int main() {
    kvstore::Config config;
    config.server_url = "http://127.0.0.1:8080";
    config.cache_ttl = std::chrono::milliseconds{5000};
    config.cache_capacity = 10000;
    
    kvstore::KvStoreClient client(config);
    
    // Set a value
    client.Set("user:123", "John Doe");
    
    // Get a value
    std::string value = client.Get("user:123");
    std::cout << "Value: " << value << std::endl;
    
    // Try get (no exception on missing key)
    auto maybe_value = client.TryGet("user:456");
    if (maybe_value) {
        std::cout << "Found: " << *maybe_value << std::endl;
    }
    
    // Cache stats
    auto stats = client.GetCacheStats();
    std::cout << "Cache hit rate: " << stats.hit_rate() * 100 << "%" << std::endl;
    
    return 0;
}
```

#### 2.3.4 Build Requirements
- C++17 or later
- CMake 3.15+
- Linked against Rust Core SDK shared library

---

## 3. Performance Requirements

### 3.1 Concurrency Target

**Test Condition**: 16x the number of vCPUs concurrent threads

```
Example: 8-core machine → 128 concurrent threads
```

### 3.2 Zero-Copy FFI Contract

**GET Operation (Cache Hit Path)**:
```
C++ calls FFI → Rust returns pointer to cached data → C++ reads directly
                 ↑
                 NO ALLOCATION - pointer into moka cache's internal storage
```

**Memory Ownership Rules**:
| Operation | Allocation | Owner | Lifetime |
|-----------|------------|-------|----------|
| Get (cache hit) | NONE | Rust cache | Until cache eviction |
| Get (cache miss) | 1x (cache insert) | Rust cache | Until cache eviction |
| Set | 1x (value copy to server) | Rust | Request duration |
| Result strings | NONE | Points to cache | Pinned during call |

### 3.3 Latency Targets

| Operation | Target (p99) | Notes |
|-----------|--------------|-------|
| Cache Hit | < 500 ns | Lock-free read |
| Cache Miss (Network) | < 5 ms | Network bound |
| FFI Overhead | < 50 ns | Zero-copy, no alloc |

### 3.4 Benchmark Validation

```
┌────────────────────────────────────────────────────────────────┐
│  BENCHMARK: Direct HTTP vs C++ Binding                         │
│                                                                 │
│  Threads: 16 × NUM_CPUS                                        │
│  Operations: 100,000 per thread                                │
│  Mix: 90% reads (cache hit), 10% writes                        │
│                                                                 │
│  PASS: binding_throughput >= 0.99 × direct_http_throughput     │
│  FAIL: binding_throughput <  0.99 × direct_http_throughput     │
└────────────────────────────────────────────────────────────────┘
```

---

## 4. Project Structure

```
ipc/
├── SPEC.md                          # This specification
├── goal.txt                         # Requirements
│
├── server/                          # Rust Web Server
│   ├── Cargo.toml
│   └── src/
│       ├── main.rs
│       ├── routes.rs
│       └── store.rs
│
├── core-sdk/                        # Rust Core SDK
│   ├── Cargo.toml
│   ├── cbindgen.toml               # FFI header generation config
│   ├── src/
│   │   ├── lib.rs
│   │   ├── ffi.rs                  # FFI interface
│   │   ├── cache.rs                # Caching layer
│   │   ├── client.rs               # HTTP client
│   │   └── error.rs                # Error types
│   └── include/
│       └── kv_store.h              # Generated C header
│
├── bindings/
│   └── cpp/                        # C++ Binding
│       ├── CMakeLists.txt
│       ├── include/
│       │   └── kv_store.hpp
│       ├── src/
│       │   └── kv_store.cpp
│       └── tests/
│           └── test_kv_store.cpp
│
└── benchmarks/
    └── cpp/                        # C++ Benchmarks
        ├── CMakeLists.txt
        ├── direct_http_bench.cpp   # Direct HTTP calls
        └── binding_bench.cpp       # Through C++ binding
```

---

## 5. Build & Test Plan

### 5.1 Build Order

1. **Rust Web Server** → Standalone executable
2. **Rust Core SDK** → Shared library (.dll/.so/.dylib) + C header
3. **C++ Binding** → Links against Core SDK

### 5.2 Build Commands

```bash
# 1. Build Server
cd server
cargo build --release

# 2. Build Core SDK
cd core-sdk
cargo build --release
# Generates: target/release/kv_core.dll (Windows)
#            target/release/libkv_core.so (Linux)

# 3. Build C++ Binding
cd bindings/cpp
mkdir build && cd build
cmake -DCMAKE_BUILD_TYPE=Release ..
cmake --build .
```

### 5.3 Testing Strategy

| Test Type | Description |
|-----------|-------------|
| Unit Tests | Rust: `cargo test`, C++: Google Test |
| Integration Tests | Full stack with server running |
| Benchmark Tests | Performance validation |

---

## 6. Security Considerations

- **Input Validation**: Key and value length limits
- **No SQL/Injection**: Simple key-value only
- **Memory Safety**: Rust guarantees + careful FFI boundary handling

---

## 7. Future Phases

| Phase | Scope |
|-------|-------|
| Phase 2 | C# Binding implementation |
| Phase 3 | Rust native binding |
| Phase 4 | Persistent storage backend |
| Phase 5 | Distributed caching / clustering |

---

## 8. Acceptance Criteria (Phase 1)

- [ ] Rust Web Server responds to GET/SET operations
- [ ] Rust Core SDK caches responses with configurable TTL
- [ ] C++ Binding provides `Get()` and `Set()` methods
- [ ] FFI layer has zero unnecessary allocations
- [ ] Benchmark shows < 1% overhead vs direct HTTP calls
- [ ] All components build on Windows, Linux, and macOS
- [ ] Unit and integration tests pass
