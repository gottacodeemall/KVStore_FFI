//! KV Store Core SDK - High-performance caching layer with stable C ABI
//!
//! Design principles:
//! - Zero-copy reads from cache (pointer directly into moka's storage)
//! - Stable ABI using #[repr(C)] everywhere
//! - Thread-safe, lock-free cache reads via moka
//! - Connection pooling via reqwest
//! - Async support via callbacks for non-blocking operations

mod ffi;
mod ffi_async;
mod cache;
mod client;
mod async_client;
mod error;

pub use ffi::*;
pub use ffi_async::*;
pub use cache::*;
pub use client::*;
pub use async_client::*;
pub use error::*;
