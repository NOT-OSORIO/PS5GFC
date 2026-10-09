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

pub mod reader;
pub mod upcase;
pub mod writer;

pub use reader::{is_exfat, ExfatVolume, EXFAT_SIGNATURE};
pub use writer::{plan, ExfatOptions, ExfatPlan};

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ctl::Ctx;
    use crate::io::ReadAt;
    use crate::volume::{FolderVolume, Volume};
    use std::sync::Arc;

    fn make_tree(root: &std::path::Path) {
        std::fs::create_dir_all(root.join("sce_sys")).unwrap();
        std::fs::create_dir_all(root.join("media/deep/er")).unwrap();
        std::fs::write(root.join("eboot.bin"), vec![7u8; 100_000]).unwrap();
        std::fs::write(root.join("sce_sys/param.json"), br#"{"titleId":"PPSA00001"}"#).unwrap();
        std::fs::write(
            root.join("media/deep/er/Big.pak"),
            (0..300_000u32).map(|x| (x % 251) as u8).collect::<Vec<_>>(),
        )
        .unwrap();
        std::fs::write(root.join("media/empty.txt"), b"").unwrap();
        std::fs::write(root.join("media/Ünïcode ファイル.txt"), b"unicode").unwrap();
    }

    #[test]
    fn roundtrip_folder_to_exfat_and_back() {
        let d = tempfile::tempdir().unwrap();
        make_tree(d.path());
        let vol: Arc<dyn Volume> = Arc::new(FolderVolume::scan(d.path(), &Ctx::new()).unwrap());
        let plan = plan(vol.clone(), &ExfatOptions::default()).unwrap();
        assert_eq!(plan.files, 5);
        assert_eq!(plan.dirs, 4);

        let len = plan.image.len() as usize;
        let mut bytes = vec![0u8; len];
        plan.image.read_exact_at(0, &mut bytes).unwrap();
        assert_eq!(&bytes[3..11], b"EXFAT   ");

        let back = ExfatVolume::open(Arc::new(crate::io::MemReader::new(bytes))).unwrap();
        let mut a: Vec<_> = vol
            .entries()
            .iter()
            .map(|e| (e.path.clone(), e.is_dir, e.size))
            .collect();
        let mut b: Vec<_> = back
            .entries()
            .iter()
            .map(|e| (e.path.clone(), e.is_dir, e.size))
            .collect();
        a.sort();
        b.sort();
        assert_eq!(a, b);

        for (i, e) in vol.entries().iter().enumerate() {
            if e.is_dir {
                continue;
            }
            let j = back.find(&e.path).unwrap();
            let x = vol.open(i).unwrap().read_vec(0, e.size as usize).unwrap();
            let y = back.open(j).unwrap().read_vec(0, e.size as usize).unwrap();
            assert_eq!(x, y, "conteúdo de {}", e.path);
        }
    }

    #[test]
    fn rejects_case_only_duplicates() {
        use crate::volume::{Entry, VolumeKind};
        struct V(Vec<Entry>);
        impl Volume for V {
            fn kind(&self) -> VolumeKind {
                VolumeKind::Folder
            }
            fn entries(&self) -> &[Entry] {
                &self.0
            }
            fn open(&self, _: usize) -> crate::Result<Arc<dyn ReadAt>> {
                unreachable!()
            }
        }
        let v = V(vec![
            Entry {
                path: "a.bin".into(),
                is_dir: false,
                size: 0,
                mtime: None,
            },
            Entry {
                path: "A.BIN".into(),
                is_dir: false,
                size: 0,
                mtime: None,
            },
        ]);
        assert!(plan(Arc::new(v), &ExfatOptions::default()).is_err());
    }
}
