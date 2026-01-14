# Build script for Windows
# Run from the ipc directory

Write-Host "============================================================"
Write-Host "Building KV Store - Phase 1"
Write-Host "============================================================"
Write-Host ""

$ErrorActionPreference = "Stop"

# 1. Build Rust Server
Write-Host "Building Rust Server..." -ForegroundColor Cyan
Push-Location server
cargo build --release
if ($LASTEXITCODE -ne 0) { throw "Server build failed" }
Pop-Location
Write-Host "Server built successfully." -ForegroundColor Green
Write-Host ""

# 2. Build Rust Core SDK
Write-Host "Building Rust Core SDK..." -ForegroundColor Cyan
Push-Location core-sdk
cargo build --release
if ($LASTEXITCODE -ne 0) { throw "Core SDK build failed" }
Pop-Location
Write-Host "Core SDK built successfully." -ForegroundColor Green
Write-Host ""

# 3. Build C++ Binding and Benchmarks
Write-Host "Building C++ Binding and Benchmarks..." -ForegroundColor Cyan
Push-Location bindings/cpp

if (!(Test-Path "build")) {
    New-Item -ItemType Directory -Name "build" | Out-Null
}

Push-Location build
cmake -G "Visual Studio 17 2022" -A x64 -DCMAKE_BUILD_TYPE=Release ..
cmake --build . --config Release
if ($LASTEXITCODE -ne 0) { throw "C++ build failed" }
Pop-Location
Pop-Location

Write-Host "C++ Binding built successfully." -ForegroundColor Green
Write-Host ""

Write-Host "============================================================"
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "============================================================"
Write-Host ""
Write-Host "To run benchmarks:"
Write-Host "  1. Start the server in a new terminal:"
Write-Host "     cd server && cargo run --release"
Write-Host ""
Write-Host "  2. Run benchmarks:"
Write-Host "     .\bindings\cpp\build\Release\kv_benchmark.exe"
Write-Host ""
