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

use std::collections::HashSet;
use std::sync::Arc;

use super::format::*;
use crate::io::{ReadAt, SubReader};
use crate::pfsc::PfscView;
use crate::volume::{Entry, Volume, VolumeKind};
use crate::{format_err, Error, Result};

pub struct PfsImage {
    img: Arc<dyn ReadAt>,
    header: Header,
    inodes: Vec<Inode>,
    entries: Vec<Entry>,

    ino_of: Vec<u32>,
}

impl PfsImage {
    pub fn open(img: Arc<dyn ReadAt>) -> Result<Self> {
        Self::open_with(img, None)
    }

    pub fn open_with(img: Arc<dyn ReadAt>, ctx: Option<&crate::ctl::Ctx>) -> Result<Self> {
        let mut head = vec![0u8; 0x400];
        img.read_exact_at(0, &mut head)
            .map_err(|_| format_err!("file too small for a PFS header"))?;
        let header = parse_header(&head)?;
        if header.mode & (MODE_SIGNED | MODE_ENCRYPTED | MODE_64BIT_INODES) != 0 {
            return Err(Error::unsupported(
                "signed, encrypted or 64-bit-inode PFS (PKG image) is not supported; \
                 only unsigned images like those made for ShadowMountPlus",
            ));
        }
        let bs = header.block_size as u64;
        if img.len() < header.ndblock.saturating_mul(bs).min(bs * 2) {
            return Err(format_err!("truncated PFS image"));
        }

        let per_block = bs / INODE_D32_SIZE as u64;
        let mut inodes = Vec::with_capacity(header.inode_count as usize);
        let mut blk = vec![0u8; bs as usize];
        'outer: for b in 0..header.inode_block_count {
            img.read_exact_at(bs * (1 + b), &mut blk)
                .map_err(|e| format_err!("unreadable inode table: {e}"))?;
            for k in 0..per_block {
                if inodes.len() as u64 >= header.inode_count {
                    break 'outer;
                }
                let at = k as usize * INODE_D32_SIZE;
                inodes.push(Inode::parse(&blk[at..at + INODE_D32_SIZE])?);
            }
        }
        if (inodes.len() as u64) < header.inode_count {
            return Err(format_err!("incomplete PFS inode table"));
        }

