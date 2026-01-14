// kv_store.h - Manually written stable C ABI header
// This matches the Rust FFI exactly

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
// - Strings are always pointer + length (never null-terminated in data)
// - Opaque handles are always pointers (stable size)
// ============================================================================

// Opaque handle to the KV store client
typedef struct KvStoreClient KvStoreClient;

// String slice - ZERO COPY reference to Rust-owned memory
// SAFETY: Valid only until kv_store_release_get() is called
typedef struct {
    const uint8_t* ptr;    // Pointer to UTF-8 data (NOT null-terminated)
    size_t len;            // Length in bytes
} KvStringRef;

// Result for Get operations - ZERO COPY
typedef struct {
    KvStringRef value;      // Valid if error_code == 0
    int32_t error_code;     // 0 = success
    int32_t _padding;       // Explicit padding for ABI stability
    const void* _arc_handle; // Internal - do not touch
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
    uint64_t cache_capacity;    // Max entries
    uint32_t connection_pool_size;
    uint32_t _padding;          // Explicit padding
} KvStoreConfig;

// Cache statistics
typedef struct {
    uint64_t hits;
    uint64_t misses;
    uint64_t current_size;
} KvCacheStats;

// ============================================================================
// LIFECYCLE FUNCTIONS
// ============================================================================

// Initialize client. Returns NULL on failure.
KvStoreClient* kv_store_init(const KvStoreConfig* config);

// Destroy client. All outstanding KvGetResult become INVALID.
void kv_store_destroy(KvStoreClient* client);

// ============================================================================
// CORE OPERATIONS - ZERO COPY
// ============================================================================

// Get value by key.
// ZERO-COPY: Result.value.ptr points directly into cache memory.
// LIFETIME: Result is valid until kv_store_release_get() is called.
KvGetResult kv_store_get(
    KvStoreClient* client,
    const uint8_t* key_ptr,
    size_t key_len
);

// Release the get result, allowing cache to potentially evict.
// MUST be called after each kv_store_get().
void kv_store_release_get(KvStoreClient* client, KvGetResult* result);

// Set key-value pair.
KvSetResult kv_store_set(
    KvStoreClient* client,
    const uint8_t* key_ptr,
    size_t key_len,
    const uint8_t* value_ptr,
    size_t value_len
);

// ============================================================================
// CACHE MANAGEMENT
// ============================================================================

void kv_store_cache_clear(KvStoreClient* client);
KvCacheStats kv_store_cache_stats(const KvStoreClient* client);

#ifdef __cplusplus
}
#endif

#endif // KV_STORE_H
