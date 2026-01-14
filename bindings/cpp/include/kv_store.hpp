// kv_store.hpp - Modern C++ wrapper for the KV Store SDK
// 
// Design goals:
// - Zero overhead over raw FFI calls
// - RAII for automatic resource management
// - std::string_view for zero-copy where possible

#pragma once

#include <string>
#include <string_view>
#include <optional>
#include <stdexcept>
#include <memory>
#include <chrono>

#include "kv_store.h"

namespace kvstore {

// ============================================================================
// ERROR CODES
// ============================================================================

enum class ErrorCode : int32_t {
    Success = 0,
    KeyNotFound = 1,
    ConnectionError = 2,
    Timeout = 3,
    InvalidInput = 4,
    InternalError = 5
};

// ============================================================================
// EXCEPTIONS
// ============================================================================

class KvStoreException : public std::runtime_error {
public:
    explicit KvStoreException(const std::string& msg, ErrorCode code)
        : std::runtime_error(msg), code_(code) {}
    
    ErrorCode code() const noexcept { return code_; }
    
private:
    ErrorCode code_;
};

class KeyNotFoundException : public KvStoreException {
public:
    explicit KeyNotFoundException(const std::string& key)
        : KvStoreException("Key not found: " + key, ErrorCode::KeyNotFound) {}
};

class ConnectionException : public KvStoreException {
public:
    explicit ConnectionException(const std::string& msg)
        : KvStoreException(msg, ErrorCode::ConnectionError) {}
};

// ============================================================================
// CONFIGURATION
// ============================================================================

struct Config {
    std::string server_url = "http://127.0.0.1:8080";
    std::chrono::milliseconds cache_ttl{5000};
    size_t cache_capacity = 10000;
    uint32_t connection_pool_size = 10;
};

// ============================================================================
// CACHE STATISTICS
// ============================================================================

struct CacheStats {
    uint64_t hits;
    uint64_t misses;
    uint64_t current_size;
    
    double hit_rate() const noexcept {
        auto total = hits + misses;
        return total > 0 ? static_cast<double>(hits) / total : 0.0;
    }
};

// ============================================================================
// VALUE REFERENCE - ZERO COPY READ
// ============================================================================

/// RAII wrapper for KvGetResult - ensures release is called
/// This allows zero-copy access to cached values
class ValueRef {
public:
    ValueRef(KvStoreClient* client, KvGetResult result) noexcept
        : client_(client), result_(result) {}
    
    ~ValueRef() {
        if (client_) {
            kv_store_release_get(client_, &result_);
        }
    }
    
    // Non-copyable
    ValueRef(const ValueRef&) = delete;
    ValueRef& operator=(const ValueRef&) = delete;
    
    // Movable
    ValueRef(ValueRef&& other) noexcept
        : client_(other.client_), result_(other.result_) {
        other.client_ = nullptr;
    }
    
    ValueRef& operator=(ValueRef&& other) noexcept {
        if (this != &other) {
            if (client_) {
                kv_store_release_get(client_, &result_);
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
    KvStoreClient* client_;
    KvGetResult result_;
};

// ============================================================================
// CLIENT
// ============================================================================

class KvStoreClient {
public:
    explicit KvStoreClient(const Config& config = Config{}) {
        KvStoreConfig c_config{};
        c_config.server_url = config.server_url.c_str();
        c_config.cache_ttl_ms = static_cast<uint64_t>(config.cache_ttl.count());
        c_config.cache_capacity = static_cast<uint64_t>(config.cache_capacity);
        c_config.connection_pool_size = config.connection_pool_size;
        c_config._padding = 0;
        
        client_ = kv_store_init(&c_config);
        if (!client_) {
            throw KvStoreException("Failed to initialize KV store client", ErrorCode::InternalError);
        }
    }
    
    ~KvStoreClient() {
        if (client_) {
            kv_store_destroy(client_);
        }
    }
    
    // Non-copyable
    KvStoreClient(const KvStoreClient&) = delete;
    KvStoreClient& operator=(const KvStoreClient&) = delete;
    
    // Movable
    KvStoreClient(KvStoreClient&& other) noexcept : client_(other.client_) {
        other.client_ = nullptr;
    }
    
    KvStoreClient& operator=(KvStoreClient&& other) noexcept {
        if (this != &other) {
            if (client_) {
                kv_store_destroy(client_);
            }
            client_ = other.client_;
            other.client_ = nullptr;
        }
        return *this;
    }
    
    /// Get value - returns RAII wrapper for zero-copy access
    /// The returned ValueRef MUST outlive any string_view obtained from it
    [[nodiscard]] ValueRef GetRef(std::string_view key) {
        KvGetResult result = kv_store_get(
            client_,
            reinterpret_cast<const uint8_t*>(key.data()),
            key.size()
        );
        
        return ValueRef(client_, result);
    }
    
    /// Get value as string (copies data)
    /// @throws KeyNotFoundException if key doesn't exist
    std::string Get(std::string_view key) {
        auto ref = GetRef(key);
        if (!ref.valid()) {
            if (ref.error() == ErrorCode::KeyNotFound) {
                throw KeyNotFoundException(std::string(key));
            }
            throw KvStoreException("Get failed", ref.error());
        }
        return ref.to_string();
    }
    
    /// Try to get value (no exception on missing key)
    std::optional<std::string> TryGet(std::string_view key) {
        auto ref = GetRef(key);
        if (ref.valid()) {
            return ref.to_string();
        }
        return std::nullopt;
    }
    
    /// Set key-value pair
    void Set(std::string_view key, std::string_view value) {
        KvSetResult result = kv_store_set(
            client_,
            reinterpret_cast<const uint8_t*>(key.data()),
            key.size(),
            reinterpret_cast<const uint8_t*>(value.data()),
            value.size()
        );
        
        if (result.error_code != 0) {
            throw KvStoreException("Set failed", static_cast<ErrorCode>(result.error_code));
        }
    }
    
    /// Clear cache
    void ClearCache() {
        kv_store_cache_clear(client_);
    }
    
    /// Get cache statistics
    CacheStats GetCacheStats() const {
        KvCacheStats stats = kv_store_cache_stats(client_);
        return CacheStats{stats.hits, stats.misses, stats.current_size};
    }
    
    /// Get raw handle (for advanced use)
    ::KvStoreClient* handle() const noexcept { return client_; }

private:
    ::KvStoreClient* client_ = nullptr;
};

} // namespace kvstore
