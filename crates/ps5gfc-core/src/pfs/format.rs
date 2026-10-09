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

use crate::{format_err, Result};

pub const PFS_MAGIC: i64 = 20_130_315;
pub const VERSION_PS4: i64 = 1;
pub const VERSION_PS5: i64 = 2;

pub const MODE_SIGNED: u16 = 0x1;
pub const MODE_64BIT_INODES: u16 = 0x2;
pub const MODE_ENCRYPTED: u16 = 0x4;
pub const MODE_CASE_INSENSITIVE: u16 = 0x8;

pub const INODE_MODE_DIR: u16 = 0x4000;
pub const INODE_MODE_FILE: u16 = 0x8000;

pub const INODE_RX_ONLY: u16 = 0x001 | 0x004 | 0x008 | 0x020 | 0x040 | 0x100;

pub const FLAG_COMPRESSED: u32 = 0x1;
pub const FLAG_READONLY: u32 = 0x10;
pub const FLAG_INTERNAL: u32 = 0x20000;

pub const DIRENT_FILE: i32 = 2;
pub const DIRENT_DIR: i32 = 3;
pub const DIRENT_DOT: i32 = 4;
pub const DIRENT_DOTDOT: i32 = 5;

pub const INODE_D32_SIZE: usize = 0xA8;
pub const DEFAULT_BLOCK: u32 = 65536;

#[derive(Debug, Clone, Default)]
pub struct Inode {
    pub mode: u16,
    pub nlink: u16,
    pub flags: u32,
    pub size: i64,
    pub size_compressed: i64,
    pub blocks: u32,
    pub db: [i32; 12],
    pub ib: [i32; 5],
    pub time: i64,
}

impl Inode {
    pub fn is_dir(&self) -> bool {
        self.mode & INODE_MODE_DIR != 0
    }
    pub fn is_file(&self) -> bool {
        self.mode & INODE_MODE_FILE != 0
    }
    pub fn is_compressed(&self) -> bool {
        self.flags & FLAG_COMPRESSED != 0
    }

    pub fn stored_size(&self) -> u64 {
        (if self.is_compressed() {
            self.size
        } else {
            self.size_compressed
        })
        .max(0) as u64
    }

    pub fn logical_size(&self) -> u64 {
        (if self.is_compressed() {
            self.size_compressed
        } else {
            self.size
        })
        .max(0) as u64
    }

    pub fn to_bytes(&self) -> [u8; INODE_D32_SIZE] {
        let mut o = [0u8; INODE_D32_SIZE];
        o[0..2].copy_from_slice(&self.mode.to_le_bytes());
        o[2..4].copy_from_slice(&self.nlink.to_le_bytes());
        o[4..8].copy_from_slice(&self.flags.to_le_bytes());
        o[8..16].copy_from_slice(&self.size.to_le_bytes());
        o[16..24].copy_from_slice(&self.size_compressed.to_le_bytes());
        for k in 0..4 {
            o[24 + k * 8..32 + k * 8].copy_from_slice(&self.time.to_le_bytes());
        }

        o[0x60..0x64].copy_from_slice(&self.blocks.to_le_bytes());
        for (k, v) in self.db.iter().enumerate() {
            o[0x64 + k * 4..0x68 + k * 4].copy_from_slice(&v.to_le_bytes());
        }
        for (k, v) in self.ib.iter().enumerate() {
            o[0x94 + k * 4..0x98 + k * 4].copy_from_slice(&v.to_le_bytes());
        }
        o
    }

    pub fn parse(b: &[u8]) -> Result<Self> {
        if b.len() < INODE_D32_SIZE {
            return Err(format_err!("truncated PFS inode"));
        }
        let mut db = [0i32; 12];
        for (k, v) in db.iter_mut().enumerate() {
            *v = i32::from_le_bytes(b[0x64 + k * 4..0x68 + k * 4].try_into().unwrap());
        }
        let mut ib = [0i32; 5];
        for (k, v) in ib.iter_mut().enumerate() {
            *v = i32::from_le_bytes(b[0x94 + k * 4..0x98 + k * 4].try_into().unwrap());
        }
        Ok(Inode {
            mode: u16::from_le_bytes([b[0], b[1]]),
            nlink: u16::from_le_bytes([b[2], b[3]]),
            flags: u32::from_le_bytes(b[4..8].try_into().unwrap()),
            size: i64::from_le_bytes(b[8..16].try_into().unwrap()),
            size_compressed: i64::from_le_bytes(b[16..24].try_into().unwrap()),
            blocks: u32::from_le_bytes(b[0x60..0x64].try_into().unwrap()),
            db,
            ib,
            time: i64::from_le_bytes(b[24..32].try_into().unwrap()),
        })
    }
}

