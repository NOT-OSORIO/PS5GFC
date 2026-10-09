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

use std::path::{Path, PathBuf};

pub const KIB: u64 = 1024;
pub const MIB: u64 = 1024 * KIB;
pub const GIB: u64 = 1024 * MIB;

#[inline]
pub const fn ceil_div(a: u64, b: u64) -> u64 {
    a.div_ceil(b)
}

#[inline]
pub const fn align_up(v: u64, align: u64) -> u64 {
    ceil_div(v, align) * align
}

pub fn is_ignored_name(name: &str) -> bool {
    const MACOS: &[&str] = &[
        "__macosx",
        ".apdisk",
        ".documentrevisions-v100",
        ".ds_store",
        ".fseventsd",
        ".spotlight-v100",
        ".temporaryitems",
        ".trashes",
        ".volumeicon.icns",
    ];
    const WINDOWS: &[&str] = &[
        "$recycle.bin",
        "desktop.ini",
        "ehthumbs.db",
        "system volume information",
        "thumbs.db",
    ];
    if name.starts_with("._") {
        return true;
    }
    let lower = name.to_ascii_lowercase();
    MACOS.contains(&lower.as_str()) || WINDOWS.contains(&lower.as_str())
}

pub fn human_bytes(n: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KiB", "MiB", "GiB", "TiB"];
    let mut v = n as f64;
    let mut i = 0;
    while v >= 1024.0 && i < UNITS.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    if i == 0 {
        format!("{n} B")
    } else {
        format!("{v:.2} {}", UNITS[i])
    }
}

pub fn long_path(p: &Path) -> PathBuf {
    #[cfg(windows)]
    {
        let s = p.as_os_str().to_string_lossy();
        if s.starts_with(r"\\?\") {
            return p.to_path_buf();
        }
        let abs = if p.is_absolute() {
            p.to_path_buf()
        } else {
            match std::env::current_dir() {
                Ok(cwd) => cwd.join(p),
                Err(_) => p.to_path_buf(),
            }
        };
        let s = abs.to_string_lossy().replace('/', "\\");
        if let Some(rest) = s.strip_prefix(r"\\") {
            return PathBuf::from(format!(r"\\?\UNC\{rest}"));
        }
        PathBuf::from(format!(r"\\?\{s}"))
    }
    #[cfg(not(windows))]
    {
        p.to_path_buf()
    }
}

pub fn join_rel(base: &Path, rel: &str) -> crate::Result<PathBuf> {
    let mut out = base.to_path_buf();
    for comp in rel.split('/') {
        if comp.is_empty() || comp == "." {
            continue;
        }
        if comp == ".." || comp.contains('\\') || comp.contains(':') {
            return Err(crate::Error::invalid(crate::t!("err.unsafe_path", path = rel)));
        }
        out.push(comp);
    }
    Ok(out)
}

pub fn default_threads() -> usize {
    std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(2)
        .saturating_sub(1)
        .max(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ignored_names() {
        assert!(is_ignored_name("Thumbs.db"));
        assert!(is_ignored_name(".DS_Store"));
        assert!(is_ignored_name("._foo"));
        assert!(!is_ignored_name("eboot.bin"));
    }

    #[test]
    fn align_math() {
        assert_eq!(align_up(1, 65536), 65536);
        assert_eq!(align_up(65536, 65536), 65536);
        assert_eq!(ceil_div(0, 4), 0);
    }

    #[test]
    fn rel_join_rejects_escape() {
        assert!(join_rel(Path::new("x"), "a/../b").is_err());
        assert!(join_rel(Path::new("x"), "a/b").is_ok());
    }
}
