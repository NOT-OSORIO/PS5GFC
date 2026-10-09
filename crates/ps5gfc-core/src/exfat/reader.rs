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

use std::io;
use std::sync::Arc;

use crate::io::ReadAt;
use crate::volume::{Entry, Volume, VolumeKind};
use crate::{format_err, Error, Result};

pub const EXFAT_SIGNATURE: &[u8; 8] = b"EXFAT   ";

const FAT_EOC_MIN: u32 = 0xFFFF_FFF8;
const FLAG_NO_FAT_CHAIN: u8 = 0x02;
const ATTR_DIRECTORY: u16 = 0x10;
const MAX_FAT_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Debug, Clone)]
pub struct Geometry {
    pub bytes_per_sector: u32,
    pub sectors_per_cluster: u32,
    pub fat_offset: u64,
    pub heap_offset: u64,
    pub cluster_count: u32,
    pub root_cluster: u32,
    pub volume_length: u64,
    pub serial: u32,
}

impl Geometry {
    pub fn cluster_size(&self) -> u64 {
        self.bytes_per_sector as u64 * self.sectors_per_cluster as u64
    }
    fn cluster_off(&self, c: u32) -> u64 {
        self.heap_offset + (c as u64 - 2) * self.cluster_size()
    }
}

pub fn is_exfat(head: &[u8]) -> bool {
    head.len() >= 11 && &head[3..11] == EXFAT_SIGNATURE
}

struct FileMeta {
    first_cluster: u32,
    size: u64,
    no_fat_chain: bool,
}

pub struct ExfatVolume {
    img: Arc<dyn ReadAt>,
    geo: Geometry,
    fat: Arc<Vec<u32>>,
    entries: Vec<Entry>,
    meta: Vec<FileMeta>,
    label: String,
}

fn parse_geometry(img: &dyn ReadAt) -> Result<Geometry> {
    let mut vbr = [0u8; 512];
    img.read_exact_at(0, &mut vbr)
        .map_err(|_| format_err!("image too small for an exFAT boot sector"))?;
    if !is_exfat(&vbr) {
        return Err(format_err!("exFAT signature missing"));
    }
    if u16::from_le_bytes([vbr[510], vbr[511]]) != 0xAA55 {
        return Err(format_err!("boot signature 0xAA55 missing"));
    }
    let u32at = |o: usize| u32::from_le_bytes(vbr[o..o + 4].try_into().unwrap());
    let u64at = |o: usize| u64::from_le_bytes(vbr[o..o + 8].try_into().unwrap());
    let bps_shift = vbr[108];
    let spc_shift = vbr[109];
    if !(9..=12).contains(&bps_shift) {
        return Err(format_err!("unsupported exFAT sector size (2^{bps_shift})"));
    }
    if spc_shift > 25 - bps_shift {
        return Err(format_err!("invalid sectors per cluster (2^{spc_shift})"));
    }
    if vbr[110] != 1 {
        return Err(Error::unsupported(
            "exFAT with more than one FAT (TexFAT) is not supported",
        ));
    }
    let bps = 1u64 << bps_shift;
    Ok(Geometry {
        bytes_per_sector: bps as u32,
        sectors_per_cluster: 1 << spc_shift,
        fat_offset: u32at(80) as u64 * bps,
        heap_offset: u32at(88) as u64 * bps,
        cluster_count: u32at(92),
        root_cluster: u32at(96),
        volume_length: u64at(72) * bps,
        serial: u32at(100),
    })
}

