//! Error types for the KV Store SDK

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum ErrorCode {
    Success = 0,
    KeyNotFound = 1,
    ConnectionError = 2,
    Timeout = 3,
    InvalidInput = 4,
    InternalError = 5,
}

impl From<i32> for ErrorCode {
    fn from(code: i32) -> Self {
        match code {
            0 => ErrorCode::Success,
            1 => ErrorCode::KeyNotFound,
            2 => ErrorCode::ConnectionError,
            3 => ErrorCode::Timeout,
            4 => ErrorCode::InvalidInput,
            _ => ErrorCode::InternalError,
        }
    }
}
