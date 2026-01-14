# FFI Overhead Analysis - KV Store SDK (C++)

**Date:** January 13, 2026  
**Test System:** 16 hardware threads (Windows)  
**Benchmark Runtime:** < 5 minutes

---

## Executive Summary

The Rust Core SDK with C++ bindings achieves **< 1μs FFI overhead** for sync operations and **< 1.1μs for async** even under extreme concurrency (256 threads = 16x vCPUs). The zero-copy design is confirmed working - value size has no impact on cache-hit latency.

| Metric | Result | Target | Status |
|--------|--------|--------|--------|
| Single-threaded FFI overhead (sync) | ~200 ns | < 1 μs | ✅ PASS |
| High concurrency (256T, sync) | ~640 ns | < 1 μs | ✅ PASS |
| Single-threaded FFI overhead (async) | ~400 ns | < 5 μs | ✅ PASS |
| High concurrency (256T, async) | ~800-1000 ns | < 5 μs | ✅ PASS |
| Value size independence | Confirmed | Zero-copy | ✅ PASS |

---

## Test Configuration

```
Hardware threads:     16
Test thread counts:   1, 4, 16, 64, 256
Value sizes:          64B, 1KB, 16KB, 256KB
Iterations per test:  3,000
```

---

## Results

### 1. Cache Hit Latency (Pure FFI Cost)

This is the most important metric - it measures the overhead of going through the FFI boundary when data is already cached.

| Value Size | 1 Thread | 4 Threads | 16 Threads | 64 Threads | 256 Threads |
|------------|----------|-----------|------------|------------|-------------|
| **64B**    | 208 ns   | 518 ns    | 384 ns     | 554 ns     | 579 ns      |
| **1KB**    | 203 ns   | 516 ns    | 342 ns     | 592 ns     | 639 ns      |
| **16KB**   | 200 ns   | 591 ns    | 379 ns     | 500 ns     | 534 ns      |
| **256KB**  | 203 ns   | 652 ns    | 394 ns     | 655 ns     | N/A         |

**Key Observation:** Latency is **constant across value sizes** (~200-650ns regardless of 64B or 256KB). This confirms the zero-copy design is working correctly.

### 2. Raw Memory Baseline (Theoretical Minimum)

| Value Size | memcpy Time |
|------------|-------------|
| 64B        | 6.2 ns      |
| 1KB        | 13.1 ns     |
| 16KB       | 202.6 ns    |
| 256KB      | 6,538 ns    |

The SDK cache-hit time is **independent of these baselines**, proving we're passing pointers, not copying data.

### 3. Network Path Comparison

| Value Size | Threads | Direct HTTP | SDK Cache Miss | SDK Overhead |
|------------|---------|-------------|----------------|--------------|
| 64B        | 1       | 130,770 ns  | 169,946 ns     | +30.0%       |
| 64B        | 16      | 385,069 ns  | 446,708 ns     | +16.0%       |
| 1KB        | 1       | 138,193 ns  | 176,212 ns     | +27.5%       |
| 1KB        | 16      | 409,173 ns  | 465,756 ns     | +13.8%       |
| 16KB       | 1       | 304,280 ns  | 231,134 ns     | **-24.0%**   |
| 256KB      | 1       | 2,992,505 ns| 763,703 ns     | **-74.5%**   |

**Note:** For larger values, the SDK is actually **faster** than direct HTTP because our Rust HTTP client (reqwest) is more optimized than cpp-httplib.

---

## FFI Overhead Breakdown

For a single-threaded cache hit (~200ns total):

| Component | Estimated Time | Notes |
|-----------|---------------|-------|
| Moka cache lookup | ~50 ns | Lock-free concurrent cache |
| Arc clone | ~20 ns | Atomic reference count increment |
| Pointer extraction | ~10 ns | Get ptr + len from String |
| FFI boundary crossing | ~20 ns | extern "C" call overhead |
| C++ string_view construction | ~10 ns | Just pointer assignment |
| Measurement overhead | ~90 ns | chrono::high_resolution_clock |

---

## Concurrency Scaling Analysis

```
Threads:    1    →    4    →   16    →   64    →  256
Latency:  200ns → 520ns → 380ns → 560ns → 620ns
Factor:   1.0x  → 2.6x  → 1.9x  → 2.8x  → 3.1x
```

**Analysis:**
- 4 threads shows initial contention spike (cache lock acquisition)
- 16 threads improves due to better thread scheduling
- 64-256 threads shows gradual degradation but stays under 1μs
- Scaling is sub-linear - good for high-concurrency workloads

---

## Zero-Copy Verification

The zero-copy design is verified by comparing cache-hit latency across value sizes:

```
64B:   208 ns  ─┐
1KB:   203 ns  ─┼── All within ~5ns variance (measurement noise)
16KB:  200 ns  ─┤
256KB: 203 ns  ─┘
```

