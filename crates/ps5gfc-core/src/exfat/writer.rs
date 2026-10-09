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

use super::upcase::{fold_key, name_hash, UPCASE_CHECKSUM, UPCASE_COMPRESSED};
use crate::image::{ExtentBuilder, ExtentImage};
use crate::tree::Tree;
use crate::util::{align_up, ceil_div};
use crate::volume::Volume;
use crate::{Error, Result};

pub const BYTES_PER_SECTOR: u64 = 512;
const FAT_OFFSET_SECTORS: u64 = 128;
const NAME_CHARS_PER_ENTRY: usize = 15;
const FIRST_CLUSTER: u32 = 2;
const ATTR_DIRECTORY: u16 = 0x10;
const ATTR_ARCHIVE: u16 = 0x20;
pub const DEFAULT_SERIAL: u32 = 0x4D6B_5046;

#[derive(Debug, Clone)]
pub struct ExfatOptions {
    pub cluster_size: u32,

    pub label: String,

    pub preserve_times: bool,

    pub free_bytes: u64,
    pub volume_serial: u32,
}

impl Default for ExfatOptions {
    fn default() -> Self {
        Self {
            cluster_size: 65536,
            label: String::new(),
            preserve_times: true,
            free_bytes: 0,
            volume_serial: DEFAULT_SERIAL,
        }
    }
}

pub struct ExfatPlan {
    pub image: ExtentImage,
    pub cluster_size: u32,
    pub cluster_count: u32,
    pub files: u64,
    pub dirs: u64,
    pub data_bytes: u64,
}

fn dos_timestamp(unix: i64) -> u32 {
    let days = unix.div_euclid(86400);
    let secs = unix.rem_euclid(86400) as u32;
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = if m <= 2 { y + 1 } else { y };
    let year = year.clamp(1980, 2107) as u32;
    ((year - 1980) << 25)
        | (m << 21)
        | (d << 16)
        | ((secs / 3600) << 11)
        | (((secs / 60) % 60) << 5)
        | ((secs % 60) / 2)
}

fn fixed_timestamp() -> u32 {
    (2024 - 1980) << 25 | 1 << 21 | 1 << 16
}

fn entry_set_checksum(data: &[u8]) -> u16 {
    let mut cs: u16 = 0;
    for (i, &b) in data.iter().enumerate() {
        if i == 2 || i == 3 {
            continue;
        }
        cs = cs.rotate_right(1).wrapping_add(b as u16);
    }
    cs
}

fn boot_checksum(region: &[u8]) -> u32 {
    let mut cs: u32 = 0;
    for (i, &b) in region.iter().enumerate() {
        if i == 106 || i == 107 || i == 112 {
            continue;
        }
        cs = cs.rotate_right(1).wrapping_add(b as u32);
    }
    cs
}

struct NodeInfo {
    name16: Vec<u16>,
    first_cluster: u32,
    clusters: u32,
    entry_count: usize,
}

