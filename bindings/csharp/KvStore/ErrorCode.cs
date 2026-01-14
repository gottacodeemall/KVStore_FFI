// ErrorCode.cs - Error codes matching Rust SDK

namespace KvStore;

/// <summary>
/// Error codes returned by the KV Store SDK.
/// These must match the Rust ErrorCode enum.
/// </summary>
public enum ErrorCode
{
    /// <summary>Operation completed successfully.</summary>
    Success = 0,
    
    /// <summary>Key does not exist.</summary>
    KeyNotFound = 1,
    
    /// <summary>Failed to connect to server.</summary>
    ConnectionError = 2,
    
    /// <summary>Operation timed out.</summary>
    Timeout = 3,
    
    /// <summary>Invalid key or value.</summary>
    InvalidInput = 4,
    
    /// <summary>Internal SDK error.</summary>
    InternalError = 5
}
