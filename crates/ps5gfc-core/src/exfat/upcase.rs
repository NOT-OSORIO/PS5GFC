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

use std::sync::OnceLock;

pub const UPCASE_COMPRESSED: &[u8] = include_bytes!("../data/exfat_upcase.bin");
pub const UPCASE_CHECKSUM: u32 = 0xE619_D30D;

static EXPANDED: OnceLock<Vec<u16>> = OnceLock::new();

fn expanded() -> &'static Vec<u16> {
    EXPANDED.get_or_init(|| {
        let mut out: Vec<u16> = Vec::with_capacity(65536);
        let words: Vec<u16> = UPCASE_COMPRESSED
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect();
        let mut i = 0;
        while i < words.len() && out.len() < 65536 {
            if words[i] == 0xFFFF && i + 1 < words.len() {
                let n = words[i + 1] as usize;
                for _ in 0..n {
                    let c = out.len() as u16;
                    out.push(c);
                }
                i += 2;
            } else {
                out.push(words[i]);
                i += 1;
            }
        }
        while out.len() < 65536 {
            let c = out.len() as u16;
            out.push(c);
        }
        out
    })
}

#[inline]
pub fn upcase_unit(c: u16) -> u16 {
    expanded()[c as usize]
}

pub fn name_hash(units: &[u16]) -> u16 {
    let mut h: u16 = 0;
    for &u in units {
        let up = upcase_unit(u);
        for b in up.to_le_bytes() {
            h = h.rotate_right(1).wrapping_add(b as u16);
        }
    }
    h
}

pub fn fold_key(units: &[u16]) -> Vec<u16> {
    units.iter().map(|&u| upcase_unit(u)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn table_checksum(bytes: &[u8]) -> u32 {
        let mut cs: u32 = 0;
        for &b in bytes {
            cs = cs.rotate_right(1).wrapping_add(b as u32);
        }
        cs
    }

    #[test]
    fn checksum_matches_spec() {
        assert_eq!(UPCASE_COMPRESSED.len(), 5836);
        assert_eq!(table_checksum(UPCASE_COMPRESSED), UPCASE_CHECKSUM);
    }

    #[test]
    fn ascii_and_latin_mapping() {
        assert_eq!(upcase_unit('a' as u16), 'A' as u16);
        assert_eq!(upcase_unit('z' as u16), 'Z' as u16);
        assert_eq!(upcase_unit('A' as u16), 'A' as u16);
        assert_eq!(upcase_unit('é' as u16), 'É' as u16);
        assert_eq!(upcase_unit('1' as u16), '1' as u16);
        assert_eq!(expanded().len(), 65536);
    }

    #[test]
    fn hash_is_case_insensitive() {
        let a: Vec<u16> = "eboot.bin".encode_utf16().collect();
        let b: Vec<u16> = "EBOOT.BIN".encode_utf16().collect();
        assert_eq!(name_hash(&a), name_hash(&b));
    }
}