pub fn plan(volume: Arc<dyn Volume>, opts: &ExfatOptions) -> Result<ExfatPlan> {
    let cs = opts.cluster_size as u64;
    if !(512..=32 * 1024 * 1024).contains(&cs) || !cs.is_power_of_two() {
        return Err(Error::invalid(
            "exFAT cluster size must be a power of 2 between 512 B and 32 MiB",
        ));
    }
    let spc = cs / BYTES_PER_SECTOR;
    let spc_shift = spc.trailing_zeros() as u8;

    let tree = Tree::build(volume.entries());
    let n = tree.nodes.len();

    let mut info: Vec<NodeInfo> = (0..n)
        .map(|_| NodeInfo {
            name16: Vec::new(),
            first_cluster: 0,
            clusters: 0,
            entry_count: 0,
        })
        .collect();
    for i in 1..n {
        let name16: Vec<u16> = tree.nodes[i].name.encode_utf16().collect();
        if name16.is_empty() || name16.len() > 255 {
            return Err(Error::invalid(format!(
                "nome inválido para exFAT (vazio ou > 255 caracteres): {:?}",
                tree.path_of(i)
            )));
        }
        info[i].name16 = name16;
    }
    for i in 0..n {
        if !tree.nodes[i].is_dir {
            continue;
        }
        let mut seen: HashSet<Vec<u16>> = HashSet::new();
        for &c in &tree.nodes[i].children {
            if !seen.insert(fold_key(&info[c].name16)) {
                return Err(Error::invalid(format!(
                    "dois itens só diferem por maiúsculas/minúsculas (exFAT não diferencia): {:?}",
                    tree.path_of(c)
                )));
            }
        }
    }

    let upcase_len = UPCASE_COMPRESSED.len() as u64;
    let upcase_clusters = ceil_div(upcase_len, cs) as u32;

    let mut content_clusters: u64 = upcase_clusters as u64;
    for i in 0..n {
        let node = &tree.nodes[i];
        if node.is_dir {
            let mut cnt = if i == 0 { 3 } else { 0 };
            for &c in &node.children {
                cnt += 2 + ceil_div(info[c].name16.len() as u64, NAME_CHARS_PER_ENTRY as u64) as usize;
            }
            info[i].entry_count = cnt;
            let bytes = cnt as u64 * 32;
            if bytes > 256 * 1024 * 1024 {
                return Err(Error::invalid(format!(
                    "directory too large for exFAT: {:?}",
                    tree.path_of(i)
                )));
            }
            info[i].clusters = ceil_div(bytes, cs).max(1) as u32;
        } else {
            info[i].clusters = ceil_div(node.size, cs) as u32;
        }
        content_clusters += info[i].clusters as u64;
    }

    let free_clusters = ceil_div(opts.free_bytes, cs);

    let mut bitmap_clusters: u64 = 1;
    loop {
        let total = bitmap_clusters + content_clusters + free_clusters;
        let need = ceil_div(ceil_div(total, 8), cs);
        if need == bitmap_clusters {
            break;
        }
        bitmap_clusters = need;
    }
    let used_clusters = bitmap_clusters + content_clusters;
    let cluster_count = used_clusters + free_clusters;
    if cluster_count > 0xFFFF_FFF0 - FIRST_CLUSTER as u64 {
        return Err(Error::invalid("exFAT volume exceeds the cluster limit"));
    }

    let bitmap_first = FIRST_CLUSTER;
    let upcase_first = bitmap_first + bitmap_clusters as u32;
    let mut next = upcase_first + upcase_clusters;
    info[0].first_cluster = next;
    next += info[0].clusters;
    for i in tree.preorder() {
        if info[i].clusters > 0 {
            info[i].first_cluster = next;
            next += info[i].clusters;
        }
    }
    debug_assert_eq!(next as u64 - FIRST_CLUSTER as u64, used_clusters);

    let fat_entries = cluster_count + FIRST_CLUSTER as u64;
    let fat_len_sectors = align_up(ceil_div(fat_entries * 4, BYTES_PER_SECTOR), spc);
    let heap_offset = align_up(FAT_OFFSET_SECTORS + fat_len_sectors, spc);
    let volume_len_sectors = heap_offset + cluster_count * spc;
    let volume_bytes = volume_len_sectors * BYTES_PER_SECTOR;
    let cluster_off = |c: u32| -> u64 { (heap_offset + (c as u64 - FIRST_CLUSTER as u64) * spc) * BYTES_PER_SECTOR };

    let mut eb = ExtentBuilder::new();

    let boot = build_boot_region(
        volume_len_sectors,
        FAT_OFFSET_SECTORS as u32,
        fat_len_sectors as u32,
        heap_offset as u32,
        cluster_count as u32,
        info[0].first_cluster,
        spc_shift,
        opts.volume_serial,
    );
    let mut boot2 = Vec::with_capacity(boot.len() * 2);
    boot2.extend_from_slice(&boot);
    boot2.extend_from_slice(&boot);
    eb.meta_vec(0, boot2);

    let mut fat = vec![0u8; (fat_len_sectors * BYTES_PER_SECTOR) as usize];
    fat[0..4].copy_from_slice(&0xFFFF_FFF8u32.to_le_bytes());
    fat[4..8].copy_from_slice(&0xFFFF_FFFFu32.to_le_bytes());
    let mut chain = |first: u32, count: u32| {
        if count == 0 {
            return;
        }
        for k in 0..count - 1 {
            let at = (first + k) as usize * 4;
            fat[at..at + 4].copy_from_slice(&(first + k + 1).to_le_bytes());
        }
        let at = (first + count - 1) as usize * 4;
        fat[at..at + 4].copy_from_slice(&0xFFFF_FFFFu32.to_le_bytes());
    };
    chain(bitmap_first, bitmap_clusters as u32);
    chain(upcase_first, upcase_clusters);
    chain(info[0].first_cluster, info[0].clusters);
    for i in tree.preorder() {
        if info[i].first_cluster >= FIRST_CLUSTER {
            chain(info[i].first_cluster, info[i].clusters);
        }
    }
    eb.meta_vec(FAT_OFFSET_SECTORS * BYTES_PER_SECTOR, fat);

    let bitmap_bytes = ceil_div(cluster_count, 8) as usize;
    let mut bitmap = vec![0u8; bitmap_bytes];
    for bit in 0..used_clusters as usize {
        bitmap[bit / 8] |= 1 << (bit % 8);
    }
    eb.meta_vec(cluster_off(bitmap_first), bitmap);

    eb.meta_vec(cluster_off(upcase_first), UPCASE_COMPRESSED.to_vec());

    let fixed_ts = fixed_timestamp();
    let ts_of = |t: Option<i64>| -> u32 {
        match (opts.preserve_times, t) {
            (true, Some(s)) => dos_timestamp(s),
            _ => fixed_ts,
        }
    };

    let label_units: Vec<u16> = opts.label.encode_utf16().take(11).collect();
    let mut files = 0u64;
    let mut dirs = 0u64;
    let mut data_bytes = 0u64;

    for i in 0..n {
        let node = &tree.nodes[i];
        if node.is_dir {
            if i != 0 {
                dirs += 1;
            }
            let mut blob: Vec<u8> = Vec::with_capacity(info[i].entry_count * 32);
            if i == 0 {
                let mut label = [0u8; 32];
                label[0] = 0x83;
                label[1] = label_units.len() as u8;
                for (k, u) in label_units.iter().enumerate() {
                    label[2 + k * 2..4 + k * 2].copy_from_slice(&u.to_le_bytes());
                }
                blob.extend_from_slice(&label);

                let mut bm = [0u8; 32];
                bm[0] = 0x81;
                bm[0x14..0x18].copy_from_slice(&bitmap_first.to_le_bytes());
                bm[0x18..0x20].copy_from_slice(&(bitmap_bytes as u64).to_le_bytes());
                blob.extend_from_slice(&bm);

                let mut up = [0u8; 32];
                up[0] = 0x82;
                up[4..8].copy_from_slice(&UPCASE_CHECKSUM.to_le_bytes());
                up[0x14..0x18].copy_from_slice(&upcase_first.to_le_bytes());
                up[0x18..0x20].copy_from_slice(&upcase_len.to_le_bytes());
                blob.extend_from_slice(&up);
            }
            for &c in &tree.nodes[i].children {
                push_entry_set(&mut blob, &tree, &info, c, cs, &ts_of);
            }
            let start = if i == 0 {
                cluster_off(info[0].first_cluster)
            } else {
                cluster_off(info[i].first_cluster)
            };
            eb.meta_vec(start, blob);
        } else {
            files += 1;
            data_bytes += node.size;
            if node.size > 0 {
                let entry = node.entry.expect("arquivo sem entrada de origem");
                eb.file(cluster_off(info[i].first_cluster), entry, node.size);
            }
        }
    }

    let image = eb.finish(volume_bytes, volume);
    Ok(ExfatPlan {
        image,
        cluster_size: opts.cluster_size,
        cluster_count: cluster_count as u32,
        files,
        dirs,
        data_bytes,
    })
}

