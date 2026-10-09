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

use std::path::Path;

use super::format::*;
use crate::ctl::{Ctx, Stage};
use crate::emit::PartialFile;
use crate::io::{OutFile, ReadAt};
use crate::pfsc::{measure_pfsc, stream_pfsc, write_pfsc, PfscOptions, PfscPlan, PfscStats};
use crate::remote::upload::SeqSink;
use crate::util::ceil_div;
use crate::{Error, Result};

#[derive(Debug, Clone)]
pub struct ContainerOptions {
    pub pfsc: PfscOptions,
    pub block_size: u32,
    pub version: i64,
    pub case_insensitive: bool,

    pub timestamp: i64,
}

impl Default for ContainerOptions {
    fn default() -> Self {
        Self {
            pfsc: PfscOptions::default(),
            block_size: DEFAULT_BLOCK,
            version: VERSION_PS5,
            case_insensitive: true,
            timestamp: std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0),
        }
    }
}

#[derive(Debug, Clone)]
pub struct ContainerStats {
    pub pfsc: PfscStats,
    pub file_len: u64,
    pub inner_len: u64,
}

pub fn safe_inner_name(source_name: &str) -> String {
    let (stem, ext) = match source_name.rfind('.') {
        Some(i) if i > 0 => (&source_name[..i], source_name[i..].to_ascii_lowercase()),
        _ => (source_name, String::new()),
    };
    if let Some(id) = find_title_id(stem) {
        return format!("{id}{ext}");
    }
    let mut out = String::new();
    let mut pending: Option<char> = None;
    for ch in stem.chars() {
        if ch.is_ascii_alphanumeric() {
            if let Some(p) = pending.take() {
                if !out.is_empty() {
                    out.push(p);
                }
            }
            out.push(ch);
        } else if ch == ' ' {
            pending = Some('_');
        } else if ch == '_' || ch == '-' {
            pending = Some(ch);
        }
    }
    out.truncate(15);
    if out.is_empty() {
        out = "IMAGE".to_string();
    }
    format!("{out}{ext}")
}

pub fn find_title_id(s: &str) -> Option<String> {
    let b = s.as_bytes();
    let n = b.len();
    if n < 9 {
        return None;
    }
    for i in 0..=n - 9 {
        let w = &b[i..i + 9];
        let ok = w[..4].iter().all(|c| c.is_ascii_uppercase()) && w[4..].iter().all(|c| c.is_ascii_digit());
        if !ok {
            continue;
        }
        let before_ok = i == 0 || !(b[i - 1].is_ascii_uppercase() || b[i - 1].is_ascii_digit());
        let after_ok = i + 9 == n || !(b[i + 9].is_ascii_uppercase() || b[i + 9].is_ascii_digit());
        if before_ok && after_ok {
            return Some(String::from_utf8_lossy(w).into_owned());
        }
    }
    None
}

struct Layout {
    bs: u64,
    block_size: u32,
    version: i64,
    mode: u16,
    now: i64,
    inode_count: u64,
    inode_blocks: u64,
    b_superroot: u64,
    b_fpt: u64,
    b_uroot: u64,
    b_file: u64,
    super_dir: Vec<u8>,
    uroot_dir: Vec<u8>,
    fpt: Vec<u8>,
    inner_len: u64,
}

impl Layout {
    fn new(inner_len: u64, inner_name: &str, opts: &ContainerOptions) -> Result<Self> {
        let bs = opts.block_size as u64;
        if !opts.block_size.is_power_of_two() || bs < 16384 {
            return Err(Error::invalid("PFS block size must be a power of 2 and >= 16 KiB"));
        }
        if inner_name.is_empty() || !inner_name.is_ascii() || inner_name.contains('/') {
            return Err(Error::invalid(format!("invalid inner name for PFS: {inner_name:?}")));
        }

        let inode_count = 4u64;
        let per_block = bs / INODE_D32_SIZE as u64;
        let inode_blocks = ceil_div(inode_count, per_block);
        let b_superroot = 1 + inode_blocks;
        let b_fpt = b_superroot + 1;
        let b_reserved = b_fpt + 1;
        let b_uroot = b_reserved + 1;
        let b_file = b_uroot + 1;

        let mut super_dir = Vec::new();
        push_dirent(&mut super_dir, 1, DIRENT_FILE, b"flat_path_table");
        push_dirent(&mut super_dir, 2, DIRENT_DIR, b"uroot");
        let mut uroot_dir = Vec::new();
        push_dirent(&mut uroot_dir, 2, DIRENT_DOT, b".");
        push_dirent(&mut uroot_dir, 2, DIRENT_DOTDOT, b"..");
        push_dirent(&mut uroot_dir, 3, DIRENT_FILE, inner_name.as_bytes());
        let mut fpt = Vec::new();
        fpt.extend_from_slice(&fpt_hash(&format!("/{inner_name}"), opts.case_insensitive).to_le_bytes());
        fpt.extend_from_slice(&3u32.to_le_bytes());
        if super_dir.len() as u64 > bs || uroot_dir.len() as u64 > bs {
            return Err(Error::invalid("inner name too long"));
        }

        Ok(Self {
            bs,
            block_size: opts.block_size,
            version: opts.version,
            mode: if opts.case_insensitive {
                MODE_CASE_INSENSITIVE
            } else {
                0
            },
            now: opts.timestamp,
            inode_count,
            inode_blocks,
            b_superroot,
            b_fpt,
            b_uroot,
            b_file,
            super_dir,
            uroot_dir,
            fpt,
            inner_len,
        })
    }

