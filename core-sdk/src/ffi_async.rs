//! Async FFI interface for non-blocking operations
//!
//! This module provides callback-based async APIs that integrate with:
//! - C# async/await via TaskCompletionSource
//! - C++ std::future via std::promise
//!
//! Pattern: The binding calls an async function, passing a callback.
//! Rust spawns the work on a tokio runtime and invokes the callback when done.

use std::ffi::CStr;
use std::ptr;
use std::sync::Arc;

use once_cell::sync::Lazy;
use tokio::runtime::Runtime;

use crate::async_client::KvAsyncHttpClient;
use crate::cache::KvCache;
use crate::error::ErrorCode;
use crate::ffi::KvGetResult;

// ============================================================================
// GLOBAL ASYNC RUNTIME
// ============================================================================

/// Global tokio runtime for async operations
/// This is lazily initialized on first async call
static ASYNC_RUNTIME: Lazy<Runtime> = Lazy::new(|| {
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(4)
        .enable_all()
        .build()
        .expect("Failed to create tokio runtime")
});

// ============================================================================
// ASYNC CLIENT
// ============================================================================

/// Async-capable client struct
pub struct KvStoreAsyncClient {
    pub(crate) cache: KvCache,
    pub(crate) async_http_client: KvAsyncHttpClient,
}

// ============================================================================
// CALLBACK TYPES
// ============================================================================

/// Callback invoked when async Get completes
/// Parameters: user_data, result
pub type GetCallback = extern "C" fn(user_data: *mut (), result: KvGetResult);

/// Callback invoked when async Set completes  
/// Parameters: user_data, error_code (0 = success)
pub type SetCallback = extern "C" fn(user_data: *mut (), error_code: i32);

// ============================================================================
// ASYNC FFI CONFIG
// ============================================================================

/// Configuration for async client
#[repr(C)]
pub struct KvStoreAsyncConfig {
    pub server_url: *const i8,
    pub cache_ttl_ms: u64,
    pub cache_capacity: u64,
    pub connection_pool_size: u32,
    pub _padding: u32,
}

// ============================================================================
// ASYNC FFI FUNCTIONS
// ============================================================================

/// Initialize a new async-capable KV store client
#[no_mangle]
pub unsafe extern "C" fn kv_store_async_init(config: *const KvStoreAsyncConfig) -> *mut KvStoreAsyncClient {
    if config.is_null() {
        return ptr::null_mut();
    }

    let config = &*config;
    
    // Parse server URL
    let server_url = match CStr::from_ptr(config.server_url).to_str() {
        Ok(s) => s,
        Err(_) => return ptr::null_mut(),
    };

    // Create async HTTP client
    let async_http_client = match KvAsyncHttpClient::new(server_url, config.connection_pool_size) {
        Ok(c) => c,
        Err(_) => return ptr::null_mut(),
    };

    // Create cache
    let cache = KvCache::new(config.cache_capacity, config.cache_ttl_ms);

    let client = Box::new(KvStoreAsyncClient { 
        cache, 
        async_http_client,
    });
    Box::into_raw(client)
}

/// Destroy the async client
#[no_mangle]
pub unsafe extern "C" fn kv_store_async_destroy(client: *mut KvStoreAsyncClient) {
    if !client.is_null() {
        drop(Box::from_raw(client));
    }
}

