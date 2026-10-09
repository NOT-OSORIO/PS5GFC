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
use std::io;
use std::sync::Arc;

use super::layout::*;
use crate::io::ReadAt;
use crate::volume::{Entry, Volume, VolumeKind};
use crate::{format_err, Error, Result};

pub fn is_ufs2(r: &dyn ReadAt) -> bool {
    let mut m = [0u8; 4];
    r.read_at(SBLOCK_OFFSET + 0x55C, &mut m).unwrap_or(0) == 4 && u32::from_le_bytes(m) == FS_UFS2_MAGIC
}

#[derive(Debug, Clone, Copy)]
struct Run {
    lbn: u64,

    frag: u64,
    nblocks: u64,
}

pub struct Ufs2Volume {
    img: Arc<dyn ReadAt>,
    bsize: u64,
    fsize: u64,
    frag: u64,
    ipg: u64,
    fpg: u64,
    iblkno: u64,
    inopb: u64,
    nindir: u64,
    ncg: u64,
    size_frags: u64,
    volname: String,
    entries: Vec<Entry>,
    inos: Vec<u32>,
}

impl Ufs2Volume {
    pub fn open(img: Arc<dyn ReadAt>) -> Result<Self> {
        Self::open_with(img, None)
    }

    pub fn open_with(img: Arc<dyn ReadAt>, ctx: Option<&crate::ctl::Ctx>) -> Result<Self> {
        let mut sb = vec![0u8; SBLOCKSIZE];
        img.read_exact_at(SBLOCK_OFFSET, &mut sb)
            .map_err(|_| format_err!("image too small for a UFS2 superblock"))?;
        let u32at = |o: usize| u32::from_le_bytes(sb[o..o + 4].try_into().unwrap());
        let i32at = |o: usize| i32::from_le_bytes(sb[o..o + 4].try_into().unwrap());
        let i64at = |o: usize| i64::from_le_bytes(sb[o..o + 8].try_into().unwrap());
        if u32at(0x55C) != FS_UFS2_MAGIC {
            return Err(format_err!("UFS2 magic missing"));
        }
        let bsize = i32at(0x30);
        let fsize = i32at(0x34);
        let frag = i32at(0x38);
        let ipg = i32at(0xB8);
        let fpg = i32at(0xBC);
        let ncg = i32at(0x2C);
        let iblkno = i32at(0x10);
        let inopb = i32at(0x78);
        let size_frags = i64at(0x438);
        if bsize < 4096 || bsize > 65536 || !(bsize as u32).is_power_of_two() {
            return Err(format_err!("invalid UFS2 block size: {bsize}"));
        }
        if fsize < 512 || !(fsize as u32).is_power_of_two() || frag < 1 || frag > 8 || fsize * frag != bsize {
            return Err(format_err!("invalid UFS2 fragment size: {fsize} (frag {frag})"));
        }
        if ipg <= 0 || fpg <= 0 || ncg <= 0 || iblkno <= 0 || inopb <= 0 || size_frags <= 0 {
            return Err(format_err!("inconsistent UFS2 superblock"));
        }
        if inopb as i64 * ISIZE as i64 != bsize as i64 {
            return Err(Error::unsupported("only 256-byte UFS2 inodes are supported"));
        }
        let vn = &sb[0x2A8..0x2A8 + 32];
        let volname = String::from_utf8_lossy(&vn[..vn.iter().position(|&c| c == 0).unwrap_or(32)]).into_owned();

        let mut v = Ufs2Volume {
            img,
            bsize: bsize as u64,
            fsize: fsize as u64,
            frag: frag as u64,
            ipg: ipg as u64,
            fpg: fpg as u64,
            iblkno: iblkno as u64,
            inopb: inopb as u64,
            nindir: bsize as u64 / 8,
            ncg: ncg as u64,
            size_frags: size_frags as u64,
            volname,
            entries: Vec::new(),
            inos: Vec::new(),
        };
        v.walk(ctx)?;
        Ok(v)
    }

    fn read_inode(&self, ino: u32) -> Result<Dinode> {
        let c = ino as u64 / self.ipg;
        if c >= self.ncg {
            return Err(format_err!("inode {ino} outside the volume"));
        }
        let idx = ino as u64 % self.ipg;
        let frag_addr = c * self.fpg + self.iblkno + (idx / self.inopb) * self.frag;
        let off = frag_addr * self.fsize + (idx % self.inopb) * ISIZE as u64;
        let mut b = [0u8; ISIZE as usize];
        self.img
            .read_exact_at(off, &mut b)
            .map_err(|e| format_err!("unreadable inode {ino}: {e}"))?;
        Ok(Dinode::parse(&b))
    }

