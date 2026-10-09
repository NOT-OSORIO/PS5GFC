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

use crate::util::{align_up, ceil_div};

pub const SBLOCK_OFFSET: u64 = 65536;
pub const SBLOCKSIZE: usize = 8192;
pub const FS_UFS2_MAGIC: u32 = 0x1954_0119;
pub const CG_MAGIC: u32 = 0x0009_0255;
pub const ISIZE: u32 = 256;
pub const NDADDR: usize = 12;
pub const NIADDR: usize = 3;
pub const ROOTINO: u32 = 2;
pub const DIRBLKSIZ: usize = 512;
pub const CG_HEADER: u32 = 168;
pub const MAX_CONTIG: u32 = 16;
pub const SECTOR: u64 = 512;

pub const IFDIR: u16 = 0x4000;
pub const IFREG: u16 = 0x8000;
pub const PERM_RX: u16 = 0x16D;
pub const DT_DIR: u8 = 4;
pub const DT_REG: u8 = 8;

#[derive(Debug, Clone, Copy)]
pub struct CgOffsets {
    pub iused: u32,
    pub free: u32,
    pub clustersum: u32,
    pub cluster: u32,
    pub next: u32,
}

pub fn cg_offsets(ipg: u32, fpg: u32, frag: u32, contigsum: u32) -> CgOffsets {
    let iused = CG_HEADER;
    let free = iused + ipg.div_ceil(8);
    let after_free = free + fpg.div_ceil(8);
    if contigsum > 0 {
        let clustersum = align_up(after_free as u64, 4) as u32 - 4;
        let cluster = clustersum + (contigsum + 1) * 4;
        let next = cluster + (fpg / frag).div_ceil(8);
        CgOffsets {
            iused,
            free,
            clustersum,
            cluster,
            next,
        }
    } else {
        CgOffsets {
            iused,
            free,
            clustersum: 0,
            cluster: 0,
            next: after_free,
        }
    }
}

pub fn max_fpg(ipg: u32, bsize: u32, frag: u32, contigsum: u32) -> u32 {
    let fits = |k: u32| cg_offsets(ipg, k * frag, frag, contigsum).next <= bsize;
    if !fits(1) {
        return 0;
    }

    let (mut lo, mut hi) = (1u32, 8 * bsize / frag + 1);
    while hi - lo > 1 {
        let mid = lo + (hi - lo) / 2;
        if fits(mid) {
            lo = mid;
        } else {
            hi = mid;
        }
    }
    lo * frag
}

#[derive(Debug, Clone)]
pub struct Geometry {
    pub bsize: u32,
    pub fsize: u32,
    pub frag: u32,
    pub ncg: u32,
    pub fpg: u32,
    pub ipg: u32,

    pub size: u64,
    pub sblkno: u32,
    pub cblkno: u32,
    pub iblkno: u32,
    pub dblkno: u32,
    pub inopb: u32,
    pub cgsize: u32,
    pub cssize: u32,
    pub cs_frags: u32,
    pub cs_frags_blk: u32,
    pub maxcontig: u32,
    pub contigsum: u32,
}

impl Geometry {
    pub fn derive(bsize: u32, fsize: u32, ncg: u32, fpg: u32, ipg: u32, size: u64) -> Geometry {
        let frag = bsize / fsize;
        let sb_end = (SBLOCK_OFFSET + SBLOCKSIZE as u64).div_ceil(fsize as u64) as u32;
        let sblkno = align_up(sb_end as u64, frag as u64) as u32;
        let cblkno = sblkno + align_up(ceil_div(SBLOCKSIZE as u64, fsize as u64), frag as u64) as u32;
        let iblkno = cblkno + frag;
        let inopb = bsize / ISIZE;
        let inodeblks = ceil_div(ipg as u64, inopb as u64) as u32 * frag;
        let dblkno = iblkno + inodeblks;
        let maxcontig = (65536 / bsize).max(1);
        let contigsum = maxcontig.min(MAX_CONTIG);
        let offs = cg_offsets(ipg, fpg, frag, contigsum);
        let cgsize = align_up(offs.next as u64, fsize as u64) as u32;
        let cssize = align_up(ncg as u64 * 16, fsize as u64) as u32;
        let cs_frags = cssize.div_ceil(fsize);
        let cs_frags_blk = align_up(cs_frags as u64, frag as u64) as u32;
        Geometry {
            bsize,
            fsize,
            frag,
            ncg,
            fpg,
            ipg,
            size,
            sblkno,
            cblkno,
            iblkno,
            dblkno,
            inopb,
            cgsize,
            cssize,
            cs_frags,
            cs_frags_blk,
            maxcontig,
            contigsum,
        }
    }

