// async_ffi_overhead_benchmark.cpp - Async FFI Overhead Analysis
//
// This benchmark measures async FFI overhead across:
// 1. Multiple concurrency levels: 1, 4, 16, 64, 256 concurrent operations
// 2. Multiple value sizes: 64B, 1KB, 16KB, 256KB
//
// Methodology:
// - Pure async FFI overhead = Cache hit time (no network)
// - Compare with blocking FFI overhead
// - Measure future completion latency

#include <iostream>
#include <iomanip>
#include <chrono>
#include <thread>
#include <vector>
#include <atomic>
#include <mutex>
#include <string>
#include <cstring>
#include <sstream>
#include <future>

#ifdef CPPHTTPLIB_OPENSSL_SUPPORT
#undef CPPHTTPLIB_OPENSSL_SUPPORT
#endif
#define CPPHTTPLIB_NO_OPENSSL
#include <httplib.h>
#include <nlohmann/json.hpp>

#include "kv_store.hpp"
#include "kv_store_async.hpp"

using json = nlohmann::json;
using namespace std::chrono;

// ============================================================================
// COMPILER-SPECIFIC DO-NOT-OPTIMIZE
// ============================================================================

#ifdef _MSC_VER
#include <intrin.h>
#pragma optimize("", off)
template<typename T>
void DoNotOptimize(T const& value) {
    _ReadWriteBarrier();
    (void)value;
}
#pragma optimize("", on)
#else
template<typename T>
void DoNotOptimize(T const& value) {
    asm volatile("" : : "r,m"(value) : "memory");
}
#endif

// ============================================================================
// CONFIGURATION
// ============================================================================

constexpr const char* SERVER_HOST = "127.0.0.1";
constexpr int SERVER_PORT = 8080;
constexpr int WARMUP_ITERATIONS = 100;
constexpr int MEASUREMENT_ITERATIONS = 3000;

// Value sizes to test
const std::vector<size_t> VALUE_SIZES = {64, 1024, 16384, 262144};  // 64B, 1KB, 16KB, 256KB

// Concurrency levels to test
const std::vector<int> CONCURRENCY_LEVELS = {1, 4, 16, 64, 256};

// ============================================================================
// RESULT STRUCTURES
// ============================================================================

struct BenchmarkResult {
    double mean_ns;
    double p50_ns;
    double p99_ns;
    double throughput_ops_sec;
    uint64_t total_ops;
};

struct AsyncFFIOverheadResult {
    size_t value_size;
    int concurrency;
    double async_cache_hit_ns;
    double sync_cache_hit_ns;
    double async_overhead_ns;
    double overhead_pct;
};

// ============================================================================
// UTILITIES
// ============================================================================

std::string GenerateValue(size_t size) {
    std::string value;
    value.reserve(size);
    for (size_t i = 0; i < size; ++i) {
        value += static_cast<char>('A' + (i % 26));
    }
    return value;
}

std::string FormatSize(size_t bytes) {
    if (bytes >= 1024 * 1024) return std::to_string(bytes / (1024 * 1024)) + "MB";
    if (bytes >= 1024) return std::to_string(bytes / 1024) + "KB";
    return std::to_string(bytes) + "B";
}

template<typename T>
double Percentile(std::vector<T>& data, double pct) {
    std::sort(data.begin(), data.end());
    size_t idx = static_cast<size_t>(data.size() * pct);
    if (idx >= data.size()) idx = data.size() - 1;
    return static_cast<double>(data[idx]);
}

// ============================================================================
// DIRECT HTTP CLIENT (for setup)
// ============================================================================

class DirectHttpClient {
public:
    DirectHttpClient() : client_(SERVER_HOST, SERVER_PORT) {
        client_.set_keep_alive(true);
        client_.set_connection_timeout(seconds(30));
        client_.set_read_timeout(seconds(30));
    }
    
