//! High-performance concurrent cache using moka
//!
//! Key design: moka provides lock-free reads and the cached Arc<String>
//! allows us to return pointers that remain valid as long as the entry exists.

use moka::sync::Cache;
use portable_atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

/// Cached value wrapper - Arc ensures the string stays alive
/// even if cache evicts while we hold a reference
pub type CachedValue = Arc<String>;

/// Cache statistics
pub struct CacheStatsInternal {
    pub hits: AtomicU64,
    pub misses: AtomicU64,
}

impl CacheStatsInternal {
    pub fn new() -> Self {
        Self {
            hits: AtomicU64::new(0),
            misses: AtomicU64::new(0),
        }
    }
}

/// High-performance concurrent cache
#[derive(Clone)]
pub struct KvCache {
    cache: Cache<String, CachedValue>,
    stats: Arc<CacheStatsInternal>,
}

impl KvCache {
    /// Create a new cache with given capacity and TTL
    pub fn new(capacity: u64, ttl_ms: u64) -> Self {
        let cache = Cache::builder()
            .max_capacity(capacity)
            .time_to_live(Duration::from_millis(ttl_ms))
            .build();

        Self {
            cache,
            stats: Arc::new(CacheStatsInternal::new()),
        }
    }

    /// Get from cache - returns Arc to ensure value stays alive
    /// This is a lock-free operation in moka
    #[inline]
    pub fn get(&self, key: &str) -> Option<CachedValue> {
        match self.cache.get(key) {
            Some(value) => {
                self.stats.hits.fetch_add(1, Ordering::Relaxed);
                Some(value)
            }
            None => {
                self.stats.misses.fetch_add(1, Ordering::Relaxed);
                None
            }
        }
    }

    /// Insert into cache
    #[inline]
    pub fn insert(&self, key: String, value: String) -> CachedValue {
        let arc_value = Arc::new(value);
        self.cache.insert(key, arc_value.clone());
        arc_value
    }

    /// Invalidate a key
    #[inline]
    pub fn invalidate(&self, key: &str) {
        self.cache.invalidate(key);
    }

    /// Clear entire cache
    pub fn clear(&self) {
        self.cache.invalidate_all();
    }

    /// Get current cache size
    pub fn len(&self) -> u64 {
        self.cache.entry_count()
    }

    /// Get stats
    pub fn hits(&self) -> u64 {
        self.stats.hits.load(Ordering::Relaxed)
    }

    pub fn misses(&self) -> u64 {
        self.stats.misses.load(Ordering::Relaxed)
    }
}