    pub fn cg_frags(&self, c: u32) -> u64 {
        let start = c as u64 * self.fpg as u64;
        (self.size - start).min(self.fpg as u64)
    }

    pub fn cg_base(&self, c: u32) -> u64 {
        c as u64 * self.fpg as u64
    }

    pub fn cg_data_start(&self, c: u32) -> u64 {
        self.cg_base(c) + self.dblkno as u64 + if c == 0 { self.cs_frags_blk as u64 } else { 0 }
    }

    pub fn nindir(&self) -> u64 {
        (self.bsize / 8) as u64
    }
}

#[inline]
fn p32(b: &mut [u8], at: usize, v: i32) {
    b[at..at + 4].copy_from_slice(&v.to_le_bytes());
}
#[inline]
fn pu32(b: &mut [u8], at: usize, v: u32) {
    b[at..at + 4].copy_from_slice(&v.to_le_bytes());
}
#[inline]
fn p64(b: &mut [u8], at: usize, v: i64) {
    b[at..at + 8].copy_from_slice(&v.to_le_bytes());
}

#[derive(Debug, Clone, Default)]
pub struct Totals {
    pub ndir: u64,
    pub nbfree: u64,
    pub nifree: u64,
    pub nffree: u64,
    pub dsize: u64,
}

#[derive(Debug, Clone)]
pub struct SbParams {
    pub now: i64,
    pub volname: String,
    pub id: [i32; 2],
    pub minfree: i32,
}

