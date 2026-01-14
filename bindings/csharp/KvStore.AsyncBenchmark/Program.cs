// Async FFI Overhead Benchmark for C# Binding
//
// Measures async FFI overhead across:
// 1. Multiple concurrency levels: 1, 4, 16, 64, 256 parallel tasks
// 2. Multiple value sizes: 64B, 1KB, 16KB, 256KB
//
// Methodology:
// - Pure async FFI overhead = Cache hit time (no network)
// - Compare with blocking FFI overhead
// - Measure task completion latency

using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KvStore;

const string ServerHost = "127.0.0.1";
const int ServerPort = 8080;
const int WarmupIterations = 100;
const int MeasurementIterations = 3000;

// Value sizes to test
int[] valueSizes = [64, 1024, 16384, 262144];  // 64B, 1KB, 16KB, 256KB

// Concurrency levels to test
int[] concurrencyLevels = [1, 4, 16, 64, 256];

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║       ASYNC FFI OVERHEAD BENCHMARK - KV Store C# SDK                 ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Hardware threads: {Environment.ProcessorCount,4}                                              ║");
Console.WriteLine("║  Test concurrency: 1, 4, 16, 64, 256 parallel tasks                  ║");
Console.WriteLine("║  Value sizes: 64B, 1KB, 16KB, 256KB                                  ║");
Console.WriteLine($"║  Iterations per test: {MeasurementIterations}                                           ║");
Console.WriteLine("║  Estimated runtime: < 5 minutes                                      ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Verify server connection
using var httpClient = new HttpClient { BaseAddress = new Uri($"http://{ServerHost}:{ServerPort}") };
try
{
    var response = await httpClient.GetAsync("/kv/key0");
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"ERROR: Server returned {response.StatusCode}");
        return 1;
    }
    Console.WriteLine("✓ Server connection verified\n");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: Cannot connect to server at {ServerHost}:{ServerPort}");
    Console.WriteLine($"  {ex.Message}");
    Console.WriteLine("Please start the server: cd server && cargo run --release");
    return 1;
}

// Setup test data
Console.WriteLine("Setting up test data...");
foreach (var size in valueSizes)
{
    var key = $"async_bench_{size}";
    var value = new string('A', size);
    var content = JsonContent.Create(new { value });
    var response = await httpClient.PutAsync($"/kv/{key}", content);
    
    // Verify
    var getResponse = await httpClient.GetFromJsonAsync<JsonElement>($"/kv/{key}");
    var retrievedValue = getResponse.GetProperty("value").GetString();
    if (retrievedValue?.Length != size)
    {
        Console.WriteLine($"  - {key}: FAILED (got {retrievedValue?.Length ?? 0} bytes, expected {size})");
    }
    else
    {
        Console.WriteLine($"  - {key}: {FormatSize(size)} ✓");
    }
}
Console.WriteLine("Test data ready.\n");

// Results storage
var asyncResults = new List<AsyncFFIOverheadResult>();
var syncResults = new List<AsyncFFIOverheadResult>();

// Run benchmarks
foreach (var valueSize in valueSizes)
{
    var key = $"async_bench_{valueSize}";
    var keyBytes = Encoding.UTF8.GetBytes(key);
    
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  VALUE SIZE: {FormatSize(valueSize)}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    
    Console.WriteLine($"{"Concurrency",12}{"Async Hit",14}{"Sync Hit",14}{"Async Ovhd",14}{"Diff",12}");
    Console.WriteLine(new string('-', 66));
    
    foreach (var concurrency in concurrencyLevels)
    {
        // Skip very high concurrency for large values
        if (valueSize >= 65536 && concurrency > 64) continue;
        
        var itersPerTask = Math.Max(100, MeasurementIterations / concurrency);
        
        // Run async benchmark (cache hit)
        var asyncHit = await RunAsyncCacheHitBenchmark(key, valueSize, concurrency, itersPerTask);
        
        // Run sync benchmark for comparison (cache hit)
        var syncHit = RunSyncCacheHitBenchmark(keyBytes, valueSize, concurrency, itersPerTask);
        
        var asyncResult = new AsyncFFIOverheadResult
        {
            ValueSize = valueSize,
            Concurrency = concurrency,
            AsyncCacheHitNs = asyncHit.MeanNs,
            SyncCacheHitNs = syncHit.MeanNs,
            AsyncOverheadNs = asyncHit.MeanNs - syncHit.MeanNs,
            OverheadPct = syncHit.MeanNs > 0 ? ((asyncHit.MeanNs - syncHit.MeanNs) / syncHit.MeanNs) * 100 : 0
        };
        
        asyncResults.Add(asyncResult);
        
        // Print row
        Console.Write($"{concurrency,12}");
        Console.Write($"{asyncHit.MeanNs,12:F0}ns");
        Console.Write($"{syncHit.MeanNs,12:F0}ns");
        Console.Write($"{asyncResult.AsyncOverheadNs,12:F1}ns");
        
        var diffSign = asyncResult.OverheadPct >= 0 ? "+" : "";
        Console.Write($"{diffSign}{asyncResult.OverheadPct,10:F1}%");
        
        Console.WriteLine();
    }
    Console.WriteLine();
}