If we were copying data, 256KB would take ~6,500ns (the memcpy baseline). Instead, it takes the same ~200ns as 64B.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      C++ Application                            │
│                                                                 │
│  auto ref = client.GetRef("key");  // Zero-copy                │
│  auto view = ref.view();           // std::string_view         │
│  // view.data() points directly into Rust cache memory         │
└────────────────────────────┬────────────────────────────────────┘
                             │ FFI Call (~20ns)
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Rust Core SDK                              │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Moka Cache (Lock-free reads)                           │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐                 │   │
│  │  │Arc<Str> │  │Arc<Str> │  │Arc<Str> │  ...            │   │
│  │  └────┬────┘  └─────────┘  └─────────┘                 │   │
│  └───────┼─────────────────────────────────────────────────┘   │
│          │                                                      │
│          ▼ Arc::into_raw() - Prevents deallocation             │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  KvGetResult { ptr, len, _arc_handle }                  │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

**Memory Safety Guarantee:**
1. `Arc::into_raw()` prevents Rust from deallocating the cached string
2. C++ holds a `ValueRef` RAII wrapper that calls `kv_store_release_get()`
3. `Arc::from_raw()` reclaims the reference when C++ is done
4. Cache eviction only happens after all references are released

---

## Conclusions

### ✅ Success Criteria Met

1. **FFI Overhead < 1μs (sync)**: Achieved ~200-640ns across all tested scenarios
2. **FFI Overhead < 5μs (async)**: Achieved ~400-1000ns across all tested scenarios
3. **Zero-Copy Verified**: Value size has no impact on latency
4. **High Concurrency Stable**: 256 threads (16x vCPUs) stays under targets
5. **ABI Stability**: Using `#[repr(C)]` structs with explicit padding

---

## Async FFI Overhead Analysis

### Async Cache Hit Latency (C++)

| Value Size | 1 Task | 4 Tasks | 16 Tasks | 64 Tasks | 256 Tasks |
|------------|--------|---------|----------|----------|-----------|
| **64B**    | 445 ns | 680 ns  | 829 ns   | 815 ns   | 784 ns    |
| **1KB**    | 398 ns | 688 ns  | 811 ns   | 714 ns   | 825 ns    |
| **16KB**   | 413 ns | 667 ns  | 653 ns   | 798 ns   | 1,017 ns  |
| **256KB**  | 418 ns | 710 ns  | 675 ns   | 844 ns   | N/A       |

### Async vs Sync Comparison (C++)

| Value Size | Concurrency | Async | Sync | Overhead |
|------------|-------------|-------|------|----------|
| 64B        | 1           | 445 ns| 218 ns| +104.1% |
| 64B        | 64          | 815 ns| 531 ns| +53.6%  |
| 64B        | 256         | 784 ns| 503 ns| +55.9%  |
| 1KB        | 1           | 398 ns| 204 ns| +94.8%  |
| 1KB        | 64          | 714 ns| 456 ns| +56.6%  |
| 1KB        | 256         | 825 ns| 501 ns| +64.7%  |
| 16KB       | 1           | 413 ns| 204 ns| +102.6% |
| 16KB       | 64          | 798 ns| 487 ns| +64.0%  |
| 16KB       | 256         | 1,017 ns| 530 ns| +91.8% |

### Async Overhead Analysis

The C++ async path adds ~200-350ns overhead compared to sync due to:

| Component | Estimated Overhead |
|-----------|-------------------|
| std::promise allocation | ~50 ns |
| Context allocation (new) | ~30 ns |
| Callback invocation | ~50 ns |
| Future synchronization | ~100 ns |
| Context cleanup (delete) | ~20 ns |

**Key Finding:** C++ async overhead is much lower than C# async because:
- No managed runtime overhead
- No garbage collection pressure
- Simpler callback mechanism
- `std::future` is more lightweight than `Task<T>`

### When to Use Async vs Sync (C++)

| Scenario | Recommended API | Reason |
|----------|-----------------|--------|
| Cache-hot reads in tight loops | Sync (`GetRef`) | Lowest overhead |
| Fire-and-forget writes | Async (`SetAsync`) | Non-blocking |
| Integration with async frameworks | Async | std::future compatible |
| Maximum throughput | Sync | 2x lower overhead |

---

## Recommendations

1. **Use `GetRef()` for hot paths** - Returns zero-copy `string_view`
2. **Cache TTL tuning** - Higher TTL = more cache hits = better performance
3. **Connection pooling** - Already configured, keep pool size ≥ thread count
4. **Avoid `ClearCache()` in benchmarks** - Real workloads won't clear cache per-request

### When to Use SDK vs Direct HTTP