pub fn build_superblock(g: &Geometry, t: &Totals, p: &SbParams) -> Vec<u8> {
    let mut b = vec![0u8; SBLOCKSIZE];
    let bshift = g.bsize.trailing_zeros() as i32;
    let fshift = g.fsize.trailing_zeros() as i32;
    let bmask = !(g.bsize as i32 - 1);
    let fmask = !(g.fsize as i32 - 1);
    let sectors_per_frag = (g.fsize as u64 / SECTOR) as i32;
    let offs = cg_offsets(g.ipg, g.fpg, g.frag, g.contigsum);
    let _ = offs;

    p32(&mut b, 0x008, g.sblkno as i32);
    p32(&mut b, 0x00C, g.cblkno as i32);
    p32(&mut b, 0x010, g.iblkno as i32);
    p32(&mut b, 0x014, g.dblkno as i32);
    p32(&mut b, 0x020, p.now as i32);
    p32(&mut b, 0x024, g.size as i32);
    p32(&mut b, 0x028, t.dsize as i32);
    p32(&mut b, 0x02C, g.ncg as i32);
    p32(&mut b, 0x030, g.bsize as i32);
    p32(&mut b, 0x034, g.fsize as i32);
    p32(&mut b, 0x038, g.frag as i32);
    p32(&mut b, 0x03C, p.minfree);
    p32(&mut b, 0x048, bmask);
    p32(&mut b, 0x04C, fmask);
    p32(&mut b, 0x050, bshift);
    p32(&mut b, 0x054, fshift);
    p32(&mut b, 0x058, g.maxcontig as i32);
    p32(&mut b, 0x05C, (g.bsize / 8) as i32);
    p32(&mut b, 0x060, g.frag.trailing_zeros() as i32);
    p32(&mut b, 0x064, (sectors_per_frag as u32).trailing_zeros() as i32);
    p32(&mut b, 0x068, SBLOCKSIZE as i32);
    p32(&mut b, 0x074, (g.bsize / 8) as i32);
    p32(&mut b, 0x078, g.inopb as i32);
    p32(&mut b, 0x07C, sectors_per_frag);
    p32(&mut b, 0x080, 0);
    p32(&mut b, 0x090, p.id[0]);
    p32(&mut b, 0x094, p.id[1]);
    p32(&mut b, 0x098, g.dblkno as i32);
    p32(&mut b, 0x09C, g.cssize as i32);
    p32(&mut b, 0x0A0, g.cgsize as i32);
    p32(&mut b, 0x0B0, g.ncg as i32);
    p32(&mut b, 0x0B4, 1);
    p32(&mut b, 0x0B8, g.ipg as i32);
    p32(&mut b, 0x0BC, g.fpg as i32);

    p32(&mut b, 0x0C0, t.ndir as i32);
    p32(&mut b, 0x0C4, t.nbfree as i32);
    p32(&mut b, 0x0C8, t.nifree as i32);
    p32(&mut b, 0x0CC, t.nffree as i32);
    b[0x0D1] = 1;
    b[0x0D3] = 0x80;
    b[0x0D4] = b'/';
    let vn = p.volname.as_bytes();
    let n = vn.len().min(31);
    b[0x2A8..0x2A8 + n].copy_from_slice(&vn[..n]);
    p32(&mut b, 0x35C, g.bsize.max(65536) as i32);
    p64(&mut b, 0x368, g.size as i64);
    let metaspace = (g.fpg as i64 * p.minfree as i64 / 200 / g.frag as i64) * g.frag as i64;
    p64(&mut b, 0x370, metaspace);
    p64(&mut b, 0x3E0, SBLOCK_OFFSET as i64);
    p64(&mut b, 0x3E8, SBLOCK_OFFSET as i64);
    p64(&mut b, 0x3F0, t.ndir as i64);
    p64(&mut b, 0x3F8, t.nbfree as i64);
    p64(&mut b, 0x400, t.nifree as i64);
    p64(&mut b, 0x408, t.nffree as i64);
    p64(&mut b, 0x430, p.now);
    p64(&mut b, 0x438, g.size as i64);
    p64(&mut b, 0x440, t.dsize as i64);
    p64(&mut b, 0x448, g.dblkno as i64);
    pu32(&mut b, 0x4AC, 16384);
    pu32(&mut b, 0x4B0, 64);
    p64(&mut b, 0x4B8, p.now);
    p32(&mut b, 0x520, 0);
    p32(&mut b, 0x524, g.contigsum as i32);
    p32(&mut b, 0x528, ((NDADDR + NIADDR) * 8) as i32);
    p32(&mut b, 0x52C, 2);

    let nindir = (g.bsize / 8) as i64;
    let mut maxfile = g.bsize as i64 * NDADDR as i64 - 1;
    let mut sizepb = g.bsize as i64;
    for _ in 0..NIADDR {
        sizepb = sizepb.saturating_mul(nindir);
        maxfile = maxfile.saturating_add(sizepb);
    }
    p64(&mut b, 0x530, maxfile);
    p64(&mut b, 0x538, !(bmask as i64));
    p64(&mut b, 0x540, !(fmask as i64));
    p32(&mut b, 0x54C, -1);
    p32(&mut b, 0x550, 1);
    pu32(&mut b, 0x55C, FS_UFS2_MAGIC);
    b
}

pub fn build_recovery(g: &Geometry) -> [u8; 20] {
    let mut r = [0u8; 20];
    pu32(&mut r, 0, FS_UFS2_MAGIC);
    p32(&mut r, 4, g.fpg as i32);
    p32(&mut r, 8, (g.fsize as u64 / SECTOR).trailing_zeros() as i32);
    p32(&mut r, 12, g.sblkno as i32);
    p32(&mut r, 16, g.ncg as i32);
    r
}

#[derive(Debug, Clone)]
pub struct CgUsage {
    pub used_inodes: u32,
    pub ndir: u32,

    pub free_ranges: Vec<(u64, u64)>,
}

pub fn free_block_count(g: &Geometry, u: &CgUsage) -> u64 {
    u.free_ranges.iter().map(|(a, b)| (b - a) / g.frag as u64).sum()
}

