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

use std::cell::RefCell;
use std::collections::VecDeque;
use std::io;
use std::sync::{Arc, Mutex};

use flate2::{Compress, Compression, Decompress, FlushCompress, FlushDecompress, Status};

use crate::ctl::Ctx;
use crate::io::{OutFile, ReadAt};
use crate::par::ordered_map;
use crate::remote::upload::SeqSink;
use crate::util::ceil_div;
use crate::{format_err, Error, Result};

pub const PFSC_MAGIC: i32 = 0x4353_4650;
pub const PFSC_UNK8: i32 = 6;
pub const BLOCK: u64 = 0x10000;
pub const HEADER_SIZE: usize = 0x30;
pub const OFFSETS_AT: u64 = 0x400;
pub const MIN_DATA_START: u64 = 0x10000;

pub fn header_size(block_count: u64) -> u64 {
    let table = (block_count + 1) * 8;
    let capacity = MIN_DATA_START - OFFSETS_AT;
    let extra = table.saturating_sub(capacity);
    MIN_DATA_START + ceil_div(extra, BLOCK) * BLOCK
}

#[derive(Debug, Clone)]
pub struct PfscOptions {
    pub level: u32,

    pub threshold_gain_pct: f32,
    pub threads: usize,

    pub skip_incompressible: bool,
}

