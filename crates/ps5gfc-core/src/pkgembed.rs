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

use std::path::PathBuf;

#[cfg(embedded_builder)]
mod data {
    include!(concat!(env!("OUT_DIR"), "/embedded_builder.rs"));
}

#[cfg(embedded_builder)]
pub(crate) fn extract() -> Option<PathBuf> {
    use std::sync::OnceLock;
    static PATH: OnceLock<Option<PathBuf>> = OnceLock::new();
    PATH.get_or_init(|| materialize(data::BUILDER)).clone()
}

#[cfg(not(embedded_builder))]
pub(crate) fn extract() -> Option<PathBuf> {
    None
}

#[cfg(embedded_builder)]
fn materialize(bytes: &'static [u8]) -> Option<PathBuf> {
    if bytes.is_empty() {
        return None;
    }
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join("PS5GFC")
        .join("builder");
    let tag = format!("{:x}-{:08x}", bytes.len(), crc32fast::hash(bytes));
    let dir = base.join(&tag);
    let exe = dir.join("ps5pkg-builder.exe");
    if exe.metadata().map(|m| m.len() == bytes.len() as u64).unwrap_or(false) {
        return Some(exe);
    }
    std::fs::create_dir_all(&dir).ok()?;
    let part = dir.join(format!("ps5pkg-builder.{}.part", std::process::id()));
    std::fs::write(&part, bytes).ok()?;
    if std::fs::rename(&part, &exe).is_err() {
        let _ = std::fs::remove_file(&part);
        if !exe.is_file() {
            return None;
        }
    }
    if let Ok(entries) = std::fs::read_dir(&base) {
        for e in entries.flatten() {
            if e.file_name().to_string_lossy() != tag {
                let _ = std::fs::remove_dir_all(e.path());
            }
        }
    }
    Some(exe)
}