        let mut me = PfsImage {
            img,
            header,
            inodes,
            entries: Vec::new(),
            ino_of: Vec::new(),
        };
        if let Some(c) = ctx {
            c.progress.set_files_total(me.header.inode_count);
        }
        me.walk(ctx)?;
        Ok(me)
    }

    pub fn header(&self) -> &Header {
        &self.header
    }
    pub fn inode(&self, ino: u32) -> Option<&Inode> {
        self.inodes.get(ino as usize)
    }
    pub fn raw(&self) -> &Arc<dyn ReadAt> {
        &self.img
    }

    fn dir_blob(&self, ino: &Inode) -> Result<Vec<u8>> {
        let bs = self.header.block_size as u64;
        let len = ino.stored_size().max(ino.blocks as u64 * bs).min(64 * 1024 * 1024);
        let start = ino.db[0] as i64;
        if start < 0 {
            return Err(format_err!("directory without blocks"));
        }
        let avail = self.img.len().saturating_sub(start as u64 * bs);
        let n = len.min(avail);
        Ok(self.img.read_vec(start as u64 * bs, n as usize)?)
    }

    fn walk(&mut self, ctx: Option<&crate::ctl::Ctx>) -> Result<()> {
        let bs = self.header.block_size as u64;

        let sr_off = (1 + self.header.inode_block_count) * bs;
        let sr_blob = self.img.read_vec(sr_off, bs as usize)?;
        let sr = parse_dirents(&sr_blob)?;
        let uroot = sr
            .iter()
            .find(|d| d.name == b"uroot")
            .ok_or_else(|| format_err!("superroot without 'uroot'"))?
            .ino;

        let mut entries: Vec<Entry> = Vec::new();
        let mut ino_of: Vec<u32> = Vec::new();
        let mut stack: Vec<(u32, String)> = vec![(uroot, String::new())];
        let mut visited: HashSet<u32> = HashSet::new();
        while let Some((dino, prefix)) = stack.pop() {
            if let Some(c) = ctx {
                c.cancel.check()?;
            }
            if !visited.insert(dino) {
                return Err(format_err!("loop in the PFS directory tree"));
            }
            let dir = self
                .inodes
                .get(dino as usize)
                .ok_or_else(|| format_err!("directory inode {dino} does not exist"))?;
            let blob = self.dir_blob(dir)?;
            for d in parse_dirents(&blob)? {
                if d.typ == DIRENT_DOT || d.typ == DIRENT_DOTDOT {
                    continue;
                }
                let name = String::from_utf8_lossy(&d.name).into_owned();
                if name.is_empty() || name == "." || name == ".." || name.contains('/') {
                    return Err(format_err!("invalid PFS entry name: {name:?}"));
                }
                let path = if prefix.is_empty() {
                    name
                } else {
                    format!("{prefix}/{name}")
                };
                if let Some(c) = ctx {
                    c.progress.add_files_done(1);
                }
                let ino = self
                    .inodes
                    .get(d.ino as usize)
                    .ok_or_else(|| format_err!("inode {} does not exist", d.ino))?;
                if d.typ == DIRENT_DIR {
                    entries.push(Entry {
                        path: path.clone(),
                        is_dir: true,
                        size: 0,
                        mtime: Some(ino.time),
                    });
                    ino_of.push(d.ino);
                    stack.push((d.ino, path));
                } else {
                    entries.push(Entry {
                        path,
                        is_dir: false,
                        size: ino.logical_size(),
                        mtime: Some(ino.time),
                    });
                    ino_of.push(d.ino);
                }
            }
        }
        let mut order: Vec<usize> = (0..entries.len()).collect();
        order.sort_by(|&a, &b| entries[a].path.cmp(&entries[b].path));
        self.entries = order.iter().map(|&i| entries[i].clone()).collect();
        self.ino_of = order.iter().map(|&i| ino_of[i]).collect();
        Ok(())
    }

    pub fn file_reader(&self, ino: u32) -> Result<Arc<dyn ReadAt>> {
        let ino = self
            .inodes
            .get(ino as usize)
            .ok_or_else(|| Error::invalid("inode does not exist"))?;
        let bs = self.header.block_size as u64;
        if ino.db[0] < 0 {
            return Err(format_err!("file without blocks"));
        }
        if ino.blocks > 1 && ino.db[1] >= 0 {
            return Err(Error::unsupported("PFS file with non-contiguous blocks"));
        }
        let start = ino.db[0] as u64 * bs;
        let stored = ino.stored_size();
        let raw = SubReader::new(self.img.clone(), start, stored);
        if ino.is_compressed() {
            let view = PfscView::open(Arc::new(raw))?;
            Ok(Arc::new(SubReader::new(Arc::new(view), 0, ino.logical_size())))
        } else {
            Ok(Arc::new(SubReader::new(Arc::new(raw), 0, ino.logical_size())))
        }
    }

    pub fn single_file(&self) -> Option<usize> {
        let mut it = self.entries.iter().enumerate().filter(|(_, e)| !e.is_dir);
        let first = it.next()?;
        if it.next().is_some() || self.entries.iter().any(|e| e.is_dir) {
            return None;
        }
        Some(first.0)
    }
}

impl Volume for PfsImage {
    fn kind(&self) -> VolumeKind {
        VolumeKind::Pfs
    }
    fn describe(&self) -> Vec<(String, String)> {
        let h = &self.header;
        vec![
            (
                "pfs_version".into(),
                (if h.version == VERSION_PS5 { "2 (PS5)" } else { "1 (PS4)" }).into(),
            ),
            ("block".into(), format!("{} KiB", h.block_size / 1024)),
            ("inodes".into(), h.inode_count.to_string()),
            (
                "case".into(),
                (if h.mode & MODE_CASE_INSENSITIVE != 0 {
                    "insensitive"
                } else {
                    "sensitive"
                })
                .into(),
            ),
        ]
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
        self.file_reader(self.ino_of[index])
    }
}
