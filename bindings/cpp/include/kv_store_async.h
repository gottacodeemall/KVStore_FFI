// kv_store_async.h - Async C ABI for non-blocking operations
// Callbacks are invoked from Rust's tokio worker threads

#ifndef KV_STORE_ASYNC_H
#define KV_STORE_ASYNC_H

#include <stdint.h>
#include <stddef.h>
#include "kv_store.h"  // For KvGetResult, KvStringRef, etc.

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// ASYNC CLIENT
// ============================================================================

// Opaque handle to the async KV store client
typedef struct KvStoreAsyncClient KvStoreAsyncClient;

// Configuration for async client
typedef struct {
    const char* server_url;     // Null-terminated
    uint64_t cache_ttl_ms;
    uint64_t cache_capacity;
    uint32_t connection_pool_size;
    uint32_t _padding;
} KvStoreAsyncConfig;

// ============================================================================
// CALLBACK TYPES
// ============================================================================

// Callback for async Get completion
// user_data: pointer passed to kv_store_get_async
// result: the get result (caller must release via kv_store_async_release_get)
typedef void (*KvGetCallback)(void* user_data, KvGetResult result);

// Callback for async Set completion
// user_data: pointer passed to kv_store_set_async
// error_code: 0 = success, non-zero = error
typedef void (*KvSetCallback)(void* user_data, int32_t error_code);

// ============================================================================
// LIFECYCLE FUNCTIONS
// ============================================================================

// Initialize async client. Returns NULL on failure.
KvStoreAsyncClient* kv_store_async_init(const KvStoreAsyncConfig* config);

// Destroy async client.
void kv_store_async_destroy(KvStoreAsyncClient* client);

// ============================================================================
// ASYNC OPERATIONS
// ============================================================================

// Async Get - returns immediately, invokes callback when complete.
// - On cache hit: callback may be invoked synchronously before this returns
// - On cache miss: callback is invoked from a tokio worker thread
// THREAD SAFETY: callback must be safe to call from any thread
void kv_store_get_async(
    KvStoreAsyncClient* client,
    const uint8_t* key_ptr,
    size_t key_len,
    KvGetCallback callback,
    void* user_data
);

// Async Set - returns immediately, invokes callback when complete.
// - Callback is invoked from a tokio worker thread
// THREAD SAFETY: callback must be safe to call from any thread
void kv_store_set_async(
    KvStoreAsyncClient* client,
    const uint8_t* key_ptr,
    size_t key_len,
    const uint8_t* value_ptr,
    size_t value_len,
    KvSetCallback callback,
    void* user_data
);

// Release async get result (same as sync version)
void kv_store_async_release_get(KvStoreAsyncClient* client, KvGetResult* result);

// ============================================================================
// CACHE MANAGEMENT
// ============================================================================

void kv_store_async_cache_clear(KvStoreAsyncClient* client);
KvCacheStats kv_store_async_cache_stats(const KvStoreAsyncClient* client);

#ifdef __cplusplus
}
#endif

#endif // KV_STORE_ASYNC_H