    fn block_map(&self, di: &Dinode) -> Result<Vec<Run>> {
        let nblocks = (di.size.max(0) as u64).div_ceil(self.bsize);
        let mut runs: Vec<Run> = Vec::new();
        let mut push = |lbn: u64, p: u64| {
            let frag = if p == 0 { u64::MAX } else { p };
            if let Some(last) = runs.last_mut() {
                let contiguous = if frag == u64::MAX {
                    last.frag == u64::MAX
                } else {
                    last.frag != u64::MAX && last.frag + last.nblocks * self.frag == frag
                };
                if contiguous && last.lbn + last.nblocks == lbn {
                    last.nblocks += 1;
                    return;
                }
            }
            runs.push(Run { lbn, frag, nblocks: 1 });
        };
        let mut lbn = 0u64;
        for i in 0..NDADDR {
            if lbn >= nblocks {
                return Ok(runs);
            }
            push(lbn, di.db[i] as u64);
            lbn += 1;
        }

        for level in 1..=NIADDR {
            if lbn >= nblocks {
                break;
            }
            let span = self.nindir.pow(level as u32);
            let end = nblocks.min(lbn + span);
            self.walk_indirect(di.ib[level - 1] as u64, level, &mut lbn, end, &mut push)?;
        }
        Ok(runs)
    }

    fn walk_indirect(
        &self,
        blk: u64,
        level: usize,
        lbn: &mut u64,
        end: u64,
        push: &mut impl FnMut(u64, u64),
    ) -> Result<()> {
        let per_child = self.nindir.pow(level as u32 - 1);
        if blk == 0 {
            while *lbn < end {
                push(*lbn, 0);
                *lbn += 1;
            }
            return Ok(());
        }
        if blk >= self.size_frags {
            return Err(format_err!("indirect pointer outside the volume: {blk}"));
        }
        let mut raw = vec![0u8; self.bsize as usize];
        self.img
            .read_exact_at(blk * self.fsize, &mut raw)
            .map_err(|e| format_err!("unreadable indirect block: {e}"))?;
        let ptrs: Vec<u64> = raw
            .chunks_exact(8)
            .map(|c| u64::from_le_bytes(c.try_into().unwrap()))
            .collect();
        for p in ptrs {
            if *lbn >= end {
                break;
            }
            if level == 1 {
                push(*lbn, p);
                *lbn += 1;
            } else {
                let child_end = end.min(*lbn + per_child);
                self.walk_indirect(p, level - 1, lbn, child_end, push)?;
            }
        }
        Ok(())
    }

    fn read_all(&self, di: &Dinode, cap: u64) -> Result<Vec<u8>> {
        let size = (di.size.max(0) as u64).min(cap);
        let runs = self.block_map(di)?;
        let f = UfsFile {
            img: self.img.clone(),
            runs,
            size,
            bsize: self.bsize,
            fsize: self.fsize,
            frag: self.frag,
        };
        Ok(f.read_vec(0, size as usize)?)
    }

    fn walk(&mut self, ctx: Option<&crate::ctl::Ctx>) -> Result<()> {
        let mut entries: Vec<Entry> = Vec::new();
        let mut inos: Vec<u32> = Vec::new();
        let mut visited: HashSet<u32> = HashSet::new();
        let mut stack: Vec<(u32, String)> = vec![(ROOTINO, String::new())];
        while let Some((dino, prefix)) = stack.pop() {
            if let Some(c) = ctx {
                c.cancel.check()?;
            }
            if !visited.insert(dino) {
                continue;
            }
            let di = self.read_inode(dino)?;
            if di.mode & 0xF000 != IFDIR {
                return Err(format_err!("inode {dino} should be a directory"));
            }
            let blob = self.read_all(&di, 256 * 1024 * 1024)?;
            let mut pos = 0usize;
            while pos + 8 <= blob.len() {
                let ino = u32::from_le_bytes(blob[pos..pos + 4].try_into().unwrap());
                let reclen = u16::from_le_bytes(blob[pos + 4..pos + 6].try_into().unwrap()) as usize;
                let namlen = blob[pos + 7] as usize;
                if reclen < 8 || reclen % 4 != 0 || pos + reclen > blob.len() || 8 + namlen > reclen {
                    return Err(format_err!("corrupted UFS2 directory (inode {dino}, offset {pos})"));
                }
                if ino != 0 {
                    let name = String::from_utf8_lossy(&blob[pos + 8..pos + 8 + namlen]).into_owned();
                    if name != "." && name != ".." {
                        let child = self.read_inode(ino)?;
                        let path = if prefix.is_empty() {
                            name.clone()
                        } else {
                            format!("{prefix}/{name}")
                        };
                        match child.mode & 0xF000 {
                            IFDIR => {
                                if let Some(c) = ctx {
                                    c.progress.add_files_done(1);
                                }
                                entries.push(Entry {
                                    path: path.clone(),
                                    is_dir: true,
                                    size: 0,
                                    mtime: Some(child.mtime),
                                });
                                inos.push(ino);
                                stack.push((ino, path));
                            }
                            IFREG => {
                                if let Some(c) = ctx {
                                    c.progress.add_files_done(1);
                                    if entries.len() % 256 == 0 {
                                        c.progress.set_current(&path);
                                    }
                                }
                                entries.push(Entry {
                                    path,
                                    is_dir: false,
                                    size: child.size.max(0) as u64,
                                    mtime: Some(child.mtime),
                                });
                                inos.push(ino);
                            }
                            _ => {}
                        }
                    }
                }
                pos += reclen;
            }
        }
        let mut order: Vec<usize> = (0..entries.len()).collect();
        order.sort_by(|&a, &b| entries[a].path.cmp(&entries[b].path));
        self.entries = order.iter().map(|&i| entries[i].clone()).collect();
        self.inos = order.iter().map(|&i| inos[i]).collect();
        Ok(())
    }
}

