//! FFI interface for the KV Store SDK
//!
//! CRITICAL: This module defines the stable C ABI interface.
//! - All structs are #[repr(C)] with explicit padding
//! - All functions use extern "C"
//! - Strings are pointer + length pairs (zero-copy)
//! - Arc<String> ensures cached values stay alive while referenced

use std::ffi::CStr;
use std::ptr;
use std::sync::Arc;

use crate::cache::{CachedValue, KvCache};
use crate::client::KvHttpClient;
use crate::error::ErrorCode;

// ============================================================================
// STABLE ABI TYPES
// ============================================================================

/// String reference - zero-copy view into Rust memory
#[repr(C)]
pub struct KvStringRef {
    pub ptr: *const u8,
    pub len: usize,
}

impl KvStringRef {
    pub fn null() -> Self {
        Self {
            ptr: ptr::null(),
            len: 0,
        }
    }

    pub fn from_str(s: &str) -> Self {
        Self {
            ptr: s.as_ptr(),
            len: s.len(),
        }
    }
}

/// Get operation result - zero-copy
#[repr(C)]
pub struct KvGetResult {
    pub value: KvStringRef,
    pub error_code: i32,
    pub _padding: i32,
    // Internal: holds Arc to keep value alive (not exposed to C)
    // This is a raw pointer to avoid exposing Rust types
    pub(crate) _arc_handle: *const (),
}

impl KvGetResult {
    pub(crate) fn success(value: CachedValue) -> Self {
        let string_ref = KvStringRef {
            ptr: value.as_ptr(),
            len: value.len(),
        };
        
        // Leak the Arc to extend lifetime - will be reclaimed in release_get
        let arc_ptr = Arc::into_raw(value) as *const ();
        
        Self {
            value: string_ref,
            error_code: ErrorCode::Success as i32,
            _padding: 0,
            _arc_handle: arc_ptr,
        }
    }

    pub(crate) fn error(code: ErrorCode) -> Self {
        Self {
            value: KvStringRef::null(),
            error_code: code as i32,
            _padding: 0,
            _arc_handle: ptr::null(),
        }
    }
}

/// Set operation result
#[repr(C)]
pub struct KvSetResult {
    pub error_code: i32,
    pub _padding: i32,
}

impl KvSetResult {
    fn success() -> Self {
        Self {
            error_code: ErrorCode::Success as i32,
            _padding: 0,
        }
    }

    fn error(code: ErrorCode) -> Self {
        Self {
            error_code: code as i32,
            _padding: 0,
        }
    }
}

/// Configuration for the client
#[repr(C)]
pub struct KvStoreConfig {
    pub server_url: *const i8,
    pub cache_ttl_ms: u64,
    pub cache_capacity: u64,
    pub connection_pool_size: u32,
    pub _padding: u32,
}

/// Cache statistics
#[repr(C)]
pub struct KvCacheStats {
    pub hits: u64,
    pub misses: u64,
    pub current_size: u64,
}

// ============================================================================
// CLIENT STRUCT
// ============================================================================

/// The main client struct - opaque to C code
pub struct KvStoreClient {
    cache: KvCache,
    http_client: KvHttpClient,
}

// ============================================================================
// FFI FUNCTIONS
// ============================================================================

/// Initialize a new KV store client
#[no_mangle]
pub unsafe extern "C" fn kv_store_init(config: *const KvStoreConfig) -> *mut KvStoreClient {
    if config.is_null() {
        return ptr::null_mut();
    }

    let config = &*config;
    
    // Parse server URL
    let server_url = match CStr::from_ptr(config.server_url).to_str() {
        Ok(s) => s,
        Err(_) => return ptr::null_mut(),
    };

    // Create HTTP client
    let http_client = match KvHttpClient::new(server_url, config.connection_pool_size) {
        Ok(c) => c,
        Err(_) => return ptr::null_mut(),
    };

    // Create cache
    let cache = KvCache::new(config.cache_capacity, config.cache_ttl_ms);

    let client = Box::new(KvStoreClient { cache, http_client });
    Box::into_raw(client)
}

