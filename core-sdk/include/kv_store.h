#ifndef KV_STORE_H
#define KV_STORE_H

#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

/**
 * Async-capable client struct
 */
typedef struct KvStoreAsyncClient KvStoreAsyncClient;

/**
 * The main client struct - opaque to C code
 */
typedef struct KvStoreClient KvStoreClient;

/**
 * Configuration for the client
 */
typedef struct KvStoreConfig {
  const int8_t *server_url;
  uint64_t cache_ttl_ms;
  uint64_t cache_capacity;
  uint32_t connection_pool_size;
  uint32_t padding;
} KvStoreConfig;

/**
 * String reference - zero-copy view into Rust memory
 */
typedef struct KvStringRef {
  const uint8_t *ptr;
  uintptr_t len;
} KvStringRef;

/**
 * Get operation result - zero-copy
 */
typedef struct KvGetResult {
  struct KvStringRef value;
  int32_t error_code;
  int32_t padding;
  const void *arc_handle;
} KvGetResult;

/**
 * Set operation result
 */
typedef struct KvSetResult {
  int32_t error_code;
  int32_t padding;
} KvSetResult;

/**
 * Cache statistics
 */
typedef struct KvCacheStats {
  uint64_t hits;
  uint64_t misses;
  uint64_t current_size;
} KvCacheStats;

/**
 * Configuration for async client
 */
typedef struct KvStoreAsyncConfig {
  const int8_t *server_url;
  uint64_t cache_ttl_ms;
  uint64_t cache_capacity;
  uint32_t connection_pool_size;
  uint32_t padding;
} KvStoreAsyncConfig;

/**
 * Callback invoked when async Get completes
 * Parameters: user_data, result
 */
typedef void (*GetCallback)(void *user_data, struct KvGetResult result);

/**
 * Callback invoked when async Set completes
 * Parameters: user_data, error_code (0 = success)
 */
typedef void (*SetCallback)(void *user_data, int32_t error_code);

#ifdef __cplusplus
extern "C" {
#endif // __cplusplus

/**
 * Initialize a new KV store client
 */
kv_store_ struct KvStoreClient *kv_store_init(const struct KvStoreConfig *config);

/**
 * Destroy the client
 */
kv_store_ void kv_store_destroy(struct KvStoreClient *client);

/**
 * Get a value - ZERO COPY
 *
 * The returned KvStringRef points directly into the cache's memory.
 * Caller MUST call kv_store_release_get() when done.
 */
kv_store_
struct KvGetResult kv_store_get(struct KvStoreClient *client,
                                const uint8_t *key_ptr,
                                uintptr_t key_len);

/**
 * Release a get result - reclaims the Arc reference
 */
kv_store_ void kv_store_release_get(struct KvStoreClient *_client, struct KvGetResult *result);

/**
 * Set a key-value pair
 */
kv_store_
struct KvSetResult kv_store_set(struct KvStoreClient *client,
                                const uint8_t *key_ptr,
                                uintptr_t key_len,
                                const uint8_t *value_ptr,
                                uintptr_t value_len);

/**
 * Clear the cache
 */
kv_store_ void kv_store_cache_clear(struct KvStoreClient *client);

/**
 * Get cache statistics
 */
kv_store_ struct KvCacheStats kv_store_cache_stats(const struct KvStoreClient *client);

/**
 * Initialize a new async-capable KV store client
 */
kv_store_ struct KvStoreAsyncClient *kv_store_async_init(const struct KvStoreAsyncConfig *config);

/**
 * Destroy the async client
 */
kv_store_ void kv_store_async_destroy(struct KvStoreAsyncClient *client);

/**
 * Async Get operation - returns immediately, invokes callback when complete
 *
 * # Safety
 * - `client` must be a valid pointer from `kv_store_async_init`
 * - `callback` must be a valid function pointer
 * - `user_data` is passed to the callback (can be null)
 * - The callback will be invoked from a different thread
 */
kv_store_
void kv_store_get_async(struct KvStoreAsyncClient *client,
                        const uint8_t *key_ptr,
                        uintptr_t key_len,
                        GetCallback callback,
                        void *user_data);

/**
 * Async Set operation - returns immediately, invokes callback when complete
 */
kv_store_
void kv_store_set_async(struct KvStoreAsyncClient *client,
                        const uint8_t *key_ptr,
                        uintptr_t key_len,
                        const uint8_t *value_ptr,
                        uintptr_t value_len,
                        SetCallback callback,
                        void *user_data);

/**
 * Release a get result from async operation (same as sync version)
 */
kv_store_
void kv_store_async_release_get(struct KvStoreAsyncClient *_client,
                                struct KvGetResult *result);

/**
 * Get cache stats for async client
 */
kv_store_ struct KvCacheStats kv_store_async_cache_stats(const struct KvStoreAsyncClient *client);

/**
 * Clear cache for async client
 */
kv_store_ void kv_store_async_cache_clear(struct KvStoreAsyncClient *client);

#ifdef __cplusplus
} // extern "C"
#endif // __cplusplus

#endif /* KV_STORE_H */
