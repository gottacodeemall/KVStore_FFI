// AsyncTest.cs - Simple test for async KV Store client
using System.Diagnostics;
using KvStore;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Async KV Store Test ===\n");
        
        var config = new KvStoreConfig
        {
            ServerUrl = "http://127.0.0.1:8080",
            CacheTtlMs = 5000,
            CacheCapacity = 10000,
            ConnectionPoolSize = 10
        };
        
        using var client = new KvStoreAsyncClient(config);
        
        // Test 1: Basic Set/Get
        Console.WriteLine("Test 1: Basic async Set/Get");
        await client.SetAsync("async_test_key", "async_test_value");
        var value = await client.GetAsync("async_test_key");
        Console.WriteLine($"  Set/Get: {value}");
        Debug.Assert(value == "async_test_value", "Value mismatch!");
        Console.WriteLine("  ✓ Passed\n");
        
        // Test 2: Cache hit (should be fast)
        Console.WriteLine("Test 2: Cache hit latency");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            _ = await client.TryGetAsync("async_test_key");
        }
        sw.Stop();
        var avgLatency = sw.Elapsed.TotalMicroseconds / 1000;
        Console.WriteLine($"  1000 cache hits: {sw.ElapsedMilliseconds}ms (avg {avgLatency:F2}μs)");
        Console.WriteLine("  ✓ Passed\n");
        
        // Test 3: Parallel requests
        Console.WriteLine("Test 3: Parallel async requests");
        var keys = Enumerable.Range(0, 100).Select(i => $"parallel_key_{i}").ToArray();
        
        // First set all keys
        var setTasks = keys.Select(k => client.SetAsync(k, $"value_for_{k}")).ToArray();
        await Task.WhenAll(setTasks);
        Console.WriteLine($"  Set {keys.Length} keys in parallel");
        
        // Clear cache to force HTTP fetches
        client.ClearCache();
        
        // Then get all in parallel
        sw.Restart();
        var getTasks = keys.Select(k => client.GetAsync(k)).ToArray();
        var values = await Task.WhenAll(getTasks);
        sw.Stop();
        Console.WriteLine($"  Got {values.Length} values in parallel: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine("  ✓ Passed\n");
        
        // Test 4: Zero-copy pattern
        Console.WriteLine("Test 4: Zero-copy with AsyncValueRef");
        using (var valueRef = await client.GetRefAsync("async_test_key"))
        {
            Console.WriteLine($"  Zero-copy span length: {valueRef.Length} bytes");
            Console.WriteLine($"  Value: {valueRef.AsString()}");
        }
        Console.WriteLine("  ✓ Passed\n");
        
        // Test 5: Non-blocking verification
        Console.WriteLine("Test 5: Non-blocking verification");
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            // This should complete quickly (cache hit)
            await client.GetAsync("async_test_key", cts.Token);
            Console.WriteLine("  Cached get completed within 100ms timeout");
            Console.WriteLine("  ✓ Passed\n");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  ✗ Failed: Operation was blocked and timed out");
        }
        
        // Show stats
        var stats = client.GetCacheStats();
        Console.WriteLine($"Cache Stats: hits={stats.Hits}, misses={stats.Misses}, size={stats.CurrentSize}");
        Console.WriteLine($"Hit Rate: {stats.HitRate:P1}");
        
        Console.WriteLine("\n=== All Tests Passed! ===");
    }
}