    std::string Get(const std::string& key) {
        auto res = client_.Get(("/kv/" + key).c_str());
        if (res && res->status == 200) {
            auto j = json::parse(res->body);
            return j["value"].get<std::string>();
        }
        return "";
    }
    
    void Set(const std::string& key, const std::string& value) {
        json body;
        body["value"] = value;
        client_.Put(("/kv/" + key).c_str(), body.dump(), "application/json");
    }

private:
    httplib::Client client_;
};

// ============================================================================
// ASYNC BENCHMARK RUNNER
// ============================================================================

BenchmarkResult RunAsyncCacheHitBenchmark(
    const std::string& key,
    size_t value_size,
    int concurrency,
    int iterations_per_task
) {
    // Create a shared async client
    kvstore::Config config;
    config.server_url = std::string("http://") + SERVER_HOST + ":" + std::to_string(SERVER_PORT);
    config.cache_ttl = hours{1};  // Long TTL
    config.cache_capacity = 10000;
    config.connection_pool_size = 10;
    
    kvstore::KvStoreAsyncClient client(config);
    
    // Warm up cache
    {
        auto future = client.GetRefAsync(key);
        auto ref = future.get();
    }
    
    std::atomic<uint64_t> total_ops{0};
    std::vector<double> all_latencies;
    std::mutex latency_mutex;
    
    auto worker = [&](int task_id) {
        std::vector<double> local_latencies;
        local_latencies.reserve(iterations_per_task);
        
        for (int i = 0; i < iterations_per_task; ++i) {
            auto start = high_resolution_clock::now();
            
            // Use async GetRef
            auto future = client.GetRefAsync(key);
            auto ref = future.get();
            auto view = ref.view();
            
            // Prevent optimization
            DoNotOptimize(view.data());
            DoNotOptimize(view.size());
            
            auto end = high_resolution_clock::now();
            
            local_latencies.push_back(
                duration_cast<nanoseconds>(end - start).count()
            );
            total_ops.fetch_add(1, std::memory_order_relaxed);
        }
        
        std::lock_guard<std::mutex> lock(latency_mutex);
        all_latencies.insert(all_latencies.end(),
                            local_latencies.begin(), local_latencies.end());
    };
    
    auto start = high_resolution_clock::now();
    
    std::vector<std::thread> threads;
    for (int t = 0; t < concurrency; ++t) {
        threads.emplace_back(worker, t);
    }
    for (auto& t : threads) {
        t.join();
    }
    
    auto end = high_resolution_clock::now();
    double total_time_sec = duration_cast<microseconds>(end - start).count() / 1e6;
    
    BenchmarkResult result;
    result.total_ops = total_ops.load();
    
    if (!all_latencies.empty()) {
        double sum = 0;
        for (auto l : all_latencies) sum += l;
        result.mean_ns = sum / all_latencies.size();
        result.p50_ns = Percentile(all_latencies, 0.50);
        result.p99_ns = Percentile(all_latencies, 0.99);
    }
    result.throughput_ops_sec = result.total_ops / total_time_sec;
    
    return result;
}

// ============================================================================
// SYNC BENCHMARK RUNNER (for comparison)
// ============================================================================