impl Default for PfscOptions {
    fn default() -> Self {
        Self {
            level: 7,
            threshold_gain_pct: 0.0,
            threads: crate::util::default_threads(),
            skip_incompressible: true,
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct PfscStats {
    pub stored_len: u64,
    pub logical_len: u64,
    pub blocks: u64,
    pub blocks_raw: u64,
    pub blocks_zero: u64,
    pub blocks_skipped: u64,

    pub ms_read: u64,

    pub ms_encode: u64,

    pub ms_write: u64,
}

struct Worker {
    level: u32,
    enc: Compress,
    zero_stream: Option<Vec<u8>>,
}

thread_local! {
    static WORKER: RefCell<Option<Worker>> = const { RefCell::new(None) };
}

fn is_all_zero(b: &[u8]) -> bool {
    let (head, mid, tail) = unsafe { b.align_to::<u64>() };
    head.iter().all(|&x| x == 0) && mid.iter().all(|&x| x == 0) && tail.iter().all(|&x| x == 0)
}

fn entropy(b: &[u8]) -> f64 {
    let mut c0 = [0u32; 256];
    let mut c1 = [0u32; 256];
    let mut c2 = [0u32; 256];
    let mut c3 = [0u32; 256];
    let mut chunks = b.chunks_exact(4);
    for ch in &mut chunks {
        c0[ch[0] as usize] += 1;
        c1[ch[1] as usize] += 1;
        c2[ch[2] as usize] += 1;
        c3[ch[3] as usize] += 1;
    }
    for &x in chunks.remainder() {
        c0[x as usize] += 1;
    }
    let n = b.len() as f64;
    let mut h = 0.0;
    for i in 0..256 {
        let c = (c0[i] + c1[i] + c2[i] + c3[i]) as f64;
        if c > 0.0 {
            let p = c / n;
            h -= p * p.log2();
        }
    }
    h
}

pub enum Encoded {
    Raw,

    Zlib(Vec<u8>),
}

fn compress_zlib(enc: &mut Compress, input: &[u8]) -> Option<Vec<u8>> {
    enc.reset();
    let mut out: Vec<u8> = Vec::with_capacity(BLOCK as usize - 1);
    match enc.compress_vec(input, &mut out, FlushCompress::Finish) {
        Ok(Status::StreamEnd) if (out.len() as u64) < BLOCK => Some(out),
        _ => None,
    }
}

pub fn encode_block(block: &[u8], opts: &PfscOptions) -> (Encoded, BlockKind) {
    debug_assert_eq!(block.len() as u64, BLOCK);
    WORKER.with(|cell| {
        let mut slot = cell.borrow_mut();
        let w = slot.get_or_insert_with(|| Worker {
            level: opts.level,
            enc: Compress::new(Compression::new(opts.level), true),
            zero_stream: None,
        });
        if w.level != opts.level {
            *w = Worker {
                level: opts.level,
                enc: Compress::new(Compression::new(opts.level), true),
                zero_stream: None,
            };
        }

        if is_all_zero(block) {
            if w.zero_stream.is_none() {
                w.zero_stream = compress_zlib(&mut w.enc, block);
            }
            return match &w.zero_stream {
                Some(z) => (Encoded::Zlib(z.clone()), BlockKind::Zero),
                None => (Encoded::Raw, BlockKind::Raw),
            };
        }
        if opts.skip_incompressible && entropy(block) >= 7.985 {
            return (Encoded::Raw, BlockKind::Skipped);
        }
        match compress_zlib(&mut w.enc, block) {
            Some(z) => {
                let gain = (BLOCK as f32 - z.len() as f32) / BLOCK as f32 * 100.0;
                if gain >= opts.threshold_gain_pct {
                    (Encoded::Zlib(z), BlockKind::Compressed)
                } else {
                    (Encoded::Raw, BlockKind::Raw)
                }
            }
            None => (Encoded::Raw, BlockKind::Raw),
        }
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BlockKind {
    Compressed,
    Raw,
    Zero,
    Skipped,
}

fn prepare(image: &dyn ReadAt, opts: &PfscOptions) -> Result<(u64, u64)> {
    let logical = image.len();
    if logical == 0 {
        return Err(Error::invalid("empty image: nothing to compress"));
    }
    if !(1..=9).contains(&opts.level) {
        return Err(Error::invalid("compression level must be between 1 and 9"));
    }
    let block_count = ceil_div(logical, BLOCK);
    Ok((block_count, header_size(block_count)))
}

struct EncodeTimes {
    ms_read: u64,
    ms_encode: u64,
}

fn run_blocks(
    image: &dyn ReadAt,
    opts: &PfscOptions,
    ctx: &Ctx,
    block_count: u64,
    mut on_block: impl FnMut(&[u8], u64, BlockKind) -> Result<()>,
) -> Result<EncodeTimes> {
    let logical = image.len();
    let t_read = std::sync::atomic::AtomicU64::new(0);
    let t_enc = std::sync::atomic::AtomicU64::new(0);
    let workers = opts.threads.max(1);

    const BATCH: u64 = 16;
    let batches = ceil_div(block_count, BATCH);

    let window = (workers * 6).max(32);

    ordered_map(
        batches,
        workers,
        window,
        &ctx.cancel,
        |bi| {
            let first = bi * BATCH;
            let n = BATCH.min(block_count - first);
            let off = first * BLOCK;
            let raw_len = (n * BLOCK).min(logical - off) as usize;
            let mut buf = vec![0u8; (n * BLOCK) as usize];
            let t0 = std::time::Instant::now();
            image.read_exact_at(off, &mut buf[..raw_len])?;
            let t1 = std::time::Instant::now();
            let mut out: Vec<(u64, Encoded, BlockKind)> = Vec::with_capacity(n as usize);
            for k in 0..n as usize {
                let blk = &buf[k * BLOCK as usize..(k + 1) * BLOCK as usize];
                let this_raw = (raw_len.saturating_sub(k * BLOCK as usize)).min(BLOCK as usize) as u64;
                let (enc, kind) = encode_block(blk, opts);

                out.push((this_raw, enc, kind));
            }
            t_read.fetch_add((t1 - t0).as_nanos() as u64, std::sync::atomic::Ordering::Relaxed);
            t_enc.fetch_add(t1.elapsed().as_nanos() as u64, std::sync::atomic::Ordering::Relaxed);
            Ok((out, buf))
        },
        |_bi, (items, buf)| {
            for (k, (raw_len, enc, kind)) in items.into_iter().enumerate() {
                let stored: &[u8] = match &enc {
                    Encoded::Raw => &buf[k * BLOCK as usize..(k + 1) * BLOCK as usize],
                    Encoded::Zlib(z) => z,
                };
                on_block(stored, raw_len, kind)?;
            }
            Ok(())
        },
    )?;
    Ok(EncodeTimes {
        ms_read: t_read.load(std::sync::atomic::Ordering::Relaxed) / 1_000_000,
        ms_encode: t_enc.load(std::sync::atomic::Ordering::Relaxed) / 1_000_000,
    })
}

fn count_kind(stats: &mut PfscStats, kind: BlockKind) {
    match kind {
        BlockKind::Zero => stats.blocks_zero += 1,
        BlockKind::Skipped => {
            stats.blocks_skipped += 1;
            stats.blocks_raw += 1;
        }
        BlockKind::Raw => stats.blocks_raw += 1,
        BlockKind::Compressed => {}
    }
}

fn build_head(hdr: u64, block_count: u64, offsets: &[u64]) -> Vec<u8> {
    let mut head = vec![0u8; hdr as usize];
    head[0..4].copy_from_slice(&PFSC_MAGIC.to_le_bytes());
    head[4..8].copy_from_slice(&0i32.to_le_bytes());
    head[8..12].copy_from_slice(&PFSC_UNK8.to_le_bytes());
    head[12..16].copy_from_slice(&(BLOCK as i32).to_le_bytes());
    head[16..24].copy_from_slice(&(BLOCK as i64).to_le_bytes());
    head[24..32].copy_from_slice(&(OFFSETS_AT as i64).to_le_bytes());
    head[32..40].copy_from_slice(&hdr.to_le_bytes());
    head[40..48].copy_from_slice(&((block_count * BLOCK) as i64).to_le_bytes());
    for (k, o) in offsets.iter().enumerate() {
        let at = OFFSETS_AT as usize + k * 8;
        head[at..at + 8].copy_from_slice(&o.to_le_bytes());
    }
    head
}

pub fn write_pfsc(out: &OutFile, base: u64, image: &dyn ReadAt, opts: &PfscOptions, ctx: &Ctx) -> Result<PfscStats> {
    let logical = image.len();
    let (block_count, hdr) = prepare(image, opts)?;

    let mut offsets: Vec<u64> = Vec::with_capacity(block_count as usize + 1);
    offsets.push(hdr);
    let mut cursor = base + hdr;
    let mut wbuf: Vec<u8> = Vec::with_capacity(4 << 20);
    let mut wbuf_start = cursor;
    let mut stats = PfscStats {
        logical_len: logical,
        blocks: block_count,
        ..Default::default()
    };
    let mut t_write = 0u128;

    let times = run_blocks(image, opts, ctx, block_count, |stored, raw_len, kind| {
        wbuf.extend_from_slice(stored);
        cursor += stored.len() as u64;
        offsets.push(cursor - base);
        count_kind(&mut stats, kind);
        ctx.progress.add_done(raw_len);
        ctx.progress.add_out(stored.len() as u64);
        if wbuf.len() >= (4 << 20) - 70_000 {
            let tw = std::time::Instant::now();
            out.write_all_at(wbuf_start, &wbuf)?;
            t_write += tw.elapsed().as_nanos();
            wbuf_start += wbuf.len() as u64;
            wbuf.clear();
        }
        Ok(())
    })?;
    if !wbuf.is_empty() {
        out.write_all_at(wbuf_start, &wbuf)?;
    }

    out.write_all_at(base, &build_head(hdr, block_count, &offsets))?;
    stats.stored_len = *offsets.last().unwrap();
    stats.ms_read = times.ms_read;
    stats.ms_encode = times.ms_encode;
    stats.ms_write = (t_write / 1_000_000) as u64;
    Ok(stats)
}

pub struct PfscPlan {
    pub logical_len: u64,
    pub block_count: u64,

    pub hdr: u64,

    pub offsets: Vec<u64>,
    pub stats: PfscStats,
}

impl PfscPlan {
    pub fn stored_len(&self) -> u64 {
        *self.offsets.last().unwrap()
    }
}

pub fn measure_pfsc(image: &dyn ReadAt, opts: &PfscOptions, ctx: &Ctx) -> Result<PfscPlan> {
    let (block_count, hdr) = prepare(image, opts)?;
    let mut offsets: Vec<u64> = Vec::with_capacity(block_count as usize + 1);
    offsets.push(hdr);
    let mut stats = PfscStats {
        logical_len: image.len(),
        blocks: block_count,
        ..Default::default()
    };
    let times = run_blocks(image, opts, ctx, block_count, |stored, raw_len, kind| {
        let last = *offsets.last().unwrap();
        offsets.push(last + stored.len() as u64);
        count_kind(&mut stats, kind);
        ctx.progress.add_done(raw_len);
        Ok(())
    })?;
    stats.stored_len = *offsets.last().unwrap();
    stats.ms_read = times.ms_read;
    stats.ms_encode = times.ms_encode;
    Ok(PfscPlan {
        logical_len: image.len(),
        block_count,
        hdr,
        offsets,
        stats,
    })
}

pub fn stream_pfsc(
    plan: &PfscPlan,
    image: &dyn ReadAt,
    opts: &PfscOptions,
    sink: &mut dyn SeqSink,
    ctx: &Ctx,
) -> Result<PfscStats> {
    if image.len() != plan.logical_len {
        return Err(Error::invalid("the image changed between the two compression passes"));
    }
    sink.write_all(&build_head(plan.hdr, plan.block_count, &plan.offsets))?;
    let mut stats = PfscStats {
        logical_len: plan.logical_len,
        blocks: plan.block_count,
        ..Default::default()
    };
    let mut k = 0usize;
    let times = run_blocks(image, opts, ctx, plan.block_count, |stored, raw_len, kind| {
        let want = plan.offsets.get(k + 1).zip(plan.offsets.get(k)).map(|(b, a)| b - a);
        if want != Some(stored.len() as u64) {
            return Err(Error::invalid(format!(
                "block {k} compressed differently in the second pass (the source changed while converting?)"
            )));
        }
        k += 1;
        sink.write_all(stored)?;
        count_kind(&mut stats, kind);
        ctx.progress.add_done(raw_len);
        ctx.progress.add_out(stored.len() as u64);
        Ok(())
    })?;
    stats.stored_len = plan.stored_len();
    stats.ms_read = times.ms_read;
    stats.ms_encode = times.ms_encode;
    Ok(stats)
}

pub fn is_pfsc(head: &[u8]) -> bool {
    head.len() >= 4 && i32::from_le_bytes([head[0], head[1], head[2], head[3]]) == PFSC_MAGIC
}

struct DecodeCache {
    map: Vec<(u64, Arc<Vec<u8>>)>,
    order: VecDeque<u64>,
}

pub struct PfscView {
    inner: Arc<dyn ReadAt>,
    offsets: Vec<u64>,
    logical_len: u64,
    cache: Mutex<DecodeCache>,
}

const CACHE_BLOCKS: usize = 24;

thread_local! {
    static DEC: RefCell<Option<Decompress>> = const { RefCell::new(None) };
}

fn inflate_block(idx: u64, stored: &[u8], out: &mut [u8]) -> io::Result<()> {
    let span = stored.len() as u64;
    if span == 0 {
        out.fill(0);
        return Ok(());
    }
    if span == BLOCK {
        out.copy_from_slice(stored);
        return Ok(());
    }
    if span > BLOCK {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("PFSC block {idx} larger than the logical block"),
        ));
    }
    DEC.with(|c| {
        let mut slot = c.borrow_mut();
        let d = slot.get_or_insert_with(|| Decompress::new(true));
        d.reset(true);
        match d.decompress(stored, out, FlushDecompress::Finish) {
            Ok(Status::StreamEnd) if d.total_out() == BLOCK => Ok(()),
            Ok(_) => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("PFSC block {idx}: incomplete stream"),
            )),
            Err(e) => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("bloco PFSC {idx}: {e}"),
            )),
        }
    })
}

