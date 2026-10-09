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

use std::sync::Arc;

use super::layout::*;
use crate::image::{ExtentBuilder, ExtentImage};
use crate::tree::Tree;
use crate::util::{align_up, ceil_div};
use crate::volume::Volume;
use crate::{Error, Result};

#[derive(Debug, Clone)]
pub struct Ufs2Options {
    pub block_size: u32,

    pub inode_slack: u32,

    pub free_bytes: u64,
    pub preserve_times: bool,
    pub volume_name: String,

    pub timestamp: i64,
}

impl Default for Ufs2Options {
    fn default() -> Self {
        Self {
            block_size: 65536,
            inode_slack: 2048,
            free_bytes: 0,
            preserve_times: true,
            volume_name: String::new(),
            timestamp: std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0),
        }
    }
}

pub struct Ufs2Plan {
    pub image: ExtentImage,
    pub geometry: Geometry,
    pub files: u64,
    pub dirs: u64,
    pub data_bytes: u64,
    pub inodes_used: u64,
}

fn count_meta(n: u64, nindir: u64) -> Result<u64> {
    let mut meta = 0u64;
    let mut rem = n.saturating_sub(NDADDR as u64);
    if rem == 0 {
        return Ok(0);
    }

    meta += 1;
    rem = rem.saturating_sub(nindir);
    if rem == 0 {
        return Ok(meta);
    }

    meta += 1;
    let in_dind = rem.min(nindir * nindir);
    meta += ceil_div(in_dind, nindir);
    rem -= in_dind;
    if rem == 0 {
        return Ok(meta);
    }

    meta += 1;
    let l2 = ceil_div(rem, nindir * nindir).min(nindir);
    meta += l2;
    let in_tind = rem.min(l2 * nindir * nindir);
    meta += ceil_div(in_tind, nindir);
    if rem > nindir * nindir * nindir {
        return Err(Error::invalid(
            "file too large for UFS2 (beyond the triple-indirect limit)",
        ));
    }
    Ok(meta)
}

#[derive(Default, Clone)]
struct NodeAlloc {
    ino: u32,

    nblocks: u64,

    nmeta: u64,
}

struct Alloc<'a> {
    g: &'a Geometry,
    cur: u32,
    next: u64,
    hw: Vec<u64>,
}

impl<'a> Alloc<'a> {
    fn new(g: &'a Geometry) -> Self {
        let hw: Vec<u64> = (0..g.ncg).map(|c| g.cg_data_start(c)).collect();
        Alloc {
            g,
            cur: 0,
            next: hw[0],
            hw,
        }
    }

    fn cg_end(&self, c: u32) -> u64 {
        self.g.cg_base(c) + self.g.cg_frags(c)
    }

    fn run(&mut self, want: u64) -> Result<(u64, u64)> {
        loop {
            let end = self.cg_end(self.cur);
            if self.next < end {
                let n = want.min(end - self.next);
                let start = self.next;
                self.next += n;
                self.hw[self.cur as usize] = self.next;
                return Ok((start, n));
            }
            self.cur += 1;
            if self.cur >= self.g.ncg {
                return Err(Error::invalid("not enough space in the UFS2 image (sizing bug)"));
            }
            self.next = self.g.cg_data_start(self.cur);
            self.hw[self.cur as usize] = self.next.max(self.hw[self.cur as usize]);
        }
    }

    fn one(&mut self) -> Result<u64> {
        Ok(self.run(1)?.0)
    }

    fn segment(&mut self, n: u64) -> Result<Vec<(u64, u64)>> {
        let mut out: Vec<(u64, u64)> = Vec::new();
        let mut left = n;
        while left > 0 {
            let (s, c) = self.run(left)?;
            match out.last_mut() {
                Some((ls, lc)) if *ls + *lc == s => *lc += c,
                _ => out.push((s, c)),
            }
            left -= c;
        }
        Ok(out)
    }
}

fn ptr_block(segs: &[(u64, u64)], bsize: usize) -> Vec<u8> {
    let mut v = Vec::with_capacity(bsize);
    for &(s, c) in segs {
        for k in 0..c {
            v.extend_from_slice(&(s + k).to_le_bytes());
        }
    }
    v.resize(bsize, 0);
    v
}

fn ptr_list_block(ptrs: &[u64], bsize: usize) -> Vec<u8> {
    let mut v = Vec::with_capacity(bsize);
    for p in ptrs {
        v.extend_from_slice(&p.to_le_bytes());
    }
    v.resize(bsize, 0);
    v
}

