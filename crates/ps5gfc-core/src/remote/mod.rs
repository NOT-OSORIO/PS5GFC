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

pub mod client;
pub mod download;
pub mod engine;
pub mod ftp;
pub mod http;
#[cfg(test)]
pub(crate) mod mock;
#[cfg(test)]
pub(crate) mod mock_ftp;
pub mod path;
pub mod prospero;
pub mod stream;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod tests_ftp;
pub mod upload;

use std::fmt;

use serde::Serialize;

pub use client::{Client, Protocol};
pub use ftp::Ftp;
pub use http::Endpoint;
pub use prospero::Prospero;

pub const DEFAULT_PORT: u16 = 7070;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum RemoteKind {
    Unreachable,

    Busy,

    Exists,
    NotFound,
    NoSpace,

    Denied,

    Protocol,
    Other,
}

#[derive(Debug, Clone)]
pub struct RemoteError {
    pub kind: RemoteKind,

    pub status: u16,

    pub message: String,
}

impl RemoteError {
    pub fn new(kind: RemoteKind, status: u16, message: impl Into<String>) -> Self {
        Self {
            kind,
            status,
            message: message.into(),
        }
    }

    pub fn from_response(status: u16, message: &str) -> Self {
        let m = message.to_ascii_lowercase();

        let kind = if m.contains("in progress")
            || m.contains("is busy")
            || m.contains("already in use")
            || m.contains("overlaps")
        {
            RemoteKind::Busy
        } else if m.contains("checkpoint") || m.contains("offset does not match") {
            RemoteKind::Other
        } else if m.contains("already exists") || m.contains("destination exists") {
            RemoteKind::Exists
        } else if status == 404 || m.contains("does not exist") || m.contains("no such file") {
            RemoteKind::NotFound
        } else if m.contains("refusing")
            || m.contains("protected")
            || m.contains("permission")
            || m.contains("not permitted")
        {
            RemoteKind::Denied
        } else if status == 507
            || m.contains("not enough space")
            || m.contains("no space")
            || m.contains("insufficient")
        {
            RemoteKind::NoSpace
        } else {
            RemoteKind::Other
        };
        let message = if message.is_empty() {
            format!("HTTP {status}")
        } else {
            message.to_string()
        };
        Self { kind, status, message }
    }

    pub fn is_transient(&self) -> bool {
        matches!(self.kind, RemoteKind::Busy | RemoteKind::Unreachable)
    }
}

impl fmt::Display for RemoteError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for RemoteError {}

pub(crate) const CANCEL_MARK: &str = "cancelled";

pub(crate) fn cancel_io() -> std::io::Error {
    std::io::Error::new(std::io::ErrorKind::Interrupted, CANCEL_MARK)
}