fn push_entry_set(
    out: &mut Vec<u8>,
    tree: &Tree,
    info: &[NodeInfo],
    idx: usize,
    cluster_size: u64,
    ts_of: &dyn Fn(Option<i64>) -> u32,
) {
    let node = &tree.nodes[idx];
    let ni = &info[idx];
    let name_entries = ceil_div(ni.name16.len() as u64, NAME_CHARS_PER_ENTRY as u64) as usize;
    let secondary = 1 + name_entries;

    let data_length: u64 = if node.is_dir {
        ni.clusters as u64 * cluster_size
    } else {
        node.size
    };
    let has_alloc = ni.first_cluster >= FIRST_CLUSTER && ni.clusters > 0;
    let attrs = if node.is_dir { ATTR_DIRECTORY } else { ATTR_ARCHIVE };
    let ts = ts_of(node.mtime);

    let mut set = vec![0u8; 32 * (2 + name_entries)];

    set[0] = 0x85;
    set[1] = secondary as u8;
    set[4..6].copy_from_slice(&attrs.to_le_bytes());
    set[8..12].copy_from_slice(&ts.to_le_bytes());
    set[12..16].copy_from_slice(&ts.to_le_bytes());
    set[16..20].copy_from_slice(&ts.to_le_bytes());

    let s = 32;
    set[s] = 0xC0;
    set[s + 1] = if has_alloc { 0x01 } else { 0x00 };
    set[s + 3] = ni.name16.len() as u8;
    set[s + 4..s + 6].copy_from_slice(&name_hash(&ni.name16).to_le_bytes());
    set[s + 8..s + 16].copy_from_slice(&data_length.to_le_bytes());
    let fc = if has_alloc { ni.first_cluster } else { 0 };
    set[s + 0x14..s + 0x18].copy_from_slice(&fc.to_le_bytes());
    set[s + 0x18..s + 0x20].copy_from_slice(&data_length.to_le_bytes());

    for k in 0..name_entries {
        let base = 64 + k * 32;
        set[base] = 0xC1;
        for (j, u) in ni
            .name16
            .iter()
            .skip(k * NAME_CHARS_PER_ENTRY)
            .take(NAME_CHARS_PER_ENTRY)
            .enumerate()
        {
            set[base + 2 + j * 2..base + 4 + j * 2].copy_from_slice(&u.to_le_bytes());
        }
    }
    let cs16 = entry_set_checksum(&set);
    set[2..4].copy_from_slice(&cs16.to_le_bytes());
    out.extend_from_slice(&set);
}