impl Volume for Ufs2Volume {
    fn kind(&self) -> VolumeKind {
        VolumeKind::Ufs2
    }
    fn entries(&self) -> &[Entry] {
        &self.entries
    }
    fn label(&self) -> String {
        self.volname.clone()
    }
    fn describe(&self) -> Vec<(String, String)> {
        vec![
            ("block".into(), format!("{} KiB", self.bsize / 1024)),
            ("fragment".into(), format!("{} KiB", self.fsize / 1024)),
            ("cyl_groups".into(), self.ncg.to_string()),
            ("inodes_per_group".into(), self.ipg.to_string()),
            ("volume".into(), crate::util::human_bytes(self.size_frags * self.fsize)),
        ]
    }
    fn open(&self, index: usize) -> Result<Arc<dyn ReadAt>> {
        let e = self
            .entries
            .get(index)
            .ok_or_else(|| Error::invalid(crate::t!("err.bad_index")))?;
        if e.is_dir {
            return Err(Error::invalid(crate::t!("err.is_dir", path = e.path)));
        }
        let di = self.read_inode(self.inos[index])?;
        let runs = self.block_map(&di)?;
        Ok(Arc::new(UfsFile {
            img: self.img.clone(),
            runs,
            size: di.size.max(0) as u64,
            bsize: self.bsize,
            fsize: self.fsize,
            frag: self.frag,
        }))
    }
}

struct UfsFile {
    img: Arc<dyn ReadAt>,
    runs: Vec<Run>,
    size: u64,
    bsize: u64,
    fsize: u64,
    frag: u64,
}

impl ReadAt for UfsFile {
    fn len(&self) -> u64 {
        self.size
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        if off >= self.size || buf.is_empty() {
            return Ok(0);
        }
        let want = (buf.len() as u64).min(self.size - off) as usize;
        let mut done = 0usize;
        let mut pos = off;
        let mut i = self.runs.partition_point(|r| r.lbn + r.nblocks <= pos / self.bsize);
        while done < want {
            let lbn = pos / self.bsize;
            let r = match self.runs.get(i) {
                Some(r) if r.lbn <= lbn => *r,
                _ => {
                    let next_start = self.runs.get(i).map_or(u64::MAX, |r| r.lbn * self.bsize);
                    let n = ((want - done) as u64).min(next_start.saturating_sub(pos)) as usize;
                    let n = n.max(1).min(want - done);
                    buf[done..done + n].fill(0);
                    done += n;
                    pos += n as u64;
                    continue;
                }
            };
            let within_run = pos - r.lbn * self.bsize;
            let run_bytes = r.nblocks * self.bsize - within_run;
            let n = ((want - done) as u64).min(run_bytes).min(self.size - pos) as usize;
            if r.frag == u64::MAX {
                buf[done..done + n].fill(0);
            } else {
                let disk = r.frag * self.fsize + within_run;
                self.img.read_exact_at(disk, &mut buf[done..done + n])?;
            }
            done += n;
            pos += n as u64;
            if pos >= (r.lbn + r.nblocks) * self.bsize {
                i += 1;
            }
        }
        let _ = self.frag;
        Ok(want)
    }
}
