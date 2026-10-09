// PS5GFC — PS5 Game Format Converter
// Copyright (C) 2026 OSØRIO
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

use std::io;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    Io(#[from] io::Error),

    #[error("invalid format: {0}")]
    Format(String),

    #[error("not supported: {0}")]
    Unsupported(String),

    #[error("{0}")]
    Invalid(String),

    #[error("operation cancelled")]
    Cancelled,

    #[error("console: {0}")]
    Remote(#[from] crate::remote::RemoteError),
}

pub type Result<T> = std::result::Result<T, Error>;

impl Error {
    pub fn format(msg: impl Into<String>) -> Self {
        Error::Format(msg.into())
    }
    pub fn unsupported(msg: impl Into<String>) -> Self {
        Error::Unsupported(msg.into())
    }
    pub fn invalid(msg: impl Into<String>) -> Self {
        Error::Invalid(msg.into())
    }

    pub fn localized(&self) -> String {
        match self {
            Error::Io(e) => crate::t!("err.io", e = e),
            Error::Format(m) => crate::t!("err.format", e = m),
            Error::Unsupported(m) => crate::t!("err.unsupported", e = m),
            Error::Invalid(m) => m.clone(),
            Error::Cancelled => crate::t!("err.cancelled"),
            Error::Remote(r) => {
                use crate::remote::RemoteKind::*;
                match r.kind {
                    Unreachable => crate::t!("err.rm.unreachable", e = r.message),
                    Busy => crate::t!("err.rm.busy"),
                    Exists => crate::t!("err.rm.exists", e = r.message),
                    NotFound => crate::t!("err.rm.notfound", e = r.message),
                    NoSpace => crate::t!("err.rm.nospace", e = r.message),
                    Denied => crate::t!("err.rm.denied", e = r.message),
                    Protocol => crate::t!("err.rm.protocol", e = r.message),
                    Other => crate::t!("err.rm.other", e = r.message),
                }
            }
        }
    }
    pub fn is_cancelled(&self) -> bool {
        matches!(self, Error::Cancelled)
    }
}

#[macro_export]
macro_rules! format_err {
    ($($arg:tt)*) => { $crate::error::Error::Format(format!($($arg)*)) };
}