#[allow(clippy::too_many_arguments)]
fn build_boot_region(
    volume_len_sectors: u64,
    fat_offset: u32,
    fat_length: u32,
    heap_offset: u32,
    cluster_count: u32,
    root_cluster: u32,
    spc_shift: u8,
    serial: u32,
) -> Vec<u8> {
    let bps = BYTES_PER_SECTOR as usize;
    let mut region = vec![0u8; bps * 12];
    {
        let vbr = &mut region[0..bps];
        vbr[0..3].copy_from_slice(&[0xEB, 0x76, 0x90]);
        vbr[3..11].copy_from_slice(b"EXFAT   ");

        vbr[72..80].copy_from_slice(&volume_len_sectors.to_le_bytes());
        vbr[80..84].copy_from_slice(&fat_offset.to_le_bytes());
        vbr[84..88].copy_from_slice(&fat_length.to_le_bytes());
        vbr[88..92].copy_from_slice(&heap_offset.to_le_bytes());
        vbr[92..96].copy_from_slice(&cluster_count.to_le_bytes());
        vbr[96..100].copy_from_slice(&root_cluster.to_le_bytes());
        vbr[100..104].copy_from_slice(&serial.to_le_bytes());
        vbr[104..106].copy_from_slice(&0x0100u16.to_le_bytes());
        vbr[106..108].copy_from_slice(&0u16.to_le_bytes());
        vbr[108] = 9;
        vbr[109] = spc_shift;
        vbr[110] = 1;
        vbr[111] = 0x80;
        vbr[112] = 0xFF;
        vbr[510..512].copy_from_slice(&0xAA55u16.to_le_bytes());
    }

    for s in 1..=8 {
        let at = s * bps + bps - 4;
        region[at..at + 4].copy_from_slice(&0xAA55_0000u32.to_le_bytes());
    }

    let cs = boot_checksum(&region[0..bps * 11]);
    for k in 0..bps / 4 {
        let at = bps * 11 + k * 4;
        region[at..at + 4].copy_from_slice(&cs.to_le_bytes());
    }
    region
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dos_timestamp_known_values() {
        assert_eq!(dos_timestamp(1_704_067_200), fixed_timestamp());

        assert_eq!(dos_timestamp(315_532_800), (1 << 21) | (1 << 16));

        assert_eq!(dos_timestamp(0), (1 << 21) | (1 << 16));
    }

    #[test]
    fn boot_region_checksum_sector() {
        let r = build_boot_region(1000, 128, 128, 256, 100, 5, 7, DEFAULT_SERIAL);
        assert_eq!(r.len(), 12 * 512);
        let cs = boot_checksum(&r[..11 * 512]);
        assert_eq!(&r[11 * 512..11 * 512 + 4], &cs.to_le_bytes());
        assert_eq!(&r[3..11], b"EXFAT   ");
    }
}
