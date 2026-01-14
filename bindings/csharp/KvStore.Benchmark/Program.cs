// FFI Overhead Benchmark for C# Binding
//
// Measures FFI overhead across:
// 1. Multiple concurrency levels: 1, 4, 16, 64, 256 threads
// 2. Multiple value sizes: 64B, 1KB, 16KB, 256KB
//
// Methodology:
// - Pure FFI overhead = Cache hit time (no network)
// - Network overhead = Direct HTTP vs SDK cache miss
// - Compare against theoretical minimum (raw memory copy)

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

// Value sizes to test (reduced for ~5 min total runtime)
int[] valueSizes = [64, 1024, 16384, 262144];  // 64B, 1KB, 16KB, 256KB

// Thread counts to test
int[] threadCounts = [1, 4, 16, 64, 256];

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║          FFI OVERHEAD BENCHMARK - KV Store C# SDK                    ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Hardware threads: {Environment.ProcessorCount,4}                                              ║");
Console.WriteLine("║  Test thread counts: 1, 4, 16, 64, 256                               ║");
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
    var key = $"bench_{size}";
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
var results = new List<FFIOverheadResult>();

// Run benchmarks
foreach (var valueSize in valueSizes)
{
    var key = $"bench_{valueSize}";
    var keyBytes = Encoding.UTF8.GetBytes(key);
    
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  VALUE SIZE: {FormatSize(valueSize)}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
    
    // Get baseline raw memory copy time
    var rawMemcpyNs = BenchmarkRawMemcpy(valueSize, MeasurementIterations);
    Console.WriteLine($"  Raw memory copy baseline: {rawMemcpyNs:F1} ns\n");
    
    Console.WriteLine($"{"Threads",8}{"DirectHTTP",14}{"SDK Miss",14}{"SDK Hit",14}{"FFI Ovhd",14}{"FFI %",12}{"Net %",12}");
    Console.WriteLine(new string('-', 88));
    
    foreach (var threadCount in threadCounts)
    {
        // Skip very high thread counts for large values
        if (valueSize >= 65536 && threadCount > 64) continue;
        
        var itersPerThread = Math.Max(100, MeasurementIterations / threadCount);
        
        // Warmup
        using (var warmupClient = new KvStoreClient(new KvStoreConfig
        {
            ServerUrl = $"http://{ServerHost}:{ServerPort}",
            CacheTtlMs = 3600000,
            CacheCapacity = 10000
        }))
        {
            for (int i = 0; i < WarmupIterations / 10; i++)
            {
                _ = warmupClient.TryGet(key);
            }
        }
        
        // Run benchmarks
        BenchmarkResult directHttp = default;
        BenchmarkResult sdkMiss = default;
        BenchmarkResult sdkHit;
        
        // Direct HTTP (only for reasonable thread counts)
        if (threadCount <= 64)
        {
            directHttp = await RunDirectHttpBenchmark(key, threadCount, itersPerThread);
        }
        
        // SDK Cache Miss (only for reasonable thread counts)
        if (threadCount <= 32)
        {
            sdkMiss = RunSdkCacheMissBenchmark(key, threadCount, itersPerThread);
        }
        
        // SDK Cache Hit (always run - this is what we care about for FFI overhead)
        sdkHit = RunSdkCacheHitBenchmark(keyBytes, valueSize, threadCount, itersPerThread);
        
        // Calculate overhead
        var result = new FFIOverheadResult
        {
            ValueSize = valueSize,
            ThreadCount = threadCount,
            DirectHttpNs = directHttp.MeanNs,
            SdkCacheMissNs = sdkMiss.MeanNs,
            SdkCacheHitNs = sdkHit.MeanNs,
            RawMemcpyNs = rawMemcpyNs,
            FfiOverheadNs = sdkHit.MeanNs - rawMemcpyNs,
            FfiOverheadPct = rawMemcpyNs > 0 ? ((sdkHit.MeanNs - rawMemcpyNs) / rawMemcpyNs) * 100 : 0
        };
        
        if (directHttp.MeanNs > 0 && sdkMiss.MeanNs > 0)
        {
            result.NetworkOverheadPct = ((sdkMiss.MeanNs - directHttp.MeanNs) / directHttp.MeanNs) * 100;
        }
        
        results.Add(result);
        
        // Print row
        Console.Write($"{threadCount,8}");
        
        if (directHttp.MeanNs > 0)
            Console.Write($"{directHttp.MeanNs,12:F0}ns");
        else
            Console.Write($"{"-",14}");
        
        if (sdkMiss.MeanNs > 0)
            Console.Write($"{sdkMiss.MeanNs,12:F0}ns");
        else
            Console.Write($"{"-",14}");
        
        Console.Write($"{sdkHit.MeanNs,12:F0}ns");
        Console.Write($"{result.FfiOverheadNs,12:F1}ns");
        Console.Write($"{result.FfiOverheadPct,10:F1}%");
        
        if (result.NetworkOverheadPct != 0)
            Console.Write($"{result.NetworkOverheadPct,10:F1}%");
        else
            Console.Write($"{"-",12}");
        
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

Console.WriteLine("FFI Overhead Analysis (Cache Hit Path - Pure FFI Cost):");
Console.WriteLine("────────────────────────────────────────────────────────────");
Console.WriteLine($"{"Size",10}{"1T",10}{"4T",10}{"16T",10}{"64T",10}{"256T",10}");
Console.WriteLine(new string('-', 60));

foreach (var size in valueSizes)
{
    Console.Write($"{FormatSize(size),10}");
    foreach (var tc in new[] { 1, 4, 16, 64, 256 })
    {
        var r = results.FirstOrDefault(x => x.ValueSize == size && x.ThreadCount == tc);
        if (r != null)
            Console.Write($"{r.SdkCacheHitNs,8:F0}ns");
        else
            Console.Write($"{"-",10}");
    }
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("Validation Results:");
Console.WriteLine("───────────────────");

// Find worst-case FFI overhead at high concurrency
var worstResult = results
    .Where(r => r.ThreadCount >= 16)
    .OrderByDescending(r => r.FfiOverheadNs)
    .FirstOrDefault();

if (worstResult != null)
{
    Console.WriteLine($"\n  Worst-case FFI overhead at high concurrency:");
    Console.WriteLine($"    {worstResult.FfiOverheadNs:F1} ns at {worstResult.ThreadCount} threads, {FormatSize(worstResult.ValueSize)}");
}

// Check if FFI overhead is acceptable
bool allPass = results.All(r => r.SdkCacheHitNs < 1000);

if (allPass)
{
    Console.WriteLine("\n  ✓ FFI overhead is acceptable (< 1μs)");
}
else
{
    Console.WriteLine("\n  ✗ FFI overhead exceeds 1μs");
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
if (allPass)
    Console.WriteLine("  RESULT: ✓ PASS - FFI overhead within acceptable limits");
else
    Console.WriteLine("  RESULT: ✗ FAIL - FFI overhead exceeds acceptable limits");
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

double BenchmarkRawMemcpy(int size, int iterations)
{
    var source = new byte[size];
    var dest = new byte[size];
    Array.Fill(source, (byte)'A');
    
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        Buffer.BlockCopy(source, 0, dest, 0, size);
        DoNotOptimize(dest);
    }
    sw.Stop();
    
    return (double)sw.Elapsed.TotalNanoseconds / iterations;
}

[MethodImpl(MethodImplOptions.NoInlining)]
void DoNotOptimize<T>(T value)
{
    // Prevent dead code elimination
    _ = value;
}

async Task<BenchmarkResult> RunDirectHttpBenchmark(string key, int threadCount, int iterationsPerThread)
{
    long totalOps = 0;
    var allLatencies = new List<double>();
    var latencyLock = new object();
    
    var tasks = new Task[threadCount];
    var sw = Stopwatch.StartNew();
    
    for (int t = 0; t < threadCount; t++)
    {
        tasks[t] = Task.Run(async () =>
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://{ServerHost}:{ServerPort}") };
            var localLatencies = new List<double>(iterationsPerThread);
            
            for (int i = 0; i < iterationsPerThread; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var response = await client.GetAsync($"/kv/{key}");
                var end = Stopwatch.GetTimestamp();
                
                if (response.IsSuccessStatusCode)
                {
                    var ns = (end - start) * 1_000_000_000.0 / Stopwatch.Frequency;
                    localLatencies.Add(ns);
                    Interlocked.Increment(ref totalOps);
                }
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

BenchmarkResult RunSdkCacheMissBenchmark(string key, int threadCount, int iterationsPerThread)
{
    long totalOps = 0;
    var allLatencies = new List<double>();
    var latencyLock = new object();
    
    var threads = new Thread[threadCount];
    var sw = Stopwatch.StartNew();
    
    for (int t = 0; t < threadCount; t++)
    {
        threads[t] = new Thread(() =>
        {
            using var client = new KvStoreClient(new KvStoreConfig
            {
                ServerUrl = $"http://{ServerHost}:{ServerPort}",
                CacheTtlMs = 1,  // Very short TTL
                CacheCapacity = 1,  // Tiny cache
                ConnectionPoolSize = 10
            });
            
            var localLatencies = new List<double>(iterationsPerThread);
            
            for (int i = 0; i < iterationsPerThread; i++)
            {
                client.ClearCache();  // Force cache miss
                
                var start = Stopwatch.GetTimestamp();
                try
                {
                    _ = client.Get(key);
                    var end = Stopwatch.GetTimestamp();
                    var ns = (end - start) * 1_000_000_000.0 / Stopwatch.Frequency;
                    localLatencies.Add(ns);
                    Interlocked.Increment(ref totalOps);
                }
                catch
                {
                    // Skip failed requests
                }
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

BenchmarkResult RunSdkCacheHitBenchmark(byte[] keyBytes, int valueSize, int threadCount, int iterationsPerThread)
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
                var span = valueRef.AsSpan();
                
                // Prevent optimization
                DoNotOptimize(span.Length);
                
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

record class FFIOverheadResult
{
    public int ValueSize;
    public int ThreadCount;
    public double DirectHttpNs;
    public double SdkCacheMissNs;
    public double SdkCacheHitNs;
    public double RawMemcpyNs;
    public double FfiOverheadNs;
    public double FfiOverheadPct;
    public double NetworkOverheadPct;
}