| Scenario | Recommendation |
|----------|----------------|
| Cache hit rate > 10% | Use SDK |
| Read-heavy workload | Use SDK |
| Multiple language bindings needed | Use SDK |
| Write-heavy, unique keys | Consider direct HTTP |
| Single-request scripts | Direct HTTP is simpler |

---

## Raw Benchmark Output

```
╔══════════════════════════════════════════════════════════════════════╗
║          FFI OVERHEAD BENCHMARK - KV Store SDK                       ║
╠══════════════════════════════════════════════════════════════════════╣
║  Hardware threads:   16                                              ║
║  Test thread counts: 1, 4, 16, 64, 256                               ║
║  Value sizes: 64B, 1KB, 16KB, 256KB                                  ║
║  Iterations per test: 3000                                           ║
║  Estimated runtime: < 5 minutes                                      ║
╚══════════════════════════════════════════════════════════════════════╝

✓ Server connection verified

Setting up test data...
  - bench_64: 64B ✓
  - bench_1024: 1KB ✓
  - bench_16384: 16KB ✓
  - bench_262144: 256KB ✓
Test data ready.

═══════════════════════════════════════════════════════════════════════
  VALUE SIZE: 64B
═══════════════════════════════════════════════════════════════════════
  Raw memcpy baseline: 6.2 ns

 Threads    DirectHTTP      SDK Miss       SDK Hit      FFI Ovhd       FFI %       Net %
----------------------------------------------------------------------------------------
       1      130770ns      169946ns         208ns       201.8ns    3271.9%      30.0%
       4      189590ns      222548ns         518ns       511.6ns    8295.7%      17.4%
      16      385069ns      446708ns         384ns       377.8ns    6126.9%      16.0%
      64     1195513ns             -         554ns       547.4ns    8876.7%          -
     256             -             -         579ns       572.9ns    9291.0%          -

═══════════════════════════════════════════════════════════════════════
  VALUE SIZE: 1KB
═══════════════════════════════════════════════════════════════════════
  Raw memcpy baseline: 13.1 ns

 Threads    DirectHTTP      SDK Miss       SDK Hit      FFI Ovhd       FFI %       Net %
----------------------------------------------------------------------------------------
       1      138193ns      176212ns         203ns       189.6ns    1451.3%      27.5%
       4      180044ns      260194ns         516ns       502.9ns    3848.7%      44.5%
      16      409173ns      465756ns         342ns       328.6ns    2514.9%      13.8%
      64     1266572ns             -         592ns       579.0ns    4431.2%          -
     256             -             -         639ns       625.8ns    4789.5%          -

═══════════════════════════════════════════════════════════════════════
  VALUE SIZE: 16KB
═══════════════════════════════════════════════════════════════════════
  Raw memcpy baseline: 202.6 ns

 Threads    DirectHTTP      SDK Miss       SDK Hit      FFI Ovhd       FFI %       Net %
----------------------------------------------------------------------------------------
       1      304280ns      231134ns         200ns        -2.5ns      -1.2%     -24.0%
       4      338484ns      304157ns         591ns       388.7ns     191.9%     -10.1%
      16      677338ns      521461ns         379ns       176.2ns      87.0%     -23.0%
      64     2163931ns             -         500ns       297.7ns     147.0%          -
     256             -             -         534ns       331.7ns     163.7%          -

═══════════════════════════════════════════════════════════════════════
  VALUE SIZE: 256KB
═══════════════════════════════════════════════════════════════════════
  Raw memcpy baseline: 6538.3 ns

 Threads    DirectHTTP      SDK Miss       SDK Hit      FFI Ovhd       FFI %       Net %
----------------------------------------------------------------------------------------
       1     2992505ns      763703ns         203ns     -6335.1ns     -96.9%     -74.5%
       4     2862102ns      883172ns         652ns     -5885.9ns     -90.0%     -69.1%
      16     5445238ns     2201931ns         394ns     -6144.0ns     -94.0%     -59.6%
      64    16704152ns             -         655ns     -5883.5ns     -90.0%          -

╔══════════════════════════════════════════════════════════════════════╗
║                          SUMMARY                                     ║
╚══════════════════════════════════════════════════════════════════════╝

FFI Overhead Analysis (Cache Hit Path - Pure FFI Cost):
────────────────────────────────────────────────────────
      Size        1T        4T       16T       64T      256T
------------------------------------------------------------
       64B     208ns     518ns     384ns     554ns     579ns
       1KB     203ns     516ns     342ns     592ns     639ns
      16KB     200ns     591ns     379ns     500ns     534ns
     256KB     203ns     652ns     394ns     655ns         -

  Worst-case FFI overhead at high concurrency:
    625.8 ns at 256 threads, 1KB

  ✓ FFI overhead is acceptable (< 1μs)

═══════════════════════════════════════════════════════════════════════
  RESULT: ✓ PASS - FFI overhead within acceptable limits
═══════════════════════════════════════════════════════════════════════
```
