// kv_store_async.hpp - Modern C++ async wrapper using std::future
//
// Design goals:
// - Non-blocking operations that integrate with std::future/std::promise
// - Thread-safe callbacks bridged to C++ futures
// - RAII for automatic resource management
// - Zero-copy where possible with proper lifetime management

#pragma once

#include <string>
#include <string_view>
#include <optional>
#include <stdexcept>
#include <memory>
#include <future>
#include <functional>
#include <chrono>
#include <atomic>
#include <mutex>

#include "kv_store_async.h"
#include "kv_store.hpp"  // Reuse ErrorCode, Config, etc.

namespace kvstore {

// ============================================================================
// ASYNC VALUE REFERENCE
// ============================================================================

/// RAII wrapper for async KvGetResult
class AsyncValueRef {
public:
    AsyncValueRef(KvStoreAsyncClient* client, KvGetResult result) noexcept
        : client_(client), result_(result) {}
    
    ~AsyncValueRef() {
        if (client_) {
            kv_store_async_release_get(client_, &result_);
        }
    }
    
    // Non-copyable
    AsyncValueRef(const AsyncValueRef&) = delete;
    AsyncValueRef& operator=(const AsyncValueRef&) = delete;
    
    // Movable
    AsyncValueRef(AsyncValueRef&& other) noexcept
        : client_(other.client_), result_(other.result_) {
        other.client_ = nullptr;
    }
    
    AsyncValueRef& operator=(AsyncValueRef&& other) noexcept {
        if (this != &other) {
            if (client_) {
                kv_store_async_release_get(client_, &result_);
            }
            client_ = other.client_;
            result_ = other.result_;
            other.client_ = nullptr;
        }
        return *this;
    }
    
    /// Get a string_view of the value - ZERO COPY
    std::string_view view() const noexcept {
        return std::string_view(
            reinterpret_cast<const char*>(result_.value.ptr),
            result_.value.len
        );
    }
    
    /// Convert to string (copies data)
    std::string to_string() const {
        return std::string(view());
    }
    
    /// Check if valid
    bool valid() const noexcept {
        return result_.error_code == 0;
    }
    
    ErrorCode error() const noexcept {
        return static_cast<ErrorCode>(result_.error_code);
    }

private:
    KvStoreAsyncClient* client_;
    KvGetResult result_;
};

// ============================================================================
// INTERNAL: CALLBACK CONTEXT
// ============================================================================

namespace detail {

// Context for async Get operations
struct GetContext {
    KvStoreAsyncClient* client;
    std::promise<AsyncValueRef> promise;
    
    GetContext(KvStoreAsyncClient* c) : client(c) {}
};

// Context for async Set operations
struct SetContext {
    std::promise<void> promise;
};

// C callback that bridges to C++ promise
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

// C callback that bridges to C++ promise
inline void set_callback(void* user_data, int32_t error_code) {
    auto* ctx = static_cast<SetContext*>(user_data);
    try {
        if (error_code == 0) {
            ctx->promise.set_value();
        } else {
            ctx->promise.set_exception(std::make_exception_ptr(
                KvStoreException("Set failed", static_cast<ErrorCode>(error_code))));
        }
    } catch (...) {
        ctx->promise.set_exception(std::current_exception());
    }
    delete ctx;
}

} // namespace detail

// ============================================================================
// ASYNC CLIENT
// ============================================================================

/// High-performance async KV Store client.
/// All operations are non-blocking - work is offloaded to Rust's tokio runtime.
/// Thread-safe for concurrent use.
class KvStoreAsyncClient {
public:
    explicit KvStoreAsyncClient(const Config& config = Config{}) {
        KvStoreAsyncConfig c_config{};
        c_config.server_url = config.server_url.c_str();
        c_config.cache_ttl_ms = static_cast<uint64_t>(config.cache_ttl.count());
        c_config.cache_capacity = static_cast<uint64_t>(config.cache_capacity);
        c_config.connection_pool_size = config.connection_pool_size;
        c_config._padding = 0;
        
        client_ = kv_store_async_init(&c_config);
        if (!client_) {
            throw KvStoreException("Failed to initialize async KV store client", ErrorCode::InternalError);
        }
    }
    