/// Async Get operation - returns immediately, invokes callback when complete
/// 
/// # Safety
/// - `client` must be a valid pointer from `kv_store_async_init`
/// - `callback` must be a valid function pointer
/// - `user_data` is passed to the callback (can be null)
/// - The callback will be invoked from a different thread
#[no_mangle]
pub unsafe extern "C" fn kv_store_get_async(
    client: *mut KvStoreAsyncClient,
    key_ptr: *const u8,
    key_len: usize,
    callback: GetCallback,
    user_data: *mut (),
) {
    if client.is_null() || key_ptr.is_null() {
        callback(user_data, KvGetResult::error(ErrorCode::InvalidInput));
        return;
    }

    let client = &*client;
    
    // Convert key to owned String (must own it since we're going async)
    let key = match std::str::from_utf8(std::slice::from_raw_parts(key_ptr, key_len)) {
        Ok(k) => k.to_string(),
        Err(_) => {
            callback(user_data, KvGetResult::error(ErrorCode::InvalidInput));
            return;
        }
    };

    // Check cache first (synchronous, fast path)
    if let Some(cached_value) = client.cache.get(&key) {
        callback(user_data, KvGetResult::success(cached_value));
        return;
    }

    // Cache miss - spawn async task
    let cache = client.cache.clone();
    let http_client = client.async_http_client.clone();
    
    // Wrap raw pointers in a Send-able struct
    let user_data_ptr = user_data as usize;  // Convert to usize for Send
    
    ASYNC_RUNTIME.spawn(async move {
        let result = match http_client.get(&key).await {
            Ok(value) => {
                // Insert into cache
                let cached_value = cache.insert(key, value);
                KvGetResult::success(cached_value)
            }
            Err(code) => KvGetResult::error(code),
        };
        
        // Invoke callback (on tokio worker thread)
        callback(user_data_ptr as *mut (), result);
    });
}

/// Async Set operation - returns immediately, invokes callback when complete
#[no_mangle]
pub unsafe extern "C" fn kv_store_set_async(
    client: *mut KvStoreAsyncClient,
    key_ptr: *const u8,
    key_len: usize,
    value_ptr: *const u8,
    value_len: usize,
    callback: SetCallback,
    user_data: *mut (),
) {
    if client.is_null() || key_ptr.is_null() || value_ptr.is_null() {
        callback(user_data, ErrorCode::InvalidInput as i32);
        return;
    }

    let client = &*client;

    // Convert key and value to owned Strings
    let key = match std::str::from_utf8(std::slice::from_raw_parts(key_ptr, key_len)) {
        Ok(k) => k.to_string(),
        Err(_) => {
            callback(user_data, ErrorCode::InvalidInput as i32);
            return;
        }
    };

    let value = match std::str::from_utf8(std::slice::from_raw_parts(value_ptr, value_len)) {
        Ok(v) => v.to_string(),
        Err(_) => {
            callback(user_data, ErrorCode::InvalidInput as i32);
            return;
        }
    };

    let cache = client.cache.clone();
    let http_client = client.async_http_client.clone();
    let user_data_ptr = user_data as usize;

    ASYNC_RUNTIME.spawn(async move {
        let error_code = match http_client.set(&key, &value).await {
            Ok(()) => {
                // Update cache (write-through)
                cache.insert(key, value);
                ErrorCode::Success as i32
            }
            Err(code) => {
                // Invalidate cache on failure
                cache.invalidate(&key);
                code as i32
            }
        };
        
        callback(user_data_ptr as *mut (), error_code);
    });
}

/// Release a get result from async operation (same as sync version)
#[no_mangle]
pub unsafe extern "C" fn kv_store_async_release_get(
    _client: *mut KvStoreAsyncClient,
    result: *mut KvGetResult,
) {
    if result.is_null() {
        return;
    }

    let result = &mut *result;
    if !result._arc_handle.is_null() {
        let _ = Arc::from_raw(result._arc_handle as *const String);
        result._arc_handle = ptr::null();
    }
}

/// Get cache stats for async client
#[no_mangle]
pub unsafe extern "C" fn kv_store_async_cache_stats(client: *const KvStoreAsyncClient) -> crate::ffi::KvCacheStats {
    if client.is_null() {
        return crate::ffi::KvCacheStats {
            hits: 0,
            misses: 0,
            current_size: 0,
        };
    }

    let client = &*client;
    crate::ffi::KvCacheStats {
        hits: client.cache.hits(),
        misses: client.cache.misses(),
        current_size: client.cache.len(),
    }
}

/// Clear cache for async client
#[no_mangle]
pub unsafe extern "C" fn kv_store_async_cache_clear(client: *mut KvStoreAsyncClient) {
    if !client.is_null() {
        (*client).cache.clear();
    }
}