pub fn build_cg(g: &Geometry, cgx: u32, u: &CgUsage, now: i64) -> Vec<u8> {
    let offs = cg_offsets(g.ipg, g.fpg, g.frag, g.contigsum);
    let mut b = vec![0u8; g.cgsize as usize];
    let ndblk = g.cg_frags(cgx);
    let nbfree = free_block_count(g, u);
    let nifree = g.ipg - u.used_inodes;

    p32(&mut b, 0x04, CG_MAGIC as i32);
    p32(&mut b, 0x08, now as i32);
    p32(&mut b, 0x0C, cgx as i32);
    p32(&mut b, 0x14, ndblk as i32);
    p32(&mut b, 0x18, u.ndir as i32);
    p32(&mut b, 0x1C, nbfree as i32);
    p32(&mut b, 0x20, nifree as i32);
    p32(&mut b, 0x24, 0);
    p32(&mut b, 0x54, 0);
    p32(&mut b, 0x58, 0);
    p32(&mut b, 0x5C, offs.iused as i32);
    p32(&mut b, 0x60, offs.free as i32);
    p32(&mut b, 0x64, offs.next as i32);
    p32(&mut b, 0x68, offs.clustersum as i32);
    p32(&mut b, 0x6C, offs.cluster as i32);
    let nclusterblks = if g.contigsum > 0 {
        (ndblk / g.frag as u64) as i32
    } else {
        0
    };
    p32(&mut b, 0x70, nclusterblks);
    p32(&mut b, 0x74, g.ipg as i32);
    let initediblk = (g.ipg).min((2 * g.inopb).max(align_up(u.used_inodes as u64, g.inopb as u64) as u32));
    p32(&mut b, 0x78, initediblk as i32);
    p64(&mut b, 0x88, now);

    let iu = offs.iused as usize;
    for i in 0..u.used_inodes as usize {
        b[iu + i / 8] |= 1 << (i % 8);
    }

    let fr = offs.free as usize;
    let frag_bytes = g.fpg.div_ceil(8) as usize;
    for &(s, e) in &u.free_ranges {
        for f in s..e.min(ndblk) {
            b[fr + (f / 8) as usize] |= 1 << (f % 8);
        }
    }
    debug_assert!(fr + frag_bytes <= b.len());

    if g.contigsum > 0 {
        let cl = offs.cluster as usize;
        let nblk = (ndblk / g.frag as u64) as usize;
        let mut sum = vec![0u32; g.contigsum as usize + 1];
        let mut run = 0usize;
        let free_at = |f: usize| -> bool { b[fr + f / 8] & (1 << (f % 8)) != 0 };
        let mut cbits: Vec<usize> = Vec::new();
        for blk in 0..nblk {
            let base = blk * g.frag as usize;
            let all_free = (0..g.frag as usize).all(|k| free_at(base + k));
            if all_free {
                cbits.push(blk);
                run += 1;
            } else if run != 0 {
                sum[run.min(g.contigsum as usize)] += 1;
                run = 0;
            }
        }
        if run != 0 {
            sum[run.min(g.contigsum as usize)] += 1;
        }
        for blk in cbits {
            b[cl + blk / 8] |= 1 << (blk % 8);
        }

        for i in 1..=g.contigsum as usize {
            let at = offs.clustersum as usize + i * 4;
            b[at..at + 4].copy_from_slice(&sum[i].to_le_bytes());
        }
    }
    b
}

#[derive(Debug, Clone, Default)]
pub struct Dinode {
    pub mode: u16,
    pub nlink: i16,
    pub blksize: u32,
    pub size: i64,

    pub blocks: i64,
    pub atime: i64,
    pub mtime: i64,
    pub ctime: i64,
    pub birthtime: i64,
    pub gen: i32,
    pub db: [i64; NDADDR],
    pub ib: [i64; NIADDR],
    pub dirdepth: u32,
}

impl Dinode {
    pub fn to_bytes(&self) -> [u8; ISIZE as usize] {
        let mut o = [0u8; ISIZE as usize];
        o[0..2].copy_from_slice(&self.mode.to_le_bytes());
        o[2..4].copy_from_slice(&self.nlink.to_le_bytes());
        o[0x0C..0x10].copy_from_slice(&self.blksize.to_le_bytes());
        o[0x10..0x18].copy_from_slice(&self.size.to_le_bytes());
        o[0x18..0x20].copy_from_slice(&self.blocks.to_le_bytes());
        o[0x20..0x28].copy_from_slice(&self.atime.to_le_bytes());
        o[0x28..0x30].copy_from_slice(&self.mtime.to_le_bytes());
        o[0x30..0x38].copy_from_slice(&self.ctime.to_le_bytes());
        o[0x38..0x40].copy_from_slice(&self.birthtime.to_le_bytes());
        o[0x50..0x54].copy_from_slice(&self.gen.to_le_bytes());
        for (i, v) in self.db.iter().enumerate() {
            o[0x70 + i * 8..0x78 + i * 8].copy_from_slice(&v.to_le_bytes());
        }
        for (i, v) in self.ib.iter().enumerate() {
            o[0xD0 + i * 8..0xD8 + i * 8].copy_from_slice(&v.to_le_bytes());
        }
        o[0xF0..0xF4].copy_from_slice(&self.dirdepth.to_le_bytes());
        o
    }