const RUN_BLOCKS: u64 = 32;

impl PfscView {
    pub fn open(inner: Arc<dyn ReadAt>) -> Result<Self> {
        let mut head = [0u8; HEADER_SIZE];
        inner
            .read_exact_at(0, &mut head)
            .map_err(|_| format_err!("truncated PFSC (header)"))?;
        let i32at = |o: usize| i32::from_le_bytes(head[o..o + 4].try_into().unwrap());
        let i64at = |o: usize| i64::from_le_bytes(head[o..o + 8].try_into().unwrap());
        if i32at(0) != PFSC_MAGIC {
            return Err(format_err!("invalid PFSC magic"));
        }
        if i32at(12) as u64 != BLOCK || i64at(16) as u64 != BLOCK {
            return Err(Error::unsupported(format!("unsupported PFSC block size {}", i32at(12))));
        }
        let table_off = i64at(24);
        let data_start = u64::from_le_bytes(head[32..40].try_into().unwrap());
        let logical_len = i64at(40);
        if table_off < HEADER_SIZE as i64 || logical_len < 0 || logical_len as u64 % BLOCK != 0 {
            return Err(format_err!("inconsistent PFSC header"));
        }
        let table_off = table_off as u64;
        let logical_len = logical_len as u64;
        let block_count = logical_len / BLOCK;
        let tbl_bytes = (block_count + 1) * 8;
        if table_off + tbl_bytes > data_start || data_start > inner.len() {
            return Err(format_err!("PFSC offset table out of bounds"));
        }
        let raw = inner.read_vec(table_off, tbl_bytes as usize)?;
        let offsets: Vec<u64> = raw
            .chunks_exact(8)
            .map(|c| u64::from_le_bytes(c.try_into().unwrap()))
            .collect();
        if offsets[0] != data_start {
            return Err(format_err!("PFSC offsets must start at data_start"));
        }
        if offsets.windows(2).any(|w| w[1] < w[0]) || *offsets.last().unwrap() > inner.len() {
            return Err(format_err!("PFSC offsets not monotonic or outside the file"));
        }
        Ok(Self {
            inner,
            offsets,
            logical_len,
            cache: Mutex::new(DecodeCache {
                map: Vec::new(),
                order: VecDeque::new(),
            }),
        })
    }