struct Placed {
    db: [i64; NDADDR],
    ib: [i64; NIADDR],

    runs: Vec<(u64, u64, u64)>,

    meta: Vec<(u64, Vec<u8>)>,
}

fn place(a: &mut Alloc, nblocks: u64, nindir: u64, bsize: usize) -> Result<Placed> {
    let mut p = Placed {
        db: [0; NDADDR],
        ib: [0; NIADDR],
        runs: Vec::new(),
        meta: Vec::new(),
    };
    let mut lbn = 0u64;
    let mut rem = nblocks;

    let direct = rem.min(NDADDR as u64);
    if direct > 0 {
        let segs = a.segment(direct)?;
        let mut k = 0usize;
        let mut l = lbn;
        for &(s, c) in &segs {
            p.runs.push((l, s, c));
            for j in 0..c {
                p.db[k] = (s + j) as i64;
                k += 1;
            }
            l += c;
        }
        lbn += direct;
        rem -= direct;
    }
    if rem == 0 {
        return Ok(p);
    }

    let ind = a.one()?;
    p.ib[0] = ind as i64;
    let take = rem.min(nindir);
    let segs = a.segment(take)?;
    let mut l = lbn;
    for &(s, c) in &segs {
        p.runs.push((l, s, c));
        l += c;
    }
    p.meta.push((ind, ptr_block(&segs, bsize)));
    lbn += take;
    rem -= take;
    if rem == 0 {
        return Ok(p);
    }

    let dind = a.one()?;
    p.ib[1] = dind as i64;
    let mut dptrs: Vec<u64> = Vec::new();
    let in_dind = rem.min(nindir * nindir);
    let mut left = in_dind;
    while left > 0 {
        let sind = a.one()?;
        dptrs.push(sind);
        let take = left.min(nindir);
        let segs = a.segment(take)?;
        let mut l = lbn;
        for &(s, c) in &segs {
            p.runs.push((l, s, c));
            l += c;
        }
        p.meta.push((sind, ptr_block(&segs, bsize)));
        lbn += take;
        left -= take;
    }
    p.meta.push((dind, ptr_list_block(&dptrs, bsize)));
    rem -= in_dind;
    if rem == 0 {
        return Ok(p);
    }

    let tind = a.one()?;
    p.ib[2] = tind as i64;
    let mut tptrs: Vec<u64> = Vec::new();
    let mut left = rem;
    while left > 0 {
        let d2 = a.one()?;
        tptrs.push(d2);
        let mut d2ptrs: Vec<u64> = Vec::new();
        let in_d2 = left.min(nindir * nindir);
        let mut l2 = in_d2;
        while l2 > 0 {
            let sind = a.one()?;
            d2ptrs.push(sind);
            let take = l2.min(nindir);
            let segs = a.segment(take)?;
            let mut l = lbn;
            for &(s, c) in &segs {
                p.runs.push((l, s, c));
                l += c;
            }
            p.meta.push((sind, ptr_block(&segs, bsize)));
            lbn += take;
            l2 -= take;
        }
        p.meta.push((d2, ptr_list_block(&d2ptrs, bsize)));
        left -= in_d2;
    }
    p.meta.push((tind, ptr_list_block(&tptrs, bsize)));
    Ok(p)
}