/// Destroy the client
#[no_mangle]
pub unsafe extern "C" fn kv_store_destroy(client: *mut KvStoreClient) {
    if !client.is_null() {
        drop(Box::from_raw(client));
    }
}

/// Get a value - ZERO COPY
/// 
/// The returned KvStringRef points directly into the cache's memory.
/// Caller MUST call kv_store_release_get() when done.
#[no_mangle]
pub unsafe extern "C" fn kv_store_get(
    client: *mut KvStoreClient,
    key_ptr: *const u8,
    key_len: usize,
) -> KvGetResult {
    if client.is_null() || key_ptr.is_null() {
        return KvGetResult::error(ErrorCode::InvalidInput);
    }

    let client = &mut *client;
    
    // Convert key to str (no allocation - just a view)
    let key = match std::str::from_utf8(std::slice::from_raw_parts(key_ptr, key_len)) {
        Ok(k) => k,
        Err(_) => return KvGetResult::error(ErrorCode::InvalidInput),
    };

    // Try cache first (lock-free read)
    if let Some(cached_value) = client.cache.get(key) {
        return KvGetResult::success(cached_value);
    }

    // Cache miss - fetch from server
    match client.http_client.get(key) {
        Ok(value) => {
            // Insert into cache and return
            let cached_value = client.cache.insert(key.to_string(), value);
            KvGetResult::success(cached_value)
        }
        Err(code) => KvGetResult::error(code),
    }
}

/// Release a get result - reclaims the Arc reference
#[no_mangle]
pub unsafe extern "C" fn kv_store_release_get(_client: *mut KvStoreClient, result: *mut KvGetResult) {
    if result.is_null() {
        return;
    }

    let result = &mut *result;
    if !result._arc_handle.is_null() {
        // Reclaim the Arc - this decrements the reference count
        let _ = Arc::from_raw(result._arc_handle as *const String);
        result._arc_handle = ptr::null();
    }
}

/// Set a key-value pair
#[no_mangle]
pub unsafe extern "C" fn kv_store_set(
    client: *mut KvStoreClient,
    key_ptr: *const u8,
    key_len: usize,
    value_ptr: *const u8,
    value_len: usize,
) -> KvSetResult {
    if client.is_null() || key_ptr.is_null() || value_ptr.is_null() {
        return KvSetResult::error(ErrorCode::InvalidInput);
    }

    let client = &mut *client;

    // Convert key and value to str (no allocation - just views)
    let key = match std::str::from_utf8(std::slice::from_raw_parts(key_ptr, key_len)) {
        Ok(k) => k,
        Err(_) => return KvSetResult::error(ErrorCode::InvalidInput),
    };

    let value = match std::str::from_utf8(std::slice::from_raw_parts(value_ptr, value_len)) {
        Ok(v) => v,
        Err(_) => return KvSetResult::error(ErrorCode::InvalidInput),
    };

    // Write to server
    match client.http_client.set(key, value) {
        Ok(()) => {
            // Update cache (write-through)
            client.cache.insert(key.to_string(), value.to_string());
            KvSetResult::success()
        }
        Err(code) => {
            // Invalidate cache on failure
            client.cache.invalidate(key);
            KvSetResult::error(code)
        }
    }
}

/// Clear the cache
#[no_mangle]
pub unsafe extern "C" fn kv_store_cache_clear(client: *mut KvStoreClient) {
    if !client.is_null() {
        (*client).cache.clear();
    }
}

/// Get cache statistics
#[no_mangle]
pub unsafe extern "C" fn kv_store_cache_stats(client: *const KvStoreClient) -> KvCacheStats {
    if client.is_null() {
        return KvCacheStats {
            hits: 0,
            misses: 0,
            current_size: 0,
        };
    }

    let client = &*client;
    KvCacheStats {
        hits: client.cache.hits(),
        misses: client.cache.misses(),
        current_size: client.cache.len(),
    }
}
