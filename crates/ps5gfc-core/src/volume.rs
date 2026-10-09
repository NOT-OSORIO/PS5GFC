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
use std::sync::Arc;
use std::time::UNIX_EPOCH;

use serde::Serialize;

use crate::ctl::Ctx;
use crate::io::{FileReader, ReadAt};
use crate::util::{is_ignored_name, long_path};
use crate::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum VolumeKind {
    Folder,
    Exfat,
    Ufs2,
    Pfs,

    Pkg,
}

impl VolumeKind {
    pub fn label(self) -> &'static str {
        match self {
            VolumeKind::Folder => "Pasta",
            VolumeKind::Exfat => "exFAT",
            VolumeKind::Ufs2 => "UFS2",
            VolumeKind::Pfs => "PFS",
            VolumeKind::Pkg => "PKG",
        }
    }
}

#[derive(Debug, Clone, Serialize)]
pub struct Entry {
    pub path: String,
    pub is_dir: bool,
    pub size: u64,

    pub mtime: Option<i64>,
}

impl Entry {
    pub fn name(&self) -> &str {
        self.path.rsplit('/').next().unwrap_or(&self.path)
    }
    pub fn parent(&self) -> &str {
        match self.path.rfind('/') {
            Some(i) => &self.path[..i],
            None => "",
        }
    }
}

pub trait Volume: Send + Sync {
    fn kind(&self) -> VolumeKind;

    fn entries(&self) -> &[Entry];

    fn open(&self, index: usize) -> Result<Arc<dyn ReadAt>>;

    fn label(&self) -> String {
        String::new()
    }

    fn describe(&self) -> Vec<(String, String)> {
        Vec::new()
    }

    fn find(&self, path: &str) -> Option<usize> {
        let want = path.trim_matches('/').to_ascii_lowercase();
        self.entries()
            .iter()
            .position(|e| !e.is_dir && e.path.to_ascii_lowercase() == want)
    }

    fn read_path(&self, path: &str) -> Result<Option<Vec<u8>>> {
        let Some(i) = self.find(path) else { return Ok(None) };
        let size = self.entries()[i].size;
        if size > 64 * 1024 * 1024 {
            return Err(Error::invalid(crate::t!("err.too_big_mem", path = path)));
        }
        let r = self.open(i)?;
        Ok(Some(r.read_vec(0, size as usize)?))
    }
}

pub fn totals(v: &dyn Volume) -> (u64, u64, u64) {
    let (mut bytes, mut files, mut dirs) = (0u64, 0u64, 0u64);
    for e in v.entries() {
        if e.is_dir {
            dirs += 1;
        } else {
            files += 1;
            bytes += e.size;
        }
    }
    (bytes, files, dirs)
}

pub struct FolderVolume {
    root: PathBuf,
    entries: Vec<Entry>,

    abs: Vec<PathBuf>,
}

fn mtime_secs(md: &std::fs::Metadata) -> Option<i64> {
    let m = md.modified().ok()?;
    match m.duration_since(UNIX_EPOCH) {
        Ok(d) => Some(d.as_secs() as i64),
        Err(e) => Some(-(e.duration().as_secs() as i64)),
    }
}

impl FolderVolume {
    pub fn scan(root: &Path, ctx: &Ctx) -> Result<Self> {
        let rootp = long_path(root);
        let md = std::fs::metadata(&rootp)?;
        if !md.is_dir() {
            return Err(Error::invalid(crate::t!("err.not_dir", path = root.display())));
        }
        let mut entries: Vec<Entry> = Vec::new();
        let mut abs: Vec<PathBuf> = Vec::new();
        let mut stack: Vec<(PathBuf, String)> = vec![(rootp.clone(), String::new())];
        let mut counter = 0u64;
        while let Some((dir, rel)) = stack.pop() {
            ctx.cancel.check()?;
            for item in std::fs::read_dir(&dir)? {
                let item = item?;
                let name_os = item.file_name();
                let Some(name) = name_os.to_str() else {
                    return Err(Error::invalid(crate::t!("err.bad_chars", path = dir.display())));
                };
                if is_ignored_name(name) {
                    continue;
                }
                let ft = item.file_type()?;
                if ft.is_symlink() {
                    continue;
                }
                let path_rel = if rel.is_empty() {
                    name.to_string()
                } else {
                    format!("{rel}/{name}")
                };
                let md = item.metadata()?;
                if ft.is_dir() {
                    entries.push(Entry {
                        path: path_rel.clone(),
                        is_dir: true,
                        size: 0,
                        mtime: mtime_secs(&md),
                    });
                    abs.push(item.path());
                    stack.push((item.path(), path_rel));
                } else if ft.is_file() {
                    entries.push(Entry {
                        path: path_rel,
                        is_dir: false,
                        size: md.len(),
                        mtime: mtime_secs(&md),
                    });
                    abs.push(item.path());
                }
                counter += 1;
                ctx.progress.add_files_done(1);
                if counter % 2048 == 0 {
                    ctx.progress.set_current(&dir.to_string_lossy());
                }
            }
        }

        let mut order: Vec<usize> = (0..entries.len()).collect();
        order.sort_by(|&a, &b| entries[a].path.cmp(&entries[b].path));
        let entries_sorted: Vec<Entry> = order.iter().map(|&i| entries[i].clone()).collect();
        let abs_sorted: Vec<PathBuf> = order.iter().map(|&i| abs[i].clone()).collect();
        Ok(Self {
            root: root.to_path_buf(),
            entries: entries_sorted,
            abs: abs_sorted,
        })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }
}

impl Volume for FolderVolume {
    fn kind(&self) -> VolumeKind {
        VolumeKind::Folder
    }
    fn entries(&self) -> &[Entry] {
        &self.entries
    }
    fn open(&self, index: usize) -> Result<Arc<dyn ReadAt>> {
        let e = self
            .entries
            .get(index)
            .ok_or_else(|| Error::invalid(crate::t!("err.bad_index")))?;
        if e.is_dir {
            return Err(Error::invalid(crate::t!("err.is_dir", path = e.path)));
        }
        Ok(Arc::new(FileReader::open_with_len(&self.abs[index], e.size)?))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scans_and_sorts() {
        let d = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(d.path().join("sce_sys")).unwrap();
        std::fs::write(d.path().join("eboot.bin"), b"abc").unwrap();
        std::fs::write(d.path().join("sce_sys/param.json"), b"{}").unwrap();
        std::fs::write(d.path().join("Thumbs.db"), b"x").unwrap();
        let v = FolderVolume::scan(d.path(), &Ctx::new()).unwrap();
        let paths: Vec<&str> = v.entries().iter().map(|e| e.path.as_str()).collect();
        assert_eq!(paths, vec!["eboot.bin", "sce_sys", "sce_sys/param.json"]);
        let i = v.find("SCE_SYS/Param.JSON").unwrap();
        assert_eq!(v.open(i).unwrap().read_vec(0, 10).unwrap(), b"{}");
        assert_eq!(totals(&v), (5, 2, 1));
    }
}
