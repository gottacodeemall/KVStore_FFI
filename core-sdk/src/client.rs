//! HTTP client for communicating with the KV server

use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use std::time::Duration;

use crate::error::ErrorCode;

#[derive(Serialize)]
struct SetRequest<'a> {
    value: &'a str,
}

#[derive(Deserialize)]
struct GetResponse {
    value: String,
}

/// HTTP client with connection pooling
pub struct KvHttpClient {
    client: Client,
    base_url: String,
}

impl KvHttpClient {
    pub fn new(server_url: &str, pool_size: u32) -> Result<Self, ErrorCode> {
        let client = Client::builder()
            .pool_max_idle_per_host(pool_size as usize)
            .pool_idle_timeout(Duration::from_secs(30))
            .timeout(Duration::from_secs(10))
            .tcp_nodelay(true)
            .build()
            .map_err(|_| ErrorCode::ConnectionError)?;

        Ok(Self {
            client,
            base_url: server_url.trim_end_matches('/').to_string(),
        })
    }

    /// Fetch value from server
    pub fn get(&self, key: &str) -> Result<String, ErrorCode> {
        let url = format!("{}/kv/{}", self.base_url, key);
        
        let response = self.client
            .get(&url)
            .send()
            .map_err(|_| ErrorCode::ConnectionError)?;

        if response.status().is_success() {
            let body: GetResponse = response.json().map_err(|_| ErrorCode::InternalError)?;
            Ok(body.value)
        } else if response.status().as_u16() == 404 {
            Err(ErrorCode::KeyNotFound)
        } else {
            Err(ErrorCode::InternalError)
        }
    }

    /// Set value on server
    pub fn set(&self, key: &str, value: &str) -> Result<(), ErrorCode> {
        let url = format!("{}/kv/{}", self.base_url, key);
        
        let response = self.client
            .put(&url)
            .json(&SetRequest { value })
            .send()
            .map_err(|_| ErrorCode::ConnectionError)?;

        if response.status().is_success() {
            Ok(())
        } else {
            Err(ErrorCode::InternalError)
        }
    }
}