// Summary
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                          SUMMARY                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
Console.WriteLine();

Console.WriteLine("Async FFI Overhead (Cache Hit Path):");
Console.WriteLine("─────────────────────────────────────────────────────────────────────");
Console.WriteLine($"{"Size",10}{"1T",12}{"4T",12}{"16T",12}{"64T",12}{"256T",12}");
Console.WriteLine(new string('-', 70));

foreach (var size in valueSizes)
{
    Console.Write($"{FormatSize(size),10}");
    foreach (var tc in new[] { 1, 4, 16, 64, 256 })
    {
        var r = asyncResults.FirstOrDefault(x => x.ValueSize == size && x.Concurrency == tc);
        if (r != null)
            Console.Write($"{r.AsyncCacheHitNs,10:F0}ns");
        else
            Console.Write($"{"-",12}");
    }
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("Async vs Sync Comparison:");
Console.WriteLine("─────────────────────────────────────────────────────────────────────");
Console.WriteLine($"{"Size",10}{"Concurrency",14}{"Async",12}{"Sync",12}{"Overhead",14}");
Console.WriteLine(new string('-', 62));

foreach (var r in asyncResults.Where(r => r.Concurrency == 1 || r.Concurrency == 64 || r.Concurrency == 256))
{
    var diffSign = r.OverheadPct >= 0 ? "+" : "";
    Console.WriteLine($"{FormatSize(r.ValueSize),10}{r.Concurrency,14}{r.AsyncCacheHitNs,10:F0}ns{r.SyncCacheHitNs,10:F0}ns{diffSign}{r.OverheadPct,11:F1}%");
}

Console.WriteLine();
Console.WriteLine("Validation Results:");
Console.WriteLine("───────────────────");

// Find worst-case async FFI overhead at high concurrency
var worstResult = asyncResults
    .Where(r => r.Concurrency >= 16)
    .OrderByDescending(r => r.AsyncCacheHitNs)
    .FirstOrDefault();

if (worstResult != null)
{
    Console.WriteLine($"\n  Worst-case async FFI overhead at high concurrency:");
    Console.WriteLine($"    {worstResult.AsyncCacheHitNs:F1} ns at {worstResult.Concurrency} tasks, {FormatSize(worstResult.ValueSize)}");
}

// Check if async FFI overhead is acceptable
bool allPass = asyncResults.All(r => r.AsyncCacheHitNs < 5000);  // 5μs limit for async

if (allPass)
{
    Console.WriteLine("\n  ✓ Async FFI overhead is acceptable (< 5μs)");
}
else
{
    Console.WriteLine("\n  ✗ Async FFI overhead exceeds 5μs");
}

// Check async vs sync overhead
var avgOverhead = asyncResults.Where(r => r.Concurrency >= 16).Average(r => r.AsyncOverheadNs);
Console.WriteLine($"\n  Average async overhead vs sync (at high concurrency): {avgOverhead:F1}ns");

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
if (allPass)
    Console.WriteLine("  RESULT: ✓ PASS - Async FFI overhead within acceptable limits");
else
    Console.WriteLine("  RESULT: ✗ FAIL - Async FFI overhead exceeds acceptable limits");
Console.WriteLine("═══════════════════════════════════════════════════════════════════════");

return allPass ? 0 : 1;

// ============================================================================
// Helper Methods
// ============================================================================

string FormatSize(int bytes)
{
    if (bytes >= 1024 * 1024) return $"{bytes / (1024 * 1024)}MB";
    if (bytes >= 1024) return $"{bytes / 1024}KB";
    return $"{bytes}B";
}

[MethodImpl(MethodImplOptions.NoInlining)]
void DoNotOptimize<T>(T value)
{
    _ = value;
}

async Task<BenchmarkResult> RunAsyncCacheHitBenchmark(string key, int valueSize, int concurrency, int iterationsPerTask)
{
    // Create a shared async client
    using var client = new KvStoreAsyncClient(new KvStoreConfig
    {
        ServerUrl = $"http://{ServerHost}:{ServerPort}",
        CacheTtlMs = 3600000,  // 1 hour TTL
        CacheCapacity = 10000,
        ConnectionPoolSize = 10
    });
    
    // Warm up cache
    _ = await client.TryGetAsync(key);
    
    long totalOps = 0;
    var allLatencies = new List<double>();
    var latencyLock = new object();
    
    var tasks = new Task[concurrency];
    var sw = Stopwatch.StartNew();
    
    for (int t = 0; t < concurrency; t++)
    {
        tasks[t] = Task.Run(async () =>
        {
            var localLatencies = new List<double>(iterationsPerTask);
            
            for (int i = 0; i < iterationsPerTask; i++)
            {
                var start = Stopwatch.GetTimestamp();
                
                // Use GetRefAsync for zero-copy
                using var valueRef = await client.GetRefAsync(key);
                var len = valueRef.Length;
                
                // Prevent optimization
                DoNotOptimize(len);
                
                var end = Stopwatch.GetTimestamp();
                var ns = (end - start) * 1_000_000_000.0 / Stopwatch.Frequency;
                localLatencies.Add(ns);
                Interlocked.Increment(ref totalOps);
            }
            
            lock (latencyLock)
            {
                allLatencies.AddRange(localLatencies);
            }
        });
    }
    
    await Task.WhenAll(tasks);
    sw.Stop();
    
    return new BenchmarkResult
    {
        MeanNs = allLatencies.Count > 0 ? allLatencies.Average() : 0,
        TotalOps = totalOps,
        ThroughputOpsPerSec = totalOps / sw.Elapsed.TotalSeconds
    };
}

BenchmarkResult RunSyncCacheHitBenchmark(byte[] keyBytes, int valueSize, int threadCount, int iterationsPerThread)
{
    // Create a shared client for all threads
    using var client = new KvStoreClient(new KvStoreConfig
    {
        ServerUrl = $"http://{ServerHost}:{ServerPort}",
        CacheTtlMs = 3600000,  // 1 hour TTL
        CacheCapacity = 10000,
        ConnectionPoolSize = 10
    });
    
    // Warm up cache
    var key = Encoding.UTF8.GetString(keyBytes);
    _ = client.TryGet(key);
    
    long totalOps = 0;
    var allLatencies = new List<double>();
    var latencyLock = new object();
    
    var threads = new Thread[threadCount];
    var sw = Stopwatch.StartNew();
    
    for (int t = 0; t < threadCount; t++)
    {
        threads[t] = new Thread(() =>
        {
            var localLatencies = new List<double>(iterationsPerThread);
            
            for (int i = 0; i < iterationsPerThread; i++)
            {
                var start = Stopwatch.GetTimestamp();
                
                // Use GetRef for zero-copy
                using var valueRef = client.GetRef(keyBytes);
                var len = valueRef.Length;
                
                // Prevent optimization
                DoNotOptimize(len);
                
                var end = Stopwatch.GetTimestamp();
                var ns = (end - start) * 1_000_000_000.0 / Stopwatch.Frequency;
                localLatencies.Add(ns);
                Interlocked.Increment(ref totalOps);
            }
            
            lock (latencyLock)
            {
                allLatencies.AddRange(localLatencies);
            }
        });
        threads[t].Start();
    }
    
    foreach (var thread in threads)
        thread.Join();
    
    sw.Stop();
    
    return new BenchmarkResult
    {
        MeanNs = allLatencies.Count > 0 ? allLatencies.Average() : 0,
        TotalOps = totalOps,
        ThroughputOpsPerSec = totalOps / sw.Elapsed.TotalSeconds
    };
}

// ============================================================================
// Types
// ============================================================================

record struct BenchmarkResult
{
    public double MeanNs;
    public long TotalOps;
    public double ThroughputOpsPerSec;
}

record class AsyncFFIOverheadResult
{
    public int ValueSize;
    public int Concurrency;
    public double AsyncCacheHitNs;
    public double SyncCacheHitNs;
    public double AsyncOverheadNs;
    public double OverheadPct;
}