    fn inodes(&self, stored_len: Option<u64>) -> [Inode; 4] {
        let (bs, now) = (self.bs, self.now);
        let ro = FLAG_READONLY;
        let neg = |first: i32| {
            let mut db = [-1i32; 12];
            db[0] = first;
            db
        };
        let mut inodes = [
            Inode {
                mode: INODE_MODE_DIR | INODE_RX_ONLY,
                nlink: 1,
                flags: FLAG_INTERNAL | ro,
                size: bs as i64,
                size_compressed: bs as i64,
                blocks: 1,
                db: {
                    let mut d = [0i32; 12];
                    d[0] = self.b_superroot as i32;
                    d
                },
                time: now,
                ..Default::default()
            },
            Inode {
                mode: INODE_MODE_FILE | INODE_RX_ONLY,
                nlink: 1,
                flags: FLAG_INTERNAL | ro,
                size: self.fpt.len() as i64,
                size_compressed: self.fpt.len() as i64,
                blocks: 1,
                db: neg(self.b_fpt as i32),
                time: now,
                ..Default::default()
            },
            Inode {
                mode: INODE_MODE_DIR | INODE_RX_ONLY,
                nlink: 3,
                flags: ro,
                size: bs as i64,
                size_compressed: bs as i64,
                blocks: 1,
                db: neg(self.b_uroot as i32),
                time: now,
                ..Default::default()
            },
            Inode {
                mode: INODE_MODE_FILE | INODE_RX_ONLY,
                nlink: 1,
                flags: ro | FLAG_COMPRESSED,
                size: 0,
                size_compressed: self.inner_len as i64,
                blocks: 1,
                db: neg(self.b_file as i32),
                time: now,
                ..Default::default()
            },
        ];
        if let Some(stored) = stored_len {
            inodes[3].size = stored as i64;
            inodes[3].blocks = ceil_div(stored, bs).max(1) as u32;
        }
        inodes
    }

    fn inode_table(&self, inodes: &[Inode; 4]) -> Vec<u8> {
        let per_block = self.bs / INODE_D32_SIZE as u64;
        let mut table = vec![0u8; (self.inode_blocks * self.bs) as usize];
        for (k, ino) in inodes.iter().enumerate() {
            let blk = k as u64 / per_block;
            let within = (k as u64 % per_block) as usize * INODE_D32_SIZE;
            let at = (blk * self.bs) as usize + within;
            table[at..at + INODE_D32_SIZE].copy_from_slice(&ino.to_bytes());
        }
        table
    }

    fn header(&self, ndblock: u64) -> Vec<u8> {
        build_header(
            self.version,
            self.mode,
            self.block_size,
            self.inode_count,
            ndblock,
            self.inode_blocks,
            self.now,
        )
    }

    fn ndblock(&self, stored_len: u64) -> Result<u64> {
        let ndblock = self.b_file + ceil_div(stored_len, self.bs).max(1);
        if ndblock > i32::MAX as u64 {
            return Err(Error::invalid("image too large for D32 inodes"));
        }
        Ok(ndblock)
    }

    fn prefix(&self, stored_len: u64) -> Result<Vec<u8>> {
        let ndblock = self.ndblock(stored_len)?;
        let bs = self.bs as usize;
        let mut out = vec![0u8; self.b_file as usize * bs];
        let mut put = |at_block: u64, bytes: &[u8]| {
            let at = at_block as usize * bs;
            out[at..at + bytes.len()].copy_from_slice(bytes);
        };
        put(0, &self.header(ndblock));
        put(1, &self.inode_table(&self.inodes(Some(stored_len))));
        put(self.b_superroot, &self.super_dir);
        put(self.b_fpt, &self.fpt);
        put(self.b_uroot, &self.uroot_dir);
        Ok(out)
    }
}