    pub fn parse(b: &[u8]) -> Dinode {
        let i64at = |o: usize| i64::from_le_bytes(b[o..o + 8].try_into().unwrap());
        let mut db = [0i64; NDADDR];
        for (i, v) in db.iter_mut().enumerate() {
            *v = i64at(0x70 + i * 8);
        }
        let mut ib = [0i64; NIADDR];
        for (i, v) in ib.iter_mut().enumerate() {
            *v = i64at(0xD0 + i * 8);
        }
        Dinode {
            mode: u16::from_le_bytes([b[0], b[1]]),
            nlink: i16::from_le_bytes([b[2], b[3]]),
            blksize: u32::from_le_bytes(b[0x0C..0x10].try_into().unwrap()),
            size: i64at(0x10),
            blocks: i64at(0x18),
            atime: i64at(0x20),
            mtime: i64at(0x28),
            ctime: i64at(0x30),
            birthtime: i64at(0x38),
            gen: i32::from_le_bytes(b[0x50..0x54].try_into().unwrap()),
            db,
            ib,
            dirdepth: u32::from_le_bytes(b[0xF0..0xF4].try_into().unwrap()),
        }
    }
}

#[inline]
pub fn dirsiz(namelen: usize) -> usize {
    (8 + namelen + 1 + 3) & !3
}

pub struct DirItem<'a> {
    pub ino: u32,
    pub typ: u8,
    pub name: &'a [u8],
}

fn write_entry(out: &mut Vec<u8>, ino: u32, reclen: usize, typ: u8, name: &[u8]) {
    let start = out.len();
    out.extend_from_slice(&ino.to_le_bytes());
    out.extend_from_slice(&(reclen as u16).to_le_bytes());
    out.push(typ);
    out.push(name.len() as u8);
    out.extend_from_slice(name);
    out.resize(start + reclen, 0);
}

pub fn pack_dir(items: &[DirItem], bsize: usize) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::with_capacity(bsize);
    let mut chunk_start = 0usize;
    let mut last_entry_at: Option<usize> = None;
    for it in items {
        let need = dirsiz(it.name.len());
        let used = out.len() - chunk_start;
        if used + need > DIRBLKSIZ {
            match last_entry_at {
                Some(at) => {
                    let new_len = DIRBLKSIZ - (at - chunk_start);
                    out[at + 4..at + 6].copy_from_slice(&(new_len as u16).to_le_bytes());
                    out.resize(chunk_start + DIRBLKSIZ, 0);
                }
                None => unreachable!("entrada maior que um chunk"),
            }
            chunk_start = out.len();
        }
        last_entry_at = Some(out.len());
        write_entry(&mut out, it.ino, need, it.typ, it.name);
    }

    if let Some(at) = last_entry_at {
        let new_len = DIRBLKSIZ - (at - chunk_start);
        out[at + 4..at + 6].copy_from_slice(&(new_len as u16).to_le_bytes());
        out.resize(chunk_start + DIRBLKSIZ, 0);
    }

    let total = align_up(out.len().max(1) as u64, bsize as u64) as usize;
    while out.len() < total {
        write_entry(&mut out, 0, DIRBLKSIZ, 0, b"");
    }
    out
}