pub fn plan(volume: Arc<dyn Volume>, opts: &Ufs2Options) -> Result<Ufs2Plan> {
    let bs = opts.block_size as u64;
    if !opts.block_size.is_power_of_two() || !(4096..=65536).contains(&opts.block_size) {
        return Err(Error::invalid(
            "UFS2 block size must be a power of 2 between 4 KiB and 64 KiB",
        ));
    }
    let bsize = opts.block_size;
    let fsize = bsize;
    let frag = 1u32;
    let nindir = bs / 8;

    let tree = Tree::build(volume.entries());
    let n = tree.nodes.len();

    for i in 1..n {
        let nm = tree.nodes[i].name.as_bytes();
        if nm.is_empty() || nm.len() > 255 || nm.contains(&0) || nm.contains(&b'/') {
            return Err(Error::invalid(format!("invalid name for UFS2: {:?}", tree.path_of(i))));
        }
    }

    let mut info: Vec<NodeAlloc> = vec![NodeAlloc::default(); n];
    let (mut nfiles, mut ndirs, mut data_bytes) = (0u64, 0u64, 0u64);
    let mut d_total = 0u64;
    for i in 0..n {
        let node = &tree.nodes[i];
        if node.is_dir {
            ndirs += 1;
            let nb = dir_blocks(node.children.iter().map(|&c| tree.nodes[c].name.len()), bs as usize);
            info[i].nblocks = nb;
        } else {
            nfiles += 1;
            data_bytes += node.size;
            info[i].nblocks = ceil_div(node.size, bs);
        }
        info[i].nmeta = count_meta(info[i].nblocks, nindir)?;
        d_total += info[i].nblocks + info[i].nmeta;
    }
    let inodes_used = 2 + ndirs + nfiles;
    let i_total = inodes_used + opts.inode_slack as u64;
    if i_total > u32::MAX as u64 / 2 {
        return Err(Error::invalid("too many files for UFS2"));
    }
    let slack_blocks = ceil_div(opts.free_bytes, bs).max(1);

    let inopb = (bsize / ISIZE) as u64;
    let maxcontig = (65536 / bsize).max(1);
    let contigsum = maxcontig.min(MAX_CONTIG);
    let ipg_cap = ((262_144u64 / inopb) * inopb).max(inopb);

    let sb_end = ceil_div(SBLOCK_OFFSET + SBLOCKSIZE as u64, fsize as u64);
    let sblkno = align_up(sb_end, frag as u64);
    let cblkno = sblkno + align_up(ceil_div(SBLOCKSIZE as u64, fsize as u64), frag as u64);
    let iblkno = cblkno + frag as u64;
    let need_total = d_total + slack_blocks;

    struct Shape {
        ipg: u32,
        fpg_cap: u64,
        dblkno: u64,
        ncg: u64,
        cs_blk: u64,
        data_cgs: u64,
        last_used: u64,
    }
    let shape_for = |est: u64| -> Result<Shape> {
        let ipg = align_up(ceil_div(i_total, est.max(1)), inopb).clamp(inopb, ipg_cap) as u32;

        let fpg_cap = max_fpg(ipg, bsize - 64, frag, contigsum) as u64;
        let dblkno = iblkno + ceil_div(ipg as u64, inopb) * frag as u64;
        if fpg_cap <= dblkno + 4 {
            return Err(Error::invalid("infeasible UFS2 parameters (cylinder group too small)"));
        }
        let ncg_inode = ceil_div(i_total, ipg as u64).max(1);
        let mut ncg = 1u64;
        loop {
            let cs_blk = align_up(ceil_div(ncg * 16, fsize as u64), frag as u64);
            let cap0 = fpg_cap - dblkno - cs_blk;
            let capn = fpg_cap - dblkno;
            let needed = if need_total <= cap0 {
                1
            } else {
                1 + ceil_div(need_total - cap0, capn)
            };
            let want = needed.max(ncg_inode);
            if want <= ncg {
                break;
            }
            ncg = want;
        }
        let cs_blk = align_up(ceil_div(ncg * 16, fsize as u64), frag as u64);
        let cap0 = fpg_cap - dblkno - cs_blk;
        let capn = fpg_cap - dblkno;
        let (data_cgs, last_used) = if need_total <= cap0 {
            (1u64, need_total)
        } else {
            let extra = ceil_div(need_total - cap0, capn);
            (1 + extra, need_total - cap0 - (extra - 1) * capn)
        };
        Ok(Shape {
            ipg,
            fpg_cap,
            dblkno,
            ncg,
            cs_blk,
            data_cgs,
            last_used,
        })
    };

    let mut est = ceil_div(need_total, 250_000).max(1);
    let mut shape = shape_for(est)?;
    for _ in 0..8 {
        let next = shape.data_cgs.max(1);
        if shape.ncg == shape.data_cgs || next == est {
            break;
        }
        est = next;
        shape = shape_for(est)?;
    }
    let Shape {
        ipg,
        fpg_cap,
        dblkno,
        ncg,
        cs_blk,
        data_cgs,
        last_used,
    } = shape;

    let (fpg, size) = if ncg == 1 {
        let sz = dblkno + cs_blk + last_used;
        (sz, sz)
    } else {
        let mut sz = (ncg - 1) * fpg_cap;
        let last = if data_cgs == ncg {
            dblkno + last_used
        } else {
            dblkno + 8
        };
        sz += last;
        (fpg_cap, sz)
    };
    let mut g = Geometry::derive(bsize, fsize, ncg as u32, fpg as u32, ipg, size);
    g.maxcontig = maxcontig;
    g.contigsum = contigsum;
    if g.cs_frags_blk as u64 != cs_blk {
        return Err(Error::invalid("internal inconsistency in the UFS2 group summary"));
    }

    let order = tree.preorder();
    info[0].ino = ROOTINO;
    for (k, &i) in order.iter().enumerate() {
        info[i].ino = ROOTINO + 1 + k as u32;
    }

    let mut alloc = Alloc::new(&g);
    let mut placed: Vec<Option<Placed>> = (0..n).map(|_| None).collect();

    placed[0] = Some(place(&mut alloc, info[0].nblocks, nindir, bs as usize)?);
    for &i in &order {
        placed[i] = Some(place(&mut alloc, info[i].nblocks, nindir, bs as usize)?);
    }

    let mut eb = ExtentBuilder::new();
    let ts = |t: Option<i64>| -> i64 {
        match (opts.preserve_times, t) {
            (true, Some(x)) => x,
            _ => opts.timestamp,
        }
    };

    let mut ino_tables: Vec<Vec<u8>> = Vec::with_capacity(g.ncg as usize);
    let mut used_per_cg: Vec<u32> = vec![0; g.ncg as usize];
    let mut ndir_per_cg: Vec<u32> = vec![0; g.ncg as usize];

    let max_ino = ROOTINO + order.len() as u32;
    for ino in 0..=max_ino {
        let c = (ino / g.ipg) as usize;
        if c >= used_per_cg.len() {
            return Err(Error::invalid("not enough inodes in the computed geometry"));
        }
        used_per_cg[c] += 1;
    }
    let initediblk = |c: usize| -> u32 {
        g.ipg
            .min((2 * g.inopb).max(align_up(used_per_cg[c] as u64, g.inopb as u64) as u32))
    };
    for c in 0..g.ncg as usize {
        let mut t = vec![0u8; initediblk(c) as usize * ISIZE as usize];
        for i in 0..initediblk(c) as usize {
            let ino = c as u64 * g.ipg as u64 + i as u64;
            let gen = (((ino.wrapping_mul(2_654_435_761)) >> 8) as i32) | 1;
            t[i * 256 + 0x50..i * 256 + 0x54].copy_from_slice(&gen.to_le_bytes());
        }
        ino_tables.push(t);
    }

    let depth_of = |mut i: usize| -> u32 {
        let mut d = 0;
        while i != 0 {
            i = tree.nodes[i].parent;
            d += 1;
        }
        d
    };

    for i in 0..n {
        let node = &tree.nodes[i];
        let pl = placed[i].as_ref().unwrap();
        let ino = info[i].ino;
        let total_blocks = info[i].nblocks + info[i].nmeta;
        let sectors = (total_blocks * (fsize as u64 / SECTOR)) as i64;
        let mut di = Dinode {
            mode: if node.is_dir { IFDIR | PERM_RX } else { IFREG | PERM_RX },
            nlink: 1,
            blksize: bsize,
            blocks: sectors,
            gen: 1,
            db: pl.db,
            ib: pl.ib,
            ..Default::default()
        };
        let t = ts(node.mtime);
        di.atime = t;
        di.mtime = t;
        di.ctime = t;
        di.birthtime = t;

        if node.is_dir {
            let subdirs = node.children.iter().filter(|&&c| tree.nodes[c].is_dir).count() as i16;
            di.nlink = 2 + subdirs;
            di.size = (info[i].nblocks * bs) as i64;
            di.dirdepth = depth_of(i);
            ndir_per_cg[(ino / g.ipg) as usize] += 1;

            let parent_ino = if i == 0 { ROOTINO } else { info[node.parent].ino };
            let mut items: Vec<DirItem> = Vec::with_capacity(node.children.len() + 2);
            items.push(DirItem {
                ino,
                typ: DT_DIR,
                name: b".",
            });
            items.push(DirItem {
                ino: parent_ino,
                typ: DT_DIR,
                name: b"..",
            });
            for &c in &node.children {
                let ch = &tree.nodes[c];
                items.push(DirItem {
                    ino: info[c].ino,
                    typ: if ch.is_dir { DT_DIR } else { DT_REG },
                    name: ch.name.as_bytes(),
                });
            }
            let blob = Arc::new(pack_dir(&items, bs as usize));
            debug_assert_eq!(blob.len() as u64, info[i].nblocks * bs);
            for &(lbn, frag_abs, cnt) in &pl.runs {
                eb.meta(frag_abs * fsize as u64, blob.clone(), (lbn * bs) as usize, cnt * bs);
            }
        } else {
            di.size = node.size as i64;
            if let Some(entry) = node.entry {
                for &(lbn, frag_abs, cnt) in &pl.runs {
                    let off = lbn * bs;
                    let len = (cnt * bs).min(node.size.saturating_sub(off));
                    eb.file_part(frag_abs * fsize as u64, entry, off, len, node.size);
                }
            }
        }
        for (frag_abs, data) in &pl.meta {
            eb.meta_vec(frag_abs * fsize as u64, data.clone());
        }
        let c = (ino / g.ipg) as usize;
        let slot = (ino % g.ipg) as usize;
        ino_tables[c][slot * 256..slot * 256 + 256].copy_from_slice(&di.to_bytes());
    }

    let mut totals = Totals::default();
    let mut cg_blobs: Vec<Vec<u8>> = Vec::with_capacity(g.ncg as usize);
    for c in 0..g.ncg {
        let base = g.cg_base(c);
        let end = g.cg_frags(c);
        let mut free: Vec<(u64, u64)> = Vec::new();
        if c > 0 {
            free.push((0, g.sblkno as u64));
        }
        let hw_rel = alloc.hw[c as usize] - base;
        if hw_rel < end {
            free.push((hw_rel, end));
        }
        let usage = CgUsage {
            used_inodes: used_per_cg[c as usize],
            ndir: ndir_per_cg[c as usize],
            free_ranges: free,
        };
        totals.ndir += usage.ndir as u64;
        totals.nbfree += free_block_count(&g, &usage);
        totals.nifree += (g.ipg - usage.used_inodes) as u64;
        cg_blobs.push(build_cg(&g, c, &usage, opts.timestamp));
    }
    totals.dsize = g.size - g.sblkno as u64 - g.ncg as u64 * (g.dblkno - g.sblkno) as u64 - g.cs_frags as u64;

    let sbp = SbParams {
        now: opts.timestamp,
        volname: opts.volume_name.clone(),
        id: [opts.timestamp as i32, ((opts.timestamp >> 16) as i32) ^ 0x5053_3546],
        minfree: 0,
    };
    let sb = build_superblock(&g, &totals, &sbp);

    eb.meta_vec(SBLOCK_OFFSET - 20, build_recovery(&g).to_vec());
    eb.meta_vec(SBLOCK_OFFSET, sb.clone());

    let mut cs = Vec::with_capacity(g.ncg as usize * 16);
    for c in 0..g.ncg as usize {
        let b = &cg_blobs[c];
        cs.extend_from_slice(&b[0x18..0x28]);
    }
    eb.meta_vec(g.dblkno as u64 * fsize as u64, cs);
    let sb_arc = Arc::new(sb);
    for c in 0..g.ncg {
        let base = g.cg_base(c);
        if c > 0 {
            eb.meta(
                (base + g.sblkno as u64) * fsize as u64,
                sb_arc.clone(),
                0,
                SBLOCKSIZE as u64,
            );
        }
        eb.meta_vec(
            (base + g.cblkno as u64) * fsize as u64,
            std::mem::take(&mut cg_blobs[c as usize]),
        );
        let t = std::mem::take(&mut ino_tables[c as usize]);
        eb.meta_vec((base + g.iblkno as u64) * fsize as u64, t);
    }

    let image = eb.finish(g.size * fsize as u64, volume);
    Ok(Ufs2Plan {
        image,
        geometry: g,
        files: nfiles,
        dirs: ndirs - 1,
        data_bytes,
        inodes_used,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn meta_block_counts() {
        let ni = 8192;
        assert_eq!(count_meta(0, ni).unwrap(), 0);
        assert_eq!(count_meta(12, ni).unwrap(), 0);
        assert_eq!(count_meta(13, ni).unwrap(), 1);
        assert_eq!(count_meta(12 + 8192, ni).unwrap(), 1);
        assert_eq!(count_meta(12 + 8192 + 1, ni).unwrap(), 1 + 1 + 1);
        assert_eq!(count_meta(12 + 8192 + 8192 * 2, ni).unwrap(), 1 + 1 + 2);
    }
}