fn unix_from_dos(ts: u32) -> Option<i64> {
    let year = 1980 + (ts >> 25) as i64;
    let month = ((ts >> 21) & 0xF) as i64;
    let day = ((ts >> 16) & 0x1F) as i64;
    if month == 0 || day == 0 || month > 12 {
        return None;
    }
    let hour = ((ts >> 11) & 0x1F) as i64;
    let min = ((ts >> 5) & 0x3F) as i64;
    let sec = ((ts & 0x1F) * 2) as i64;

    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let mp = if month > 2 { month - 3 } else { month + 9 };
    let doy = (153 * mp + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146_097 + doe - 719_468;
    Some(days * 86400 + hour * 3600 + min * 60 + sec)
}

impl ExfatVolume {
    pub fn open(img: Arc<dyn ReadAt>) -> Result<Self> {
        Self::open_with(img, None)
    }

    pub fn open_with(img: Arc<dyn ReadAt>, ctx: Option<&crate::ctl::Ctx>) -> Result<Self> {
        let geo = parse_geometry(img.as_ref())?;
        let cs = geo.cluster_size();
        let fat_bytes = (geo.cluster_count as u64 + 2) * 4;
        if fat_bytes > MAX_FAT_BYTES {
            return Err(Error::unsupported("exFAT volume too large (FAT > 512 MiB)"));
        }
        let mut raw = vec![0u8; fat_bytes as usize];
        img.read_exact_at(geo.fat_offset, &mut raw)
            .map_err(|e| format_err!("unreadable FAT: {e}"))?;
        let fat: Vec<u32> = raw
            .chunks_exact(4)
            .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
            .collect();
        drop(raw);

        let mut vol = ExfatVolume {
            img,
            geo,
            fat: Arc::new(fat),
            entries: Vec::new(),
            meta: Vec::new(),
            label: String::new(),
        };

        let root_chain = vol.cluster_list(vol.geo.root_cluster, 0, false)?;
        let mut stack: Vec<(Vec<u32>, String)> = vec![(root_chain, String::new())];
        let mut cluster_buf = vec![0u8; cs as usize];
        let mut seen_dirs = 0u64;
        while let Some((chain, prefix)) = stack.pop() {
            if let Some(c) = ctx {
                c.cancel.check()?;
            }
            seen_dirs += 1;
            if seen_dirs > 5_000_000 {
                return Err(format_err!("corrupted exFAT directory structure"));
            }

            let mut dir = Vec::with_capacity(chain.len() * cs as usize);
            for &c in &chain {
                vol.img
                    .read_exact_at(vol.geo.cluster_off(c), &mut cluster_buf)
                    .map_err(|e| format_err!("unreadable directory (cluster {c}): {e}"))?;
                dir.extend_from_slice(&cluster_buf);
            }
            let mut pos = 0usize;
            while pos + 32 <= dir.len() {
                let t = dir[pos];
                if t == 0x00 {
                    break;
                }
                match t {
                    0x83 => {
                        let n = dir[pos + 1] as usize;
                        let units: Vec<u16> = (0..n.min(11))
                            .map(|k| u16::from_le_bytes([dir[pos + 2 + k * 2], dir[pos + 3 + k * 2]]))
                            .collect();
                        if prefix.is_empty() {
                            vol.label = String::from_utf16_lossy(&units);
                        }
                        pos += 32;
                    }
                    0x85 => {
                        let sec = dir[pos + 1] as usize;
                        if sec < 2 || pos + 32 * (1 + sec) > dir.len() {
                            pos += 32;
                            continue;
                        }
                        let attrs = u16::from_le_bytes([dir[pos + 4], dir[pos + 5]]);
                        let mtime_raw = u32::from_le_bytes(dir[pos + 12..pos + 16].try_into().unwrap());
                        let st = pos + 32;
                        if dir[st] != 0xC0 {
                            pos += 32 * (1 + sec);
                            continue;
                        }
                        let flags = dir[st + 1];
                        let name_len = dir[st + 3] as usize;
                        let first_cluster = u32::from_le_bytes(dir[st + 0x14..st + 0x18].try_into().unwrap());
                        let valid_len = u64::from_le_bytes(dir[st + 8..st + 16].try_into().unwrap());
                        let data_len = u64::from_le_bytes(dir[st + 0x18..st + 0x20].try_into().unwrap());
                        let mut units: Vec<u16> = Vec::with_capacity(name_len);
                        for k in 0..sec - 1 {
                            let at = pos + 64 + k * 32;
                            if dir[at] != 0xC1 {
                                break;
                            }
                            for j in 0..15 {
                                units.push(u16::from_le_bytes([dir[at + 2 + j * 2], dir[at + 3 + j * 2]]));
                            }
                        }
                        units.truncate(name_len);
                        let name = String::from_utf16_lossy(&units);
                        let path = if prefix.is_empty() {
                            name.clone()
                        } else {
                            format!("{prefix}/{name}")
                        };
                        let is_dir = attrs & ATTR_DIRECTORY != 0;
                        let no_fat_chain = flags & FLAG_NO_FAT_CHAIN != 0;
                        let size = if is_dir { 0 } else { data_len };
                        let _ = valid_len;
                        vol.entries.push(Entry {
                            path: path.clone(),
                            is_dir,
                            size,
                            mtime: unix_from_dos(mtime_raw),
                        });
                        vol.meta.push(FileMeta {
                            first_cluster,
                            size: data_len,
                            no_fat_chain,
                        });
                        if let Some(c) = ctx {
                            c.progress.add_files_done(1);
                            if vol.entries.len() % 256 == 0 {
                                c.progress.set_current(&vol.entries[vol.entries.len() - 1].path);
                            }
                        }
                        if is_dir {
                            let chain = vol.cluster_list(first_cluster, data_len, no_fat_chain)?;
                            stack.push((chain, path));
                        }
                        pos += 32 * (1 + sec);
                    }
                    _ => pos += 32,
                }
            }
        }

        let mut order: Vec<usize> = (0..vol.entries.len()).collect();
        order.sort_by(|&a, &b| vol.entries[a].path.cmp(&vol.entries[b].path));
        let entries: Vec<Entry> = order.iter().map(|&i| vol.entries[i].clone()).collect();
        let meta: Vec<FileMeta> = order
            .iter()
            .map(|&i| FileMeta {
                first_cluster: vol.meta[i].first_cluster,
                size: vol.meta[i].size,
                no_fat_chain: vol.meta[i].no_fat_chain,
            })
            .collect();
        vol.entries = entries;
        vol.meta = meta;
        Ok(vol)
    }

    pub fn geometry(&self) -> &Geometry {
        &self.geo
    }

    fn cluster_list(&self, first: u32, length: u64, no_fat_chain: bool) -> Result<Vec<u32>> {
        if first < 2 {
            return Ok(Vec::new());
        }
        let cs = self.geo.cluster_size();
        let max = self.geo.cluster_count as u64 + 2;
        if no_fat_chain {
            let count = length.div_ceil(cs);
            if first as u64 + count > max {
                return Err(format_err!("contiguous allocation outside the volume"));
            }
            return Ok((first..first + count as u32).collect());
        }
        let want = if length > 0 { Some(length.div_ceil(cs)) } else { None };
        let mut out = Vec::new();
        let mut c = first;
        while (2..FAT_EOC_MIN).contains(&c) {
            if c as u64 >= max {
                return Err(format_err!("cluster {c} outside the volume"));
            }
            out.push(c);
            if let Some(w) = want {
                if out.len() as u64 >= w {
                    break;
                }
            }
            if out.len() as u64 > max {
                return Err(format_err!("loop in the cluster chain"));
            }
            c = self.fat[c as usize];
        }
        if let Some(w) = want {
            if (out.len() as u64) < w {
                return Err(format_err!("cluster chain shorter than the file size"));
            }
        }
        Ok(out)
    }

    pub fn label_str(&self) -> &str {
        &self.label
    }

    pub fn image(&self) -> &Arc<dyn ReadAt> {
        &self.img
    }
}

impl Volume for ExfatVolume {
    fn kind(&self) -> VolumeKind {
        VolumeKind::Exfat
    }
    fn entries(&self) -> &[Entry] {
        &self.entries
    }
    fn label(&self) -> String {
        self.label.clone()
    }
    fn describe(&self) -> Vec<(String, String)> {
        let g = &self.geo;
        vec![
            ("cluster".into(), format!("{} KiB", g.cluster_size() / 1024)),
            ("sector".into(), format!("{} B", g.bytes_per_sector)),
            ("clusters".into(), g.cluster_count.to_string()),
            ("volume".into(), crate::util::human_bytes(g.volume_length)),
            ("serial".into(), format!("{:08X}", g.serial)),
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
        let m = &self.meta[index];
        let clusters = self.cluster_list(m.first_cluster, m.size, m.no_fat_chain)?;

        let cs = self.geo.cluster_size();
        let mut runs: Vec<Run> = Vec::new();
        let mut logical = 0u64;
        for &c in &clusters {
            let disk = self.geo.cluster_off(c);
            match runs.last_mut() {
                Some(r) if r.disk + r.len == disk => r.len += cs,
                _ => runs.push(Run { logical, disk, len: cs }),
            }
            logical += cs;
        }
        Ok(Arc::new(ExfatFile {
            img: self.img.clone(),
            runs,
            size: m.size,
        }))
    }
}

struct Run {
    logical: u64,
    disk: u64,
    len: u64,
}

struct ExfatFile {
    img: Arc<dyn ReadAt>,
    runs: Vec<Run>,
    size: u64,
}

impl ReadAt for ExfatFile {
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
        let mut i = self.runs.partition_point(|r| r.logical + r.len <= pos);
        while done < want {
            let Some(r) = self.runs.get(i) else {
                return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "arquivo exFAT truncado"));
            };
            let within = pos - r.logical;
            let n = ((want - done) as u64).min(r.len - within) as usize;
            self.img.read_exact_at(r.disk + within, &mut buf[done..done + n])?;
            done += n;
            pos += n as u64;
            i += 1;
        }
        Ok(want)
    }
}