pub fn dir_blocks(names: impl Iterator<Item = usize>, bsize: usize) -> u64 {
    let mut chunks = 0usize;
    let mut used = DIRBLKSIZ;
    for nl in std::iter::once(1usize).chain(std::iter::once(2usize)).chain(names) {
        let need = dirsiz(nl);
        if used + need > DIRBLKSIZ {
            chunks += 1;
            used = 0;
        }
        used += need;
    }
    ceil_div((chunks * DIRBLKSIZ) as u64, bsize as u64).max(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dirsiz_matches_freebsd_macro() {
        assert_eq!(dirsiz(1), 12);
        assert_eq!(dirsiz(2), 12);
        assert_eq!(dirsiz(3), 12);
        assert_eq!(dirsiz(4), 16);
        assert_eq!(dirsiz(255), 264);
    }

    #[test]
    fn cg_layout_default_64k() {
        let g = Geometry::derive(65536, 65536, 1, 4000, 512, 4000);
        assert_eq!(g.sblkno, 2);
        assert_eq!(g.cblkno, 3);
        assert_eq!(g.iblkno, 4);
        assert_eq!(g.dblkno, 4 + 2);
        assert_eq!(g.cgsize, 65536);
        assert_eq!(g.cssize, 65536);
        assert_eq!(g.cs_frags_blk, 1);
        assert_eq!(g.inopb, 256);
    }

    #[test]
    fn max_fpg_fits_exactly() {
        let ipg = 4096;
        let f = max_fpg(ipg, 65536, 1, 1);
        assert!(f > 100_000);
        assert!(cg_offsets(ipg, f, 1, 1).next <= 65536);
        assert!(cg_offsets(ipg, f + 1, 1, 1).next > 65536);
    }

    #[test]
    fn pack_dir_chunks_and_padding() {
        let items = vec![
            DirItem {
                ino: 2,
                typ: DT_DIR,
                name: b".",
            },
            DirItem {
                ino: 2,
                typ: DT_DIR,
                name: b"..",
            },
            DirItem {
                ino: 3,
                typ: DT_REG,
                name: b"eboot.bin",
            },
        ];
        let d = pack_dir(&items, 65536);
        assert_eq!(d.len(), 65536);

        assert_eq!(u32::from_le_bytes(d[0..4].try_into().unwrap()), 2);
        assert_eq!(u16::from_le_bytes(d[4..6].try_into().unwrap()), 12);

        let off = 24;
        assert_eq!(u32::from_le_bytes(d[off..off + 4].try_into().unwrap()), 3);
        assert_eq!(
            u16::from_le_bytes(d[off + 4..off + 6].try_into().unwrap()) as usize,
            512 - 24
        );

        assert_eq!(u32::from_le_bytes(d[512..516].try_into().unwrap()), 0);
        assert_eq!(u16::from_le_bytes(d[516..518].try_into().unwrap()), 512);
        assert_eq!(dir_blocks([9usize].into_iter(), 65536), 1);
    }

    #[test]
    fn many_entries_span_chunks_and_blocks() {
        let names: Vec<String> = (0..6000).map(|i| format!("file_{i:05}.dat")).collect();
        let items: Vec<DirItem> = std::iter::once(DirItem {
            ino: 2,
            typ: DT_DIR,
            name: b".",
        })
        .chain(std::iter::once(DirItem {
            ino: 2,
            typ: DT_DIR,
            name: b"..",
        }))
        .chain(names.iter().enumerate().map(|(i, n)| DirItem {
            ino: 3 + i as u32,
            typ: DT_REG,
            name: n.as_bytes(),
        }))
        .collect();
        let d = pack_dir(&items, 65536);
        let nb = dir_blocks(names.iter().map(|n| n.len()), 65536);
        assert_eq!(d.len() as u64, nb * 65536);
        assert!(nb >= 2);

        let mut pos = 0usize;
        let mut seen = 0usize;
        while pos < d.len() {
            let ino = u32::from_le_bytes(d[pos..pos + 4].try_into().unwrap());
            let rl = u16::from_le_bytes(d[pos + 4..pos + 6].try_into().unwrap()) as usize;
            assert!(rl >= 12 && rl % 4 == 0);
            assert_eq!(pos / 512, (pos + rl - 1) / 512, "entrada cruza chunk");
            if ino != 0 {
                seen += 1;
            }
            pos += rl;
        }
        assert_eq!(seen, 6002);
    }

    #[test]
    fn dinode_roundtrip() {
        let mut d = Dinode {
            mode: IFREG | PERM_RX,
            nlink: 1,
            blksize: 65536,
            size: 123456,
            blocks: 256,
            mtime: 1700000000,
            gen: 7,
            ..Default::default()
        };
        d.db[0] = 99;
        d.ib[1] = 1234;
        let p = Dinode::parse(&d.to_bytes());
        assert_eq!(p.size, 123456);
        assert_eq!(p.db[0], 99);
        assert_eq!(p.ib[1], 1234);
        assert_eq!(p.mode, 0x816D);
    }
}