    pub fn block_count(&self) -> u64 {
        self.logical_len / BLOCK
    }

    pub fn stored_len(&self) -> u64 {
        *self.offsets.last().unwrap()
    }

    fn decode_uncached(&self, idx: u64) -> io::Result<Vec<u8>> {
        let mut out = vec![0u8; BLOCK as usize];
        self.decode_run(idx, 1, &mut out)?;
        Ok(out)
    }

    fn decode_run(&self, first: u64, count: u64, out: &mut [u8]) -> io::Result<()> {
        debug_assert_eq!(out.len() as u64, count * BLOCK);
        let a = self.offsets[first as usize];
        let b = self.offsets[(first + count) as usize];
        let mut stored = vec![0u8; (b - a) as usize];
        if !stored.is_empty() {
            self.inner.read_exact_at(a, &mut stored)?;
        }
        for k in 0..count {
            let lo = (self.offsets[(first + k) as usize] - a) as usize;
            let hi = (self.offsets[(first + k + 1) as usize] - a) as usize;
            let dst = &mut out[(k * BLOCK) as usize..((k + 1) * BLOCK) as usize];
            inflate_block(first + k, &stored[lo..hi], dst)?;
        }
        Ok(())
    }

    fn block(&self, idx: u64) -> io::Result<Arc<Vec<u8>>> {
        {
            let c = self.cache.lock().unwrap();
            if let Some((_, b)) = c.map.iter().find(|(k, _)| *k == idx) {
                return Ok(b.clone());
            }
        }
        let data = Arc::new(self.decode_uncached(idx)?);
        let mut c = self.cache.lock().unwrap();
        if c.map.len() >= CACHE_BLOCKS {
            if let Some(old) = c.order.pop_front() {
                c.map.retain(|(k, _)| *k != old);
            }
        }
        c.map.push((idx, data.clone()));
        c.order.push_back(idx);
        Ok(data)
    }

