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

use crate::volume::{Entry, Volume};

pub const INDEX_NAME: &str = "ampr_emu.index";
pub const MARKER: &str = "fakelib/libSceAmpr.sprx";

pub const PACK_INDEX: &str = "ampr_assets.index";
const MAGIC: &[u8; 8] = b"AMPRIDX3";
const VERSION: u32 = 3;
const RECORD: usize = 24;
const SLOT: usize = 16;
const HEADER: usize = 48;
const FNV_OFFSET: u64 = 1_469_598_103_934_665_603;
const FNV_PRIME: u64 = 1_099_511_628_211;

fn key_of(path: &str) -> String {
    path.replace('\\', "/").to_lowercase()
}

pub fn fnv1a64(path: &str) -> u64 {
    let mut h = FNV_OFFSET;
    for ch in key_of(path).chars() {
        h ^= ch as u32 as u64;
        h = h.wrapping_mul(FNV_PRIME);
    }
    if h == 0 {
        1
    } else {
        h
    }
}

fn slot_count(rows: usize) -> usize {
    if rows == 0 {
        return 0;
    }
    let mut n = 2usize;
    while n < rows * 2 {
        n <<= 1;
    }
    n
}

fn is_index(path: &str) -> bool {
    let k = key_of(path);
    k == INDEX_NAME || k == format!("{INDEX_NAME}.tmp")
}

pub fn build_index(entries: &[Entry]) -> Option<Vec<u8>> {
    let mut seen = std::collections::HashSet::new();
    let mut rows: Vec<(u64, i64, String)> = Vec::new();
    for e in entries.iter().filter(|e| !e.is_dir && !is_index(&e.path)) {
        let indexed = format!("/app0/{}", e.path);
        if seen.insert(key_of(&indexed)) {
            rows.push((e.size, e.mtime.unwrap_or(0), indexed));
        }
    }
    if rows.is_empty() {
        return None;
    }
    rows.sort_by(|a, b| key_of(&a.2).cmp(&key_of(&b.2)));

    let mut blob: Vec<u8> = Vec::new();
    let mut records: Vec<u8> = Vec::with_capacity(rows.len() * RECORD);
    for (size, mtime, path) in &rows {
        let off = blob.len() as u32;
        let len = path.len() as u32;
        records.extend_from_slice(&off.to_le_bytes());
        records.extend_from_slice(&len.to_le_bytes());
        records.extend_from_slice(&size.to_le_bytes());
        records.extend_from_slice(&mtime.to_le_bytes());
        blob.extend_from_slice(path.as_bytes());
        blob.push(0);
    }

    let n_slots = slot_count(rows.len());
    let mut slots: Vec<(u64, u32, u32)> = vec![(0, 0, 0); n_slots];
    let mask = n_slots - 1;
    for (i, (_, _, path)) in rows.iter().enumerate() {
        let h = fnv1a64(path);
        let mut pos = (h as usize) & mask;
        while slots[pos].1 != 0 {
            if slots[pos].0 == h {
                slots[pos].2 |= 1;
            }
            pos = (pos + 1) & mask;
        }
        slots[pos] = (h, i as u32 + 1, 0);
    }

    let path_end = HEADER + records.len() + blob.len();
    let hash_offset = (path_end + (SLOT - 1)) & !(SLOT - 1);

    let mut out = Vec::with_capacity(hash_offset + n_slots * SLOT);
    out.extend_from_slice(MAGIC);
    out.extend_from_slice(&VERSION.to_le_bytes());
    out.extend_from_slice(&(RECORD as u32).to_le_bytes());
    out.extend_from_slice(&(rows.len() as u64).to_le_bytes());
    out.extend_from_slice(&(blob.len() as u64).to_le_bytes());
    out.extend_from_slice(&(hash_offset as u64).to_le_bytes());
    out.extend_from_slice(&(SLOT as u32).to_le_bytes());
    out.extend_from_slice(&(n_slots as u32).to_le_bytes());
    out.extend_from_slice(&records);
    out.extend_from_slice(&blob);
    out.resize(hash_offset, 0);
    for (h, idx, flags) in slots {
        out.extend_from_slice(&h.to_le_bytes());
        out.extend_from_slice(&idx.to_le_bytes());
        out.extend_from_slice(&flags.to_le_bytes());
    }
    Some(out)
}

fn without_mtimes(idx: &mut [u8]) {
    if idx.len() < HEADER {
        return;
    }
    let rows = u64::from_le_bytes(idx[16..24].try_into().unwrap()) as usize;
    for i in 0..rows {
        let at = HEADER + i * RECORD + 16;
        if let Some(m) = idx.get_mut(at..at + 8) {
            m.fill(0);
        }
    }
}

pub fn index_is_current(vol: &dyn Volume) -> bool {
    let Some(i) = vol.find(INDEX_NAME) else { return false };
    let Some(mut want) = build_index(vol.entries()) else {
        return false;
    };
    let size = vol.entries()[i].size as usize;
    if size != want.len() {
        return false;
    }
    let Ok(r) = vol.open(i) else { return false };
    let Ok(mut have) = r.read_vec(0, size) else {
        return false;
    };
    without_mtimes(&mut want);
    without_mtimes(&mut have);
    have == want
}

#[cfg(test)]
mod tests {
    use super::*;