    ~KvStoreAsyncClient() {
        if (client_) {
            kv_store_async_destroy(client_);
        }
    }
    
    // Non-copyable
    KvStoreAsyncClient(const KvStoreAsyncClient&) = delete;
    KvStoreAsyncClient& operator=(const KvStoreAsyncClient&) = delete;
    
    // Movable
    KvStoreAsyncClient(KvStoreAsyncClient&& other) noexcept : client_(other.client_) {
        other.client_ = nullptr;
    }
    
    KvStoreAsyncClient& operator=(KvStoreAsyncClient&& other) noexcept {
        if (this != &other) {
            if (client_) {
                kv_store_async_destroy(client_);
            }
            client_ = other.client_;
            other.client_ = nullptr;
        }
        return *this;
    }
    
    /// Get value asynchronously - returns RAII wrapper for zero-copy access
    /// Does NOT block the calling thread.
    /// On cache hit: future may be immediately ready
    /// On cache miss: HTTP fetch happens on Rust's tokio runtime
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
    
    /// Get value as string asynchronously (copies data when future is resolved)
    /// @throws KeyNotFoundException if key doesn't exist (in the future)
    [[nodiscard]] std::future<std::string> GetAsync(std::string_view key) {
        // We need to return a future<string>, so we wrap the future<AsyncValueRef>
        auto ref_future = GetRefAsync(key);
        
        return std::async(std::launch::deferred, [f = std::move(ref_future)]() mutable {
            auto ref = f.get();
            if (!ref.valid()) {
                if (ref.error() == ErrorCode::KeyNotFound) {
                    throw KeyNotFoundException("Key not found");
                }
                throw KvStoreException("Get failed", ref.error());
            }
            return ref.to_string();
        });
    }
    
    /// Try to get value asynchronously (no exception on missing key)
    [[nodiscard]] std::future<std::optional<std::string>> TryGetAsync(std::string_view key) {
        auto ref_future = GetRefAsync(key);
        
        return std::async(std::launch::deferred, [f = std::move(ref_future)]() mutable {
            auto ref = f.get();
            if (ref.valid()) {
                return std::optional<std::string>(ref.to_string());
            }
            return std::optional<std::string>(std::nullopt);
        });
    }
    
    /// Set key-value pair asynchronously
    /// Does NOT block the calling thread.
    /// HTTP write happens on Rust's tokio runtime
    [[nodiscard]] std::future<void> SetAsync(std::string_view key, std::string_view value) {
        auto* ctx = new detail::SetContext();
        auto future = ctx->promise.get_future();
        
        kv_store_set_async(
            client_,
            reinterpret_cast<const uint8_t*>(key.data()),
            key.size(),
            reinterpret_cast<const uint8_t*>(value.data()),
            value.size(),
            detail::set_callback,
            ctx
        );
        
        return future;
    }
    
    /// Clear cache (synchronous, fast operation)
    void ClearCache() {
        kv_store_async_cache_clear(client_);
    }
    
    /// Get cache statistics
    CacheStats GetCacheStats() const {
        KvCacheStats stats = kv_store_async_cache_stats(client_);
        return CacheStats{stats.hits, stats.misses, stats.current_size};
    }
    
    /// Get raw handle (for advanced use)
    ::KvStoreAsyncClient* handle() const noexcept { return client_; }

private:
    ::KvStoreAsyncClient* client_ = nullptr;
};

// ============================================================================
// CONVENIENCE: FIRE-AND-FORGET OPERATIONS
// ============================================================================

/// Helper for fire-and-forget Set operations
/// Use when you don't need to wait for the result
class FireAndForget {
public:
    explicit FireAndForget(KvStoreAsyncClient& client) : client_(client) {}
    
    /// Set without waiting for completion
    /// Warning: Errors are silently discarded
    void Set(std::string_view key, std::string_view value) {
        // We must still call get() eventually to avoid broken promise,
        // but we can do it in a detached thread
        auto future = client_.SetAsync(key, value);
        
        // Detach the future - it will complete when Rust is done
        std::thread([f = std::move(future)]() mutable {
            try { f.get(); } catch (...) {}
        }).detach();
    }
    
private:
    KvStoreAsyncClient& client_;
};

} // namespace kvstore
