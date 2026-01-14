cmake_minimum_required(VERSION 3.15)
project(kv_store_cpp VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# ============================================================================
# OPTIONS
# ============================================================================

option(BUILD_BENCHMARKS "Build benchmarks" ON)

# ============================================================================
# PATHS
# ============================================================================

# Path to the Rust SDK library
set(RUST_SDK_DIR "${CMAKE_SOURCE_DIR}/../../core-sdk" CACHE PATH "Path to Rust SDK")
set(RUST_TARGET_DIR "${RUST_SDK_DIR}/target/release" CACHE PATH "Path to Rust target")

# ============================================================================
# KV STORE C++ BINDING (Header-only)
# ============================================================================

add_library(kv_store_cpp INTERFACE)
target_include_directories(kv_store_cpp INTERFACE
    ${CMAKE_SOURCE_DIR}/include
)

# Link against Rust library
if(WIN32)
    set(RUST_LIB_NAME "kv_core_sdk.dll.lib")
    set(RUST_DLL_NAME "kv_core_sdk.dll")
elseif(APPLE)
    set(RUST_LIB_NAME "libkv_core_sdk.dylib")
else()
    set(RUST_LIB_NAME "libkv_core_sdk.so")
endif()

target_link_libraries(kv_store_cpp INTERFACE
    ${RUST_TARGET_DIR}/${RUST_LIB_NAME}
)

# ============================================================================
# BENCHMARKS
# ============================================================================

if(BUILD_BENCHMARKS)
    # Download cpp-httplib for direct HTTP benchmark baseline
    include(FetchContent)
    
    # Important: disable SSL/ZLIB/Brotli for httplib before declaring it
    set(HTTPLIB_REQUIRE_OPENSSL OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_USE_OPENSSL_IF_AVAILABLE OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_REQUIRE_ZLIB OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_USE_ZLIB_IF_AVAILABLE OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_REQUIRE_BROTLI OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_USE_BROTLI_IF_AVAILABLE OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_COMPILE OFF CACHE BOOL "" FORCE)
    set(HTTPLIB_INSTALL OFF CACHE BOOL "" FORCE)
    
    FetchContent_Declare(
        httplib
        GIT_REPOSITORY https://github.com/yhirose/cpp-httplib.git
        GIT_TAG v0.14.3
    )
    FetchContent_MakeAvailable(httplib)
    
    # Download Google Benchmark
    FetchContent_Declare(
        benchmark
        GIT_REPOSITORY https://github.com/google/benchmark.git
        GIT_TAG v1.8.3
    )
    set(BENCHMARK_ENABLE_TESTING OFF CACHE BOOL "" FORCE)
    set(BENCHMARK_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(benchmark)
    
    # Download nlohmann/json for HTTP response parsing
    FetchContent_Declare(
        json
        GIT_REPOSITORY https://github.com/nlohmann/json.git
        GIT_TAG v3.11.3
    )
    FetchContent_MakeAvailable(json)
    
    # Benchmark executable (Google Benchmark based)
    add_executable(kv_benchmark
        benchmarks/benchmark_main.cpp
    )
    
    # Disable OpenSSL for httplib (we only need basic HTTP)
    target_compile_definitions(kv_benchmark PRIVATE
        CPPHTTPLIB_OPENSSL_SUPPORT=0
        CPPHTTPLIB_NO_EXCEPTIONS=0
    )
    
    target_link_libraries(kv_benchmark PRIVATE
        kv_store_cpp
        httplib::httplib
        benchmark::benchmark
        nlohmann_json::nlohmann_json
    )
    
    # FFI Overhead Benchmark (comprehensive analysis)
    add_executable(ffi_overhead_benchmark
        benchmarks/ffi_overhead_benchmark.cpp
    )
    
    target_compile_definitions(ffi_overhead_benchmark PRIVATE
        CPPHTTPLIB_OPENSSL_SUPPORT=0
        CPPHTTPLIB_NO_EXCEPTIONS=0
    )
    
    target_link_libraries(ffi_overhead_benchmark PRIVATE
        kv_store_cpp
        httplib::httplib
        nlohmann_json::nlohmann_json
    )
    
    # Async FFI Overhead Benchmark
    add_executable(async_ffi_overhead_benchmark
        benchmarks/async_ffi_overhead_benchmark.cpp
    )
    
    target_compile_definitions(async_ffi_overhead_benchmark PRIVATE
        CPPHTTPLIB_OPENSSL_SUPPORT=0
        CPPHTTPLIB_NO_EXCEPTIONS=0
    )
    
    target_link_libraries(async_ffi_overhead_benchmark PRIVATE
        kv_store_cpp
        httplib::httplib
        nlohmann_json::nlohmann_json
    )
    
    # Copy Rust DLL to output directory on Windows
    if(WIN32)
        add_custom_command(TARGET kv_benchmark POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${RUST_TARGET_DIR}/${RUST_DLL_NAME}"
                "$<TARGET_FILE_DIR:kv_benchmark>"
        )
        add_custom_command(TARGET ffi_overhead_benchmark POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${RUST_TARGET_DIR}/${RUST_DLL_NAME}"
                "$<TARGET_FILE_DIR:ffi_overhead_benchmark>"
        )
        add_custom_command(TARGET async_ffi_overhead_benchmark POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${RUST_TARGET_DIR}/${RUST_DLL_NAME}"
                "$<TARGET_FILE_DIR:async_ffi_overhead_benchmark>"
        )
    endif()
endif()