BenchmarkResult RunSyncCacheHitBenchmark(
    const std::string& key,
    size_t value_size,
    int thread_count,
    int iterations_per_thread
) {
    // Create a shared client for all threads
    kvstore::Config config;
    config.server_url = std::string("http://") + SERVER_HOST + ":" + std::to_string(SERVER_PORT);
    config.cache_ttl = hours{1};  // Long TTL
    config.cache_capacity = 10000;
    config.connection_pool_size = 10;
    
    kvstore::KvStoreClient client(config);
    
    // Warm up cache
    auto _ = client.TryGet(key);
    
    std::atomic<uint64_t> total_ops{0};
    std::vector<double> all_latencies;
    std::mutex latency_mutex;
    
    auto worker = [&](int thread_id) {
        std::vector<double> local_latencies;
        local_latencies.reserve(iterations_per_thread);
        
        for (int i = 0; i < iterations_per_thread; ++i) {
            auto start = high_resolution_clock::now();
            
            // Use sync GetRef
            auto ref = client.GetRef(key);
            auto view = ref.view();
            
            // Prevent optimization
            DoNotOptimize(view.data());
            DoNotOptimize(view.size());
            
            auto end = high_resolution_clock::now();
            
            local_latencies.push_back(
                duration_cast<nanoseconds>(end - start).count()
            );
            total_ops.fetch_add(1, std::memory_order_relaxed);
        }
        
        std::lock_guard<std::mutex> lock(latency_mutex);
        all_latencies.insert(all_latencies.end(),
                            local_latencies.begin(), local_latencies.end());
    };
    
    auto start = high_resolution_clock::now();
    
    std::vector<std::thread> threads;
    for (int t = 0; t < thread_count; ++t) {
        threads.emplace_back(worker, t);
    }
    for (auto& t : threads) {
        t.join();
    }
    
    auto end = high_resolution_clock::now();
    double total_time_sec = duration_cast<microseconds>(end - start).count() / 1e6;
    
    BenchmarkResult result;
    result.total_ops = total_ops.load();
    
    if (!all_latencies.empty()) {
        double sum = 0;
        for (auto l : all_latencies) sum += l;
        result.mean_ns = sum / all_latencies.size();
        result.p50_ns = Percentile(all_latencies, 0.50);
        result.p99_ns = Percentile(all_latencies, 0.99);
    }
    result.throughput_ops_sec = result.total_ops / total_time_sec;
    
    return result;
}

// ============================================================================
// SETUP TEST DATA
// ============================================================================

void SetupTestData(const std::vector<size_t>& sizes) {
    DirectHttpClient client;
    
    std::cout << "Setting up test data...\n";
    for (size_t size : sizes) {
        std::string key = "async_bench_" + std::to_string(size);
        std::string value = GenerateValue(size);
        client.Set(key, value);
        
        // Verify
        auto retrieved = client.Get(key);
        if (retrieved.size() != size) {
            std::cerr << "ERROR: Failed to set key " << key 
                      << " (got " << retrieved.size() << " bytes, expected " << size << ")\n";
        } else {
            std::cout << "  - " << key << ": " << FormatSize(size) << " ✓\n";
        }
    }
    std::cout << "Test data ready.\n\n";
}

// ============================================================================
// MAIN
// ============================================================================