pub fn write_container(
    image: &dyn ReadAt,
    inner_name: &str,
    out_path: &Path,
    opts: &ContainerOptions,
    ctx: &Ctx,
) -> Result<ContainerStats> {
    let layout = Layout::new(image.len(), inner_name, opts)?;
    let bs = layout.bs;
    let inner_len = image.len();
    ctx.progress.begin_phase(Stage::Processing, inner_len);

    let guard = PartialFile::new(out_path);
    let out = OutFile::create(out_path, 0)?;

    out.write_all_at(0, &layout.header(0))?;
    out.write_all_at(bs, &layout.inode_table(&layout.inodes(None)))?;
    out.write_all_at(layout.b_superroot * bs, &layout.super_dir)?;
    out.write_all_at(layout.b_fpt * bs, &layout.fpt)?;
    out.write_all_at(layout.b_uroot * bs, &layout.uroot_dir)?;

    let stats = write_pfsc(&out, layout.b_file * bs, image, &opts.pfsc, ctx)?;

    ctx.progress.set_stage(Stage::Finalizing);
    let ndblock = layout.ndblock(stats.stored_len)?;
    out.write_all_at(0, &layout.header(ndblock))?;
    out.write_all_at(bs, &layout.inode_table(&layout.inodes(Some(stats.stored_len))))?;
    out.set_len(ndblock * bs)?;
    out.sync()?;
    guard.keep();
    Ok(ContainerStats {
        pfsc: stats,
        file_len: ndblock * bs,
        inner_len,
    })
}

pub struct ContainerPlan {
    layout: Layout,
    pfsc: PfscPlan,
    file_len: u64,
}

impl ContainerPlan {
    pub fn file_len(&self) -> u64 {
        self.file_len
    }

    pub fn stats(&self) -> &PfscStats {
        &self.pfsc.stats
    }

    pub fn write(
        &self,
        image: &dyn ReadAt,
        opts: &ContainerOptions,
        sink: &mut dyn SeqSink,
        ctx: &Ctx,
    ) -> Result<ContainerStats> {
        let prefix = self.layout.prefix(self.pfsc.stored_len())?;
        sink.write_all(&prefix)?;
        let stats = stream_pfsc(&self.pfsc, image, &opts.pfsc, sink, ctx)?;

        ctx.progress.set_stage(Stage::Finalizing);
        let mut rest = self.file_len - prefix.len() as u64 - stats.stored_len;
        let zeros = vec![0u8; (rest.min(1 << 20)) as usize];
        while rest > 0 {
            let n = rest.min(zeros.len() as u64) as usize;
            sink.write_all(&zeros[..n])?;
            rest -= n as u64;
        }
        Ok(ContainerStats {
            pfsc: stats,
            file_len: self.file_len,
            inner_len: self.layout.inner_len,
        })
    }
}

pub fn plan_container(
    image: &dyn ReadAt,
    inner_name: &str,
    opts: &ContainerOptions,
    ctx: &Ctx,
) -> Result<ContainerPlan> {
    let layout = Layout::new(image.len(), inner_name, opts)?;
    let pfsc = measure_pfsc(image, &opts.pfsc, ctx)?;
    let file_len = layout.ndblock(pfsc.stored_len())? * layout.bs;
    Ok(ContainerPlan { layout, pfsc, file_len })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn title_id_detection() {
        assert_eq!(find_title_id("Game PPSA01411 v1"), Some("PPSA01411".into()));
        assert_eq!(find_title_id("PPSA01411"), Some("PPSA01411".into()));

        assert_eq!(find_title_id("xPPSA01411"), Some("PPSA01411".into()));
        assert_eq!(find_title_id("XPPSA01411"), None);
        assert_eq!(find_title_id("PPSA014111"), None);
        assert_eq!(find_title_id("nothing"), None);
    }

    #[test]
    fn inner_name_sanitized() {
        assert_eq!(safe_inner_name("PPSA01411.exfat"), "PPSA01411.exfat");
        assert_eq!(
            safe_inner_name("My Cool Game (v1.2).EXFAT"),
            "My_Cool_Game_v1".to_string() + ".exfat"
        );
        assert_eq!(safe_inner_name("日本語.exfat"), "IMAGE.exfat");
    }
}
