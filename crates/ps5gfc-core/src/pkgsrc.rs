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

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::convert::{find_pkg_builder, parse_pkg_line, run_builder, PkgMsg, PkgProgress};
use crate::ctl::{Ctx, Stage};
use crate::io::{FileReader, ReadAt};
use crate::source::{Format, OpenedSource};
use crate::util::{human_bytes, long_path, GIB};
use crate::volume::{Entry, Volume, VolumeKind};
use crate::{Error, Result};

pub struct PkgVolume {
    entries: Vec<Entry>,

    meta: tempfile::TempDir,
}

impl Volume for PkgVolume {
    fn kind(&self) -> VolumeKind {
        VolumeKind::Pkg
    }

    fn entries(&self) -> &[Entry] {
        &self.entries
    }

    fn open(&self, index: usize) -> Result<Arc<dyn ReadAt>> {
        let e = self
            .entries
            .get(index)
            .ok_or_else(|| Error::invalid(crate::t!("err.bad_index")))?;
        if !e.is_dir {
            let p = self.meta.path().join(&e.path);
            if let Ok(md) = std::fs::metadata(long_path(&p)) {
                return Ok(Arc::new(FileReader::open_with_len(&p, md.len())?));
            }
        }
        Err(Error::unsupported(crate::t!("err.pkgsrc_not_staged", path = e.path)))
    }
}

fn entries_from(files: &[(String, u64)]) -> Vec<Entry> {
    let mut dirs: BTreeSet<String> = BTreeSet::new();
    for (path, _) in files {
        let mut cur = path.as_str();
        while let Some(i) = cur.rfind('/') {
            cur = &cur[..i];
            if !dirs.insert(cur.to_string()) {
                break;
            }
        }
    }
    let mut sorted: Vec<&(String, u64)> = files.iter().collect();
    sorted.sort_by(|a, b| a.0.cmp(&b.0));
    let mut out: Vec<Entry> = dirs
        .into_iter()
        .map(|path| Entry {
            path,
            is_dir: true,
            size: 0,
            mtime: None,
        })
        .collect();
    out.extend(sorted.into_iter().map(|(path, size)| Entry {
        path: path.clone(),
        is_dir: false,
        size: *size,
        mtime: None,
    }));
    out
}

pub fn open(path: &Path, ctx: &Ctx) -> Result<OpenedSource> {
    let builder = find_pkg_builder()?;
    ctx.progress.info(crate::tl!("log.pkgsrc.list"));
    let meta = tempfile::Builder::new().prefix("ps5gfc-pkgmeta-").tempdir()?;
    let mut cmd = builder.command();
    cmd.arg("--list").arg(path).arg("--meta-dir").arg(meta.path());
    let mut files: Vec<(String, u64)> = Vec::new();
    run_builder(
        cmd,
        crate::sys::worker_priority(),
        None,
        ctx,
        |line| match parse_pkg_line(line) {
            Some(PkgMsg::Entry { path, size }) => files.push((path, size)),
            Some(PkgMsg::Log(m)) | Some(PkgMsg::Text(m)) => ctx.progress.info(m.trim().to_string()),
            _ => {}
        },
    )?;
    if files.is_empty() {
        return Err(Error::invalid(crate::t!("err.pkgsrc_empty")));
    }
    let volume = PkgVolume {
        entries: entries_from(&files),
        meta,
    };
    Ok(OpenedSource {
        path: path.to_path_buf(),
        format: Format::Pkg,
        volume: Arc::new(volume),
        fs_image: None,
        wrapper: None,
    })
}

pub struct StagedTree {
    dir: PathBuf,
}

impl StagedTree {
    pub fn tree(&self) -> PathBuf {
        self.dir.join("tree")
    }
}

impl Drop for StagedTree {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(long_path(&self.dir));
    }
}

pub fn stage(pkg: &Path, parent: &Path, total: u64, ctx: &Ctx) -> Result<StagedTree> {
    std::fs::create_dir_all(long_path(parent))?;
    if let Some(free) = crate::sys::free_space(parent) {
        let need = total + GIB;
        if free < need {
            return Err(Error::invalid(crate::t!(
                "err.pkgsrc_space",
                path = parent.display(),
                free = human_bytes(free),
                need = human_bytes(need)
            )));
        }
    }
    let builder = find_pkg_builder()?;
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let staged = StagedTree {
        dir: parent.join(format!(".ps5gfc-pkgsrc-{}-{nanos:x}", std::process::id())),
    };
    ctx.progress.info(crate::tl!("log.pkgsrc.extract"));
    ctx.progress.begin_phase(Stage::Processing, total);
    let mut cmd = builder.command();
    cmd.arg("--extract").arg(pkg).arg("--output").arg(staged.tree());
    let mut prog = PkgProgress::new(total);
    run_builder(
        cmd,
        crate::sys::worker_priority(),
        None,
        ctx,
        |line| match parse_pkg_line(line) {
            Some(PkgMsg::Progress {
                stage,
                done,
                total,
                current,
            }) => prog.apply(ctx, &stage, done, total, &current),
            Some(PkgMsg::Log(m)) | Some(PkgMsg::Text(m)) => ctx.progress.info(m.trim().to_string()),
            _ => {}
        },
    )?;
    Ok(staged)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn entries_have_parent_dirs_and_sorted_files() {
        let files = vec![
            ("sce_sys/param.json".to_string(), 10),
            ("eboot.bin".to_string(), 5),
            ("data/a/b.pak".to_string(), 7),
        ];
        let e = entries_from(&files);
        let dirs: Vec<&str> = e.iter().filter(|x| x.is_dir).map(|x| x.path.as_str()).collect();
        assert_eq!(dirs, ["data", "data/a", "sce_sys"]);
        let names: Vec<&str> = e.iter().filter(|x| !x.is_dir).map(|x| x.path.as_str()).collect();
        assert_eq!(names, ["data/a/b.pak", "eboot.bin", "sce_sys/param.json"]);

        for (i, x) in e.iter().enumerate() {
            if let Some(p) = x.path.rfind('/') {
                assert!(
                    e[..i].iter().any(|d| d.is_dir && d.path == x.path[..p]),
                    "pai de {} ausente",
                    x.path
                );
            }
        }
    }
}
