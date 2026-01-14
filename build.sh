# Build script for Unix (Linux/macOS)
#!/bin/bash

set -e

echo "============================================================"
echo "Building KV Store - Phase 1"
echo "============================================================"
echo ""

# 1. Build Rust Server
echo "Building Rust Server..."
cd server
cargo build --release
cd ..
echo "Server built successfully."
echo ""

# 2. Build Rust Core SDK
echo "Building Rust Core SDK..."
cd core-sdk
cargo build --release
cd ..
echo "Core SDK built successfully."
echo ""

# 3. Build C++ Binding and Benchmarks
echo "Building C++ Binding and Benchmarks..."
cd bindings/cpp

mkdir -p build
cd build
cmake -DCMAKE_BUILD_TYPE=Release ..
cmake --build . --config Release
cd ..
cd ../..

echo "C++ Binding built successfully."
echo ""

echo "============================================================"
echo "Build Complete!"
echo "============================================================"
echo ""
echo "To run benchmarks:"
echo "  1. Start the server in a new terminal:"
echo "     cd server && cargo run --release"
echo ""
echo "  2. Run benchmarks:"
echo "     ./bindings/cpp/build/kv_benchmark"
echo ""