    pub fn decode_block_raw(&self, idx: u64) -> io::Result<Vec<u8>> {
        self.decode_uncached(idx)
    }
}

impl ReadAt for PfscView {
    fn len(&self) -> u64 {
        self.logical_len
    }

    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        if off >= self.logical_len || buf.is_empty() {
            return Ok(0);
        }
        let want = (buf.len() as u64).min(self.logical_len - off) as usize;
        let mut done = 0usize;
        let mut pos = off;
        while done < want {
            let idx = pos / BLOCK;
            let within = (pos % BLOCK) as usize;
            if within == 0 && want - done >= BLOCK as usize {
                let whole = ((want - done) as u64 / BLOCK).min(RUN_BLOCKS);
                let n = (whole * BLOCK) as usize;
                self.decode_run(idx, whole, &mut buf[done..done + n])?;
                done += n;
                pos += n as u64;
            } else {
                let n = (want - done).min(BLOCK as usize - within);
                let b = self.block(idx)?;
                buf[done..done + n].copy_from_slice(&b[within..within + n]);
                done += n;
                pos += n as u64;
            }
        }
        Ok(want)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::io::MemReader;

    #[test]
    fn header_size_grows_with_table() {
        assert_eq!(header_size(0), 0x10000);
        assert_eq!(header_size(100), 0x10000);

        assert_eq!(header_size(8063), 0x10000);
        assert_eq!(header_size(8064), 0x20000);
    }

