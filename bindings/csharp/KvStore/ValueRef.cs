// ValueRef.cs - Zero-copy value reference with RAII semantics
//
// This class provides zero-copy access to cached values via ReadOnlySpan<byte>.
// The underlying Rust memory remains valid until Dispose() is called.

using System.Runtime.InteropServices;
using System.Text;
using KvStore.Native;

namespace KvStore;

/// <summary>
/// RAII wrapper for KvGetResult - ensures release is called.
/// Provides zero-copy access to cached values via ReadOnlySpan&lt;byte&gt;.
/// </summary>
/// <remarks>
/// CRITICAL: The Span returned by AsSpan() is only valid while this ValueRef exists.
/// Do not store the Span or use it after disposing the ValueRef.
/// </remarks>
public sealed class ValueRef : IDisposable
{
    private readonly IntPtr _client;
    private KvGetResult _result;
    private bool _disposed;

    internal ValueRef(IntPtr client, KvGetResult result)
    {
        _client = client;
        _result = result;
        _disposed = false;
    }

    /// <summary>
    /// Gets whether the operation was successful.
    /// </summary>
    public bool IsSuccess => _result.ErrorCode == 0;

    /// <summary>
    /// Gets the error code (0 = success).
    /// </summary>
    public ErrorCode Error => (ErrorCode)_result.ErrorCode;

    /// <summary>
    /// Gets a zero-copy span view of the value.
    /// WARNING: This span is only valid while the ValueRef is not disposed!
    /// </summary>
    public unsafe ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfDisposed();
        if (!IsSuccess || _result.Value.Ptr == IntPtr.Zero)
            return ReadOnlySpan<byte>.Empty;

        return new ReadOnlySpan<byte>(
            (void*)_result.Value.Ptr,
            (int)_result.Value.Len);
    }

    /// <summary>
    /// Gets a zero-copy string view using UTF-8 decoding.
    /// WARNING: This creates a string copy. Use AsSpan() for true zero-copy.
    /// </summary>
    public string AsString()
    {
        var span = AsSpan();
        if (span.IsEmpty)
            return string.Empty;
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Gets the length of the value in bytes.
    /// </summary>
    public int Length => IsSuccess ? (int)_result.Value.Len : 0;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_client != IntPtr.Zero)
            {
                NativeMethods.kv_store_release_get(_client, ref _result);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ValueRef));
    }
}