    fn e(p: &str, size: u64) -> Entry {
        Entry {
            path: p.into(),
            is_dir: false,
            size,
            mtime: Some(1_700_000_000),
        }
    }

    #[test]
    fn index_layout_and_hash_table() {
        let entries = vec![
            e("eboot.bin", 100),
            e("Sce_Sys/param.json", 20),
            e("ampr_emu.index", 1),
            e("a/b.bin", 5),
        ];
        let idx = build_index(&entries).unwrap();
        assert_eq!(&idx[0..8], b"AMPRIDX3");
        let rows = u64::from_le_bytes(idx[16..24].try_into().unwrap());
        assert_eq!(rows, 3);
        let blob_len = u64::from_le_bytes(idx[24..32].try_into().unwrap()) as usize;
        let hash_off = u64::from_le_bytes(idx[32..40].try_into().unwrap()) as usize;
        assert_eq!(hash_off % 16, 0);
        assert_eq!(HEADER + 3 * RECORD + blob_len <= hash_off, true);
        let slots = u32::from_le_bytes(idx[44..48].try_into().unwrap()) as usize;
        assert_eq!(slots, 8);
        assert_eq!(idx.len(), hash_off + slots * SLOT);

        let blob_start = HEADER + 3 * RECORD;
        for r in 0..3 {
            let rec = &idx[HEADER + r * RECORD..HEADER + (r + 1) * RECORD];
            let off = u32::from_le_bytes(rec[0..4].try_into().unwrap()) as usize;
            let len = u32::from_le_bytes(rec[4..8].try_into().unwrap()) as usize;
            let path = std::str::from_utf8(&idx[blob_start + off..blob_start + off + len]).unwrap();
            let h = fnv1a64(path);
            let mut pos = (h as usize) & (slots - 1);
            let mut found = false;
            for _ in 0..slots {
                let s = &idx[hash_off + pos * SLOT..hash_off + (pos + 1) * SLOT];
                let sh = u64::from_le_bytes(s[0..8].try_into().unwrap());
                let si = u32::from_le_bytes(s[8..12].try_into().unwrap());
                if si == r as u32 + 1 && sh == h {
                    found = true;
                    break;
                }
                pos = (pos + 1) & (slots - 1);
            }
            assert!(found, "{path}");
        }
    }

    #[test]
    fn fnv_matches_reference_basis_and_ignores_case() {
        assert_eq!(fnv1a64("/app0/A.BIN"), fnv1a64("/app0/a.bin"));
        assert_eq!(fnv1a64("/app0\\x"), fnv1a64("/app0/X"));

        let want = (1_469_598_103_934_665_603u128 ^ u128::from(b'a')).wrapping_mul(1_099_511_628_211) as u64;
        assert_eq!(fnv1a64("a"), want);
    }

    #[test]
    fn current_index_detection() {
        use crate::io::MemReader;
        use std::sync::Arc;

        struct V(Vec<Entry>, Vec<u8>);
        impl Volume for V {
            fn kind(&self) -> crate::volume::VolumeKind {
                crate::volume::VolumeKind::Folder
            }
            fn entries(&self) -> &[Entry] {
                &self.0
            }
            fn open(&self, _: usize) -> crate::Result<Arc<dyn crate::io::ReadAt>> {
                Ok(Arc::new(MemReader::new(self.1.clone())))
            }
        }
        let files = vec![e("eboot.bin", 100), e("a/b.bin", 5)];
        let idx = build_index(&files).unwrap();
        let mut with_idx = files.clone();
        with_idx.push(e(INDEX_NAME, idx.len() as u64));

        let mut shifted = with_idx.clone();
        shifted[0].mtime = Some(1_700_000_002);
        assert!(index_is_current(&V(shifted, idx.clone())));

        let mut changed = with_idx.clone();
        changed[1].size = 6;
        assert!(!index_is_current(&V(changed, idx.clone())));

        let mut more = with_idx.clone();
        more.push(e("new.bin", 1));
        assert!(!index_is_current(&V(more, idx.clone())));

        assert!(!index_is_current(&V(files, Vec::new())));
    }

    #[test]
    fn packed_games_keep_their_index() {
        use crate::convert::{ampr_needed, AmprMode};
        use crate::io::MemReader;
        use std::sync::Arc;

        struct V(Vec<Entry>);
        impl Volume for V {
            fn kind(&self) -> crate::volume::VolumeKind {
                crate::volume::VolumeKind::Folder
            }
            fn entries(&self) -> &[Entry] {
                &self.0
            }
            fn open(&self, _: usize) -> crate::Result<Arc<dyn crate::io::ReadAt>> {
                Ok(Arc::new(MemReader::new(vec![0u8; 16])))
            }
        }

        let plain = vec![e("eboot.bin", 100), e(MARKER, 10), e(INDEX_NAME, 16)];
        assert!(ampr_needed(&V(plain.clone()), AmprMode::Auto));

        let mut packed = plain;
        packed.push(e(PACK_INDEX, 32));
        packed.push(e("ampr_assets-001.pak", 4096));
        assert!(!ampr_needed(&V(packed.clone()), AmprMode::Auto));

        assert!(ampr_needed(&V(packed), AmprMode::Always));
    }

    #[test]
    fn empty_gives_none() {
        assert!(build_index(&[]).is_none());
    }
}
