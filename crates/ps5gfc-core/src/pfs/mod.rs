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

pub mod container;
pub mod format;
pub mod reader;
pub mod tree_writer;

pub use container::{
    find_title_id, plan_container, safe_inner_name, write_container, ContainerOptions, ContainerPlan, ContainerStats,
};
pub use reader::PfsImage;
pub use tree_writer::{plan as plan_tree, PfsTreeOptions};

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ctl::Ctx;
    use crate::io::{FileReader, MemReader, ReadAt};
    use crate::volume::Volume;
    use std::sync::Arc;

    #[test]
    fn container_roundtrip_single_inner_file() {
        let dir = tempfile::tempdir().unwrap();
        let out = dir.path().join("PPSA00001.ffpfsc");

        let mut inner = Vec::new();
        let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
        for blk in 0..40u32 {
            for i in 0..65536usize {
                inner.push(match blk % 3 {
                    0 => 0,
                    1 => ((i + blk as usize) % 97) as u8,
                    _ => {
                        x ^= x << 13;
                        x ^= x >> 7;
                        x ^= x << 17;
                        (x >> 11) as u8
                    }
                });
            }
        }
        inner.truncate(inner.len() - 777);
        let img = MemReader::new(inner.clone());
        let opts = ContainerOptions {
            pfsc: crate::pfsc::PfscOptions {
                threads: 4,
                ..Default::default()
            },
            ..Default::default()
        };
        let stats = write_container(&img, "PPSA00001.exfat", &out, &opts, &Ctx::new()).unwrap();
        assert_eq!(stats.inner_len, inner.len() as u64);
        assert_eq!(std::fs::metadata(&out).unwrap().len(), stats.file_len);
        assert_eq!(stats.file_len % 65536, 0);

        let pfs = PfsImage::open(Arc::new(FileReader::open(&out).unwrap())).unwrap();
        assert_eq!(pfs.header().version, 2);
        assert_eq!(pfs.header().ndblock * 65536, stats.file_len);
        assert_eq!(pfs.entries().len(), 1);
        assert_eq!(pfs.entries()[0].path, "PPSA00001.exfat");
        assert_eq!(pfs.entries()[0].size, inner.len() as u64);
        let idx = pfs.single_file().unwrap();
        let r = pfs.open(idx).unwrap();
        assert_eq!(r.len(), inner.len() as u64);
        let back = r.read_vec(0, inner.len()).unwrap();
        assert_eq!(back, inner);
    }
}
