use axum::{
    extract::{Path, State},
    http::StatusCode,
    response::IntoResponse,
    routing::{delete, get, put},
    Json, Router,
};
use dashmap::DashMap;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tracing::info;

/// Shared application state - uses DashMap for lock-free concurrent access
type AppState = Arc<DashMap<String, String>>;

#[derive(Serialize)]
struct GetResponse {
    value: String,
}

#[derive(Deserialize)]
struct SetRequest {
    value: String,
}

#[derive(Serialize)]
struct SetResponse {
    success: bool,
}

#[derive(Serialize)]
struct HealthResponse {
    status: &'static str,
}

#[derive(Serialize)]
struct ErrorResponse {
    error: String,
}

/// GET /kv/{key} - Retrieve value
async fn get_value(
    Path(key): Path<String>,
    State(store): State<AppState>,
) -> impl IntoResponse {
    match store.get(&key) {
        Some(value) => (StatusCode::OK, Json(GetResponse { value: value.clone() })).into_response(),
        None => (
            StatusCode::NOT_FOUND,
            Json(ErrorResponse {
                error: format!("Key '{}' not found", key),
            }),
        )
            .into_response(),
    }
}

/// PUT /kv/{key} - Set value
async fn set_value(
    Path(key): Path<String>,
    State(store): State<AppState>,
    Json(payload): Json<SetRequest>,
) -> impl IntoResponse {
    store.insert(key, payload.value);
    (StatusCode::OK, Json(SetResponse { success: true }))
}

/// DELETE /kv/{key} - Delete value
async fn delete_value(
    Path(key): Path<String>,
    State(store): State<AppState>,
) -> impl IntoResponse {
    match store.remove(&key) {
        Some(_) => (StatusCode::OK, Json(SetResponse { success: true })).into_response(),
        None => (
            StatusCode::NOT_FOUND,
            Json(ErrorResponse {
                error: format!("Key '{}' not found", key),
            }),
        )
            .into_response(),
    }
}

/// GET /health - Health check
async fn health_check() -> impl IntoResponse {
    Json(HealthResponse { status: "ok" })
}

#[tokio::main]
async fn main() {
    // Initialize tracing
    tracing_subscriber::fmt::init();

    // Create shared state
    let store: AppState = Arc::new(DashMap::new());

    // Pre-populate with some test data
    for i in 0..1000 {
        store.insert(format!("key{}", i), format!("value{}", i));
    }
    info!("Pre-populated {} keys", store.len());

    // Build router
    let app = Router::new()
        .route("/health", get(health_check))
        .route("/kv/:key", get(get_value))
        .route("/kv/:key", put(set_value))
        .route("/kv/:key", delete(delete_value))
        .with_state(store);

    // Start server
    let addr = "127.0.0.1:8080";
    info!("Starting KV server on {}", addr);
    
    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    axum::serve(listener, app).await.unwrap();
}