pub fn dirent_size(name_len: usize) -> usize {
    (name_len + 17).div_ceil(8) * 8
}

pub fn push_dirent(out: &mut Vec<u8>, ino: u32, typ: i32, name: &[u8]) {
    let ent = dirent_size(name.len());
    out.extend_from_slice(&ino.to_le_bytes());
    out.extend_from_slice(&typ.to_le_bytes());
    out.extend_from_slice(&(name.len() as i32).to_le_bytes());
    out.extend_from_slice(&(ent as i32).to_le_bytes());
    out.extend_from_slice(name);
    out.resize(out.len() + (ent - 16 - name.len()), 0);
}

#[derive(Debug, Clone)]
pub struct Dirent {
    pub ino: u32,
    pub typ: i32,
    pub name: Vec<u8>,
}

pub fn parse_dirents(blob: &[u8]) -> Result<Vec<Dirent>> {
    let mut out = Vec::new();
    let mut pos = 0usize;
    while pos + 16 <= blob.len() {
        let ino = u32::from_le_bytes(blob[pos..pos + 4].try_into().unwrap());
        let typ = i32::from_le_bytes(blob[pos + 4..pos + 8].try_into().unwrap());
        let nlen = i32::from_le_bytes(blob[pos + 8..pos + 12].try_into().unwrap());
        let ent = i32::from_le_bytes(blob[pos + 12..pos + 16].try_into().unwrap());
        if ino == 0 && typ == 0 && nlen == 0 && ent == 0 {
            break;
        }
        if ent < 17 || ent % 8 != 0 || nlen < 0 || nlen as i64 > ent as i64 - 16 || pos + ent as usize > blob.len() {
            return Err(format_err!("invalid PFS directory entry at offset {pos}"));
        }
        out.push(Dirent {
            ino,
            typ,
            name: blob[pos + 16..pos + 16 + nlen as usize].to_vec(),
        });
        pos += ent as usize;
    }
    Ok(out)
}

pub fn fpt_hash(path: &str, case_insensitive: bool) -> u32 {
    let mut h: u32 = 0;
    for &b in path.as_bytes() {
        let c = if case_insensitive { b.to_ascii_uppercase() } else { b };
        h = (c as u32).wrapping_add(h.wrapping_mul(31));
    }
    h
}

pub fn build_header(
    version: i64,
    mode: u16,
    block_size: u32,
    inode_count: u64,
    ndblock: u64,
    inode_block_count: u64,
    now: i64,
) -> Vec<u8> {
    let mut h = vec![0u8; block_size as usize];
    h[0..8].copy_from_slice(&version.to_le_bytes());
    h[8..16].copy_from_slice(&PFS_MAGIC.to_le_bytes());
    h[0x18..0x1C].copy_from_slice(&[0, 0, 1, 0]);
    h[0x1C..0x1E].copy_from_slice(&mode.to_le_bytes());
    h[0x20..0x24].copy_from_slice(&block_size.to_le_bytes());
    h[0x28..0x30].copy_from_slice(&1i64.to_le_bytes());
    h[0x30..0x38].copy_from_slice(&(inode_count as i64).to_le_bytes());
    h[0x38..0x40].copy_from_slice(&(ndblock as i64).to_le_bytes());
    h[0x40..0x48].copy_from_slice(&(inode_block_count as i64).to_le_bytes());

    let sig = &mut h[0x50..0x50 + 0x310];
    sig[2..4].copy_from_slice(&1u16.to_le_bytes());
    sig[4..8].copy_from_slice(&FLAG_READONLY.to_le_bytes());
    let sz = (inode_block_count * block_size as u64) as i64;
    sig[8..16].copy_from_slice(&sz.to_le_bytes());
    sig[16..24].copy_from_slice(&sz.to_le_bytes());
    for k in 0..4 {
        sig[0x18 + k * 8..0x20 + k * 8].copy_from_slice(&now.to_le_bytes());
    }
    sig[0x60..0x64].copy_from_slice(&(inode_block_count as u32).to_le_bytes());

    sig[0x68 + 32..0x68 + 40].copy_from_slice(&1i64.to_le_bytes());
    h[0x368..0x36C].copy_from_slice(&1u32.to_le_bytes());
    h
}

