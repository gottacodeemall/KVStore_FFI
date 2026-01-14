// Exceptions.cs - Exception types for the KV Store SDK

namespace KvStore;

/// <summary>
/// Base exception for KV Store operations.
/// </summary>
public class KvStoreException : Exception
{
    public ErrorCode ErrorCode { get; }

    public KvStoreException(string message, ErrorCode errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thrown when a key is not found.
/// </summary>
public class KeyNotFoundException : KvStoreException
{
    public string Key { get; }

    public KeyNotFoundException(string key)
        : base($"Key not found: {key}", ErrorCode.KeyNotFound)
    {
        Key = key;
    }
}

/// <summary>
/// Thrown when connection to the server fails.
/// </summary>
public class ConnectionException : KvStoreException
{
    public ConnectionException(string message)
        : base(message, ErrorCode.ConnectionError)
    {
    }
}