int main() {
    int hw_threads = std::thread::hardware_concurrency();
    
    std::cout << "╔══════════════════════════════════════════════════════════════════════╗\n";
    std::cout << "║       ASYNC FFI OVERHEAD BENCHMARK - KV Store C++ SDK                ║\n";
    std::cout << "╠══════════════════════════════════════════════════════════════════════╣\n";
    std::cout << "║  Hardware threads: " << std::setw(4) << hw_threads << std::setw(48) << " ║\n";
    std::cout << "║  Test concurrency: 1, 4, 16, 64, 256                                 ║\n";
    std::cout << "║  Value sizes: 64B, 1KB, 16KB, 256KB                                  ║\n";
    std::cout << "║  Iterations per test: " << MEASUREMENT_ITERATIONS << std::setw(45) << " ║\n";
    std::cout << "║  Estimated runtime: < 5 minutes                                      ║\n";
    std::cout << "╚══════════════════════════════════════════════════════════════════════╝\n\n";
    
    // Verify server connection
    {
        DirectHttpClient client;
        auto value = client.Get("key0");
        if (value.empty()) {
            std::cerr << "ERROR: Cannot connect to server at " 
                      << SERVER_HOST << ":" << SERVER_PORT << "\n";
            std::cerr << "Please start the server: cd server && cargo run --release\n";
            return 1;
        }
        std::cout << "✓ Server connection verified\n\n";
    }
    
    // Setup test data
    SetupTestData(VALUE_SIZES);
    
    // Results storage
    std::vector<AsyncFFIOverheadResult> results;
    
    // Run benchmarks for each value size and concurrency level
    for (size_t value_size : VALUE_SIZES) {
        std::string key = "async_bench_" + std::to_string(value_size);
        
        std::cout << "═══════════════════════════════════════════════════════════════════════\n";
        std::cout << "  VALUE SIZE: " << FormatSize(value_size) << "\n";
        std::cout << "═══════════════════════════════════════════════════════════════════════\n";
        
        std::cout << std::setw(12) << "Concurrency" 
                  << std::setw(14) << "Async Hit"
                  << std::setw(14) << "Sync Hit"
                  << std::setw(14) << "Async Ovhd"
                  << std::setw(12) << "Diff"
                  << "\n";
        std::cout << std::string(66, '-') << "\n";
        
        for (int concurrency : CONCURRENCY_LEVELS) {
            // Skip very high concurrency for large values
            if (value_size >= 65536 && concurrency > 64) continue;
            
            int iters_per_task = MEASUREMENT_ITERATIONS / concurrency;
            if (iters_per_task < 100) iters_per_task = 100;
            
            // Run async benchmark (cache hit)
            auto async_result = RunAsyncCacheHitBenchmark(key, value_size, concurrency, iters_per_task);
            
            // Run sync benchmark for comparison (cache hit)
            auto sync_result = RunSyncCacheHitBenchmark(key, value_size, concurrency, iters_per_task);
            
            AsyncFFIOverheadResult ovhd;
            ovhd.value_size = value_size;
            ovhd.concurrency = concurrency;
            ovhd.async_cache_hit_ns = async_result.mean_ns;
            ovhd.sync_cache_hit_ns = sync_result.mean_ns;
            ovhd.async_overhead_ns = async_result.mean_ns - sync_result.mean_ns;
            ovhd.overhead_pct = sync_result.mean_ns > 0 
                ? ((async_result.mean_ns - sync_result.mean_ns) / sync_result.mean_ns) * 100.0 
                : 0;
            
            results.push_back(ovhd);
            
            // Print row
            std::cout << std::setw(12) << concurrency;
            std::cout << std::setw(12) << std::fixed << std::setprecision(0) 
                      << async_result.mean_ns << "ns";
            std::cout << std::setw(12) << std::fixed << std::setprecision(0) 
                      << sync_result.mean_ns << "ns";
            std::cout << std::setw(12) << std::fixed << std::setprecision(1)
                      << ovhd.async_overhead_ns << "ns";
            
            std::string sign = ovhd.overhead_pct >= 0 ? "+" : "";
            std::cout << std::setw(10) << std::fixed << std::setprecision(1) 
                      << sign << ovhd.overhead_pct << "%";
            
            std::cout << "\n";
        }
        std::cout << "\n";
    }
    
    // Summary
    std::cout << "\n╔══════════════════════════════════════════════════════════════════════╗\n";
    std::cout << "║                          SUMMARY                                     ║\n";
    std::cout << "╚══════════════════════════════════════════════════════════════════════╝\n\n";
    
    std::cout << "Async FFI Overhead (Cache Hit Path):\n";
    std::cout << "─────────────────────────────────────────────────────────────────────\n";
    
    std::cout << std::setw(10) << "Size"
              << std::setw(12) << "1T"
              << std::setw(12) << "4T"
              << std::setw(12) << "16T"
              << std::setw(12) << "64T"
              << std::setw(12) << "256T"
              << "\n";
    std::cout << std::string(70, '-') << "\n";
    
    for (size_t size : VALUE_SIZES) {
        std::cout << std::setw(10) << FormatSize(size);
        
        for (int tc : {1, 4, 16, 64, 256}) {
            auto it = std::find_if(results.begin(), results.end(), 
                [&](const AsyncFFIOverheadResult& r) {
                    return r.value_size == size && r.concurrency == tc;
                });
            
            if (it != results.end()) {
                std::cout << std::setw(10) << std::fixed << std::setprecision(0) 
                          << it->async_cache_hit_ns << "ns";
            } else {
                std::cout << std::setw(12) << "-";
            }
        }
        std::cout << "\n";
    }
    
    std::cout << "\nAsync vs Sync Comparison:\n";
    std::cout << "─────────────────────────────────────────────────────────────────────\n";
    std::cout << std::setw(10) << "Size"
              << std::setw(14) << "Concurrency"
              << std::setw(12) << "Async"
              << std::setw(12) << "Sync"
              << std::setw(14) << "Overhead"
              << "\n";
    std::cout << std::string(62, '-') << "\n";
    
    for (const auto& r : results) {
        if (r.concurrency == 1 || r.concurrency == 64 || r.concurrency == 256) {
            std::string sign = r.overhead_pct >= 0 ? "+" : "";
            std::cout << std::setw(10) << FormatSize(r.value_size)
                      << std::setw(14) << r.concurrency
                      << std::setw(10) << std::fixed << std::setprecision(0) << r.async_cache_hit_ns << "ns"
                      << std::setw(10) << std::fixed << std::setprecision(0) << r.sync_cache_hit_ns << "ns"
                      << std::setw(11) << std::fixed << std::setprecision(1) << sign << r.overhead_pct << "%"
                      << "\n";
        }
    }
    
    std::cout << "\nValidation Results:\n";
    std::cout << "───────────────────\n";
    
    // Find worst-case async FFI overhead at high concurrency
    double worst_async_ns = 0;
    int worst_concurrency = 0;
    size_t worst_size = 0;
    
    for (const auto& r : results) {
        if (r.concurrency >= 16 && r.async_cache_hit_ns > worst_async_ns) {
            worst_async_ns = r.async_cache_hit_ns;
            worst_concurrency = r.concurrency;
            worst_size = r.value_size;
        }
    }
    
    std::cout << "\n  Worst-case async FFI overhead at high concurrency:\n";
    std::cout << "    " << std::fixed << std::setprecision(1) << worst_async_ns 
              << " ns at " << worst_concurrency << " tasks, " << FormatSize(worst_size) << "\n";
    
    // Check if async FFI overhead is acceptable (5μs limit for async)
    bool all_pass = true;
    for (const auto& r : results) {
        if (r.async_cache_hit_ns >= 5000) {
            all_pass = false;
            break;
        }
    }
    
    if (all_pass) {
        std::cout << "\n  ✓ Async FFI overhead is acceptable (< 5μs)\n";
    } else {
        std::cout << "\n  ✗ Async FFI overhead exceeds 5μs\n";
    }
    
    // Calculate average async overhead vs sync at high concurrency
    double sum_overhead = 0;
    int count = 0;
    for (const auto& r : results) {
        if (r.concurrency >= 16) {
            sum_overhead += r.async_overhead_ns;
            count++;
        }
    }
    double avg_overhead = count > 0 ? sum_overhead / count : 0;
    
    std::cout << "\n  Average async overhead vs sync (at high concurrency): " 
              << std::fixed << std::setprecision(1) << avg_overhead << "ns\n";
    
    // Final verdict
    std::cout << "\n═══════════════════════════════════════════════════════════════════════\n";
    if (all_pass) {
        std::cout << "  RESULT: ✓ PASS - Async FFI overhead within acceptable limits\n";
    } else {
        std::cout << "  RESULT: ✗ FAIL - Async FFI overhead exceeds acceptable limits\n";
    }
    std::cout << "═══════════════════════════════════════════════════════════════════════\n";
    
    return all_pass ? 0 : 1;
}