#[derive(Debug, Clone)]
pub struct Header {
    pub version: i64,
    pub mode: u16,
    pub block_size: u32,
    pub inode_count: u64,
    pub ndblock: u64,
    pub inode_block_count: u64,
}

pub fn is_pfs(head: &[u8]) -> bool {
    head.len() >= 16
        && matches!(
            i64::from_le_bytes(head[0..8].try_into().unwrap()),
            VERSION_PS4 | VERSION_PS5
        )
        && i64::from_le_bytes(head[8..16].try_into().unwrap()) == PFS_MAGIC
}

pub fn parse_header(h: &[u8]) -> Result<Header> {
    if h.len() < 0x48 || !is_pfs(h) {
        return Err(format_err!("invalid PFS header"));
    }
    let mode = u16::from_le_bytes([h[0x1C], h[0x1D]]);
    let block_size = u32::from_le_bytes(h[0x20..0x24].try_into().unwrap());
    if !block_size.is_power_of_two() || !(4096..=32 * 1024 * 1024).contains(&block_size) {
        return Err(format_err!("invalid PFS block size: {block_size}"));
    }
    let g = |o: usize| i64::from_le_bytes(h[o..o + 8].try_into().unwrap());
    if g(0x30) < 0 || g(0x38) < 0 || g(0x40) < 0 {
        return Err(format_err!("negative PFS header counters"));
    }
    Ok(Header {
        version: g(0),
        mode,
        block_size,
        inode_count: g(0x30) as u64,
        ndblock: g(0x38) as u64,
        inode_block_count: g(0x40) as u64,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inode_roundtrip() {
        let mut i = Inode {
            mode: INODE_MODE_FILE | INODE_RX_ONLY,
            nlink: 1,
            flags: FLAG_READONLY | FLAG_COMPRESSED,
            size: 123,
            size_compressed: 456,
            blocks: 7,
            time: 1234,
            ..Default::default()
        };
        i.db[0] = 6;
        for k in 1..12 {
            i.db[k] = -1;
        }
        let b = i.to_bytes();
        assert_eq!(b.len(), 0xA8);
        let p = Inode::parse(&b).unwrap();
        assert_eq!(p.db, i.db);
        assert_eq!(p.size, 123);
        assert_eq!(p.blocks, 7);
        assert!(p.is_compressed());
        assert_eq!(p.logical_size(), 456);
        assert_eq!(p.stored_size(), 123);
    }

    #[test]
    fn dirent_known_vector() {
        let mut v = Vec::new();
        push_dirent(&mut v, 5, DIRENT_FILE, b"eboot.bin");
        assert_eq!(v.len(), 32);
        assert_eq!(&v[0..4], &5u32.to_le_bytes());
        assert_eq!(&v[8..12], &9i32.to_le_bytes());
        assert_eq!(&v[12..16], &32i32.to_le_bytes());
        let mut v2 = Vec::new();
        push_dirent(&mut v2, 2, DIRENT_DOT, b".");
        assert_eq!(v2.len(), 24);
        let all = [v, v2].concat();
        let p = parse_dirents(&all).unwrap();
        assert_eq!(p.len(), 2);
        assert_eq!(p[0].name, b"eboot.bin");
    }

    #[test]
    fn fpt_hash_matches_formula() {
        assert_eq!(fpt_hash("/A", true), 1522);
        assert_eq!(fpt_hash("/a", true), 1522);
        assert_ne!(fpt_hash("/a", false), 1522);
    }

    #[test]
    fn header_fields() {
        let h = build_header(VERSION_PS5, MODE_CASE_INSENSITIVE, 65536, 4, 7, 1, 1000);
        assert_eq!(h.len(), 65536);
        let p = parse_header(&h).unwrap();
        assert_eq!(p.version, 2);
        assert_eq!(p.mode, 8);
        assert_eq!(p.ndblock, 7);
        assert_eq!(&h[0x368..0x36C], &1u32.to_le_bytes());
    }
}