    #[test]
    fn entropy_distinguishes() {
        let zeros = vec![0u8; 65536];
        assert!(entropy(&zeros) < 0.01);
        let mut x: u64 = 0x1234_5678_9abc_def0;
        let rnd: Vec<u8> = (0..65536)
            .map(|_| {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                (x >> 24) as u8
            })
            .collect();
        assert!(entropy(&rnd) > 7.98);
        let text: Vec<u8> = b"Hello PS5 world. ".iter().cycle().take(65536).copied().collect();
        assert!(entropy(&text) < 6.0);
    }

    fn pseudo_image(len: usize) -> Vec<u8> {
        let mut v = Vec::with_capacity(len);
        let mut x: u64 = 88172645463325252;
        for i in 0..len {
            let blk = i / 65536;
            let b = match blk % 4 {
                0 => 0u8,
                1 => (i % 251) as u8,
                2 => {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    (x >> 8) as u8
                }
                _ => b"abcdefgh"[i % 8],
            };
            v.push(b);
        }
        v
    }

    #[test]
    fn write_and_read_back_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.pfsc");
        let data = pseudo_image(65536 * 10 + 12345);
        let img = MemReader::new(data.clone());
        let out = OutFile::create(&path, 0).unwrap();
        let ctx = Ctx::new();
        let stats = write_pfsc(
            &out,
            0,
            &img,
            &PfscOptions {
                threads: 4,
                ..Default::default()
            },
            &ctx,
        )
        .unwrap();
        assert_eq!(stats.blocks, 11);
        assert!(stats.blocks_zero >= 3);
        assert!(stats.stored_len < data.len() as u64 + 0x10000);

        let r: Arc<dyn ReadAt> = Arc::new(crate::io::FileReader::open(&path).unwrap());
        let view = PfscView::open(r).unwrap();
        assert_eq!(view.len(), 11 * 65536);
        let back = view.read_vec(0, data.len()).unwrap();
        assert_eq!(back, data);

        let mut buf = vec![0u8; 100_000];
        view.read_exact_at(70_000, &mut buf).unwrap();
        assert_eq!(&buf[..], &data[70_000..170_000]);

        let tail = view.read_vec(data.len() as u64, 1000).unwrap();
        assert!(tail.iter().all(|&b| b == 0));
    }

    #[test]
    fn zlib_header_is_level_marker() {
        let blk = vec![b'x'; 65536];
        let (e, k) = encode_block(
            &blk,
            &PfscOptions {
                level: 7,
                ..Default::default()
            },
        );
        assert_eq!(k, BlockKind::Compressed);
        if let Encoded::Zlib(z) = e {
            assert_eq!(z[0], 0x78);
            assert_eq!(z[1], 0xDA);
        } else {
            panic!("esperava zlib");
        }
    }
}
