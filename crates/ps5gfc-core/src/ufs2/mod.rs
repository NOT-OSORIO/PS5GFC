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

pub mod layout;
pub mod reader;
pub mod writer;

pub use reader::{is_ufs2, Ufs2Volume};
pub use writer::{plan, Ufs2Options, Ufs2Plan};

#[cfg(test)]
mod tests {
    use super::layout::*;
    use super::*;
    use crate::ctl::Ctx;
    use crate::io::{MemReader, ReadAt};
    use crate::volume::{FolderVolume, Volume};
    use std::collections::HashSet;
    use std::sync::Arc;

    struct FakeVolume(Vec<crate::volume::Entry>);
    impl Volume for FakeVolume {
        fn kind(&self) -> crate::volume::VolumeKind {
            crate::volume::VolumeKind::Folder
        }
        fn entries(&self) -> &[crate::volume::Entry] {
            &self.0
        }
        fn open(&self, _: usize) -> crate::Result<Arc<dyn ReadAt>> {
            Ok(Arc::new(MemReader::new(Vec::new())))
        }
    }

    #[test]
    fn multi_group_image_is_tight() {
        use crate::volume::Entry;
        let gib = 1u64 << 30;
        let mut entries = vec![Entry {
            path: "data".into(),
            is_dir: true,
            size: 0,
            mtime: None,
        }];
        let sizes = [3.1, 2.7, 2.2, 3.4, 1.9, 2.8, 1.5, 2.6, 2.0, 1.3, 0.9, 0.6, 0.25];
        let mut total = 0u64;
        for (i, g) in sizes.iter().enumerate() {
            let size = (*g * gib as f64) as u64;
            total += size;
            entries.push(Entry {
                path: format!("data/pak_{i:02}.pak"),
                is_dir: false,
                size,
                mtime: None,
            });
        }
        let plan = plan(Arc::new(FakeVolume(entries)), &Ufs2Options::default()).unwrap();
        let len = plan.image.len();
        assert!(len >= total, "a imagem não pode ser menor que o conteúdo");
        assert!(
            len <= total + total / 100 + (64 << 20),
            "imagem de {len} bytes para {total} de conteúdo: desperdício demais"
        );
    }

    fn make_tree(root: &std::path::Path, big: bool) {
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
        if big {
            let n = (12 + 8192 + 5) * 65536 + 123;
            let data: Vec<u8> = (0..n)
                .map(|i| ((i as u64 * 31 + (i as u64 >> 16)) % 253) as u8)
                .collect();
            std::fs::write(root.join("media/huge.pak"), data).unwrap();
        }
        for i in 0..700 {
            std::fs::write(
                root.join(format!("media/f{i:04}.dat")),
                vec![(i % 256) as u8; (i * 37) % 5000],
            )
            .unwrap();
        }
    }

    fn check_image(bytes: &[u8]) {
        let rd = |o: usize, n: usize| &bytes[o..o + n];
        let i32at = |b: &[u8], o: usize| i32::from_le_bytes(b[o..o + 4].try_into().unwrap());
        let i64at = |b: &[u8], o: usize| i64::from_le_bytes(b[o..o + 8].try_into().unwrap());
        let sb = rd(SBLOCK_OFFSET as usize, SBLOCKSIZE);
        assert_eq!(u32::from_le_bytes(sb[0x55C..0x560].try_into().unwrap()), FS_UFS2_MAGIC);
        let (bsize, fsize) = (i32at(sb, 0x30) as usize, i32at(sb, 0x34) as usize);
        let (ncg, ipg, fpg) = (
            i32at(sb, 0x2C) as usize,
            i32at(sb, 0xB8) as usize,
            i32at(sb, 0xBC) as usize,
        );
        let (iblkno, cblkno, dblkno, sblkno) = (
            i32at(sb, 0x10) as usize,
            i32at(sb, 0x0C) as usize,
            i32at(sb, 0x14) as usize,
            i32at(sb, 0x08) as usize,
        );
        let size = i64at(sb, 0x438) as usize;
        assert_eq!(bytes.len(), size * fsize);
        assert_eq!(ncg, size.div_ceil(fpg));
        assert_eq!(fsize, bsize);
        let cssize = i32at(sb, 0x9C) as usize;
        let cs_frags = cssize.div_ceil(fsize);

        let mut used: HashSet<u64> = HashSet::new();
        let mut note = |f: u64, what: &str| {
            assert!(used.insert(f), "fragmento {f} referenciado duas vezes ({what})");
        };
        let nindir = bsize / 8;
        let read_ptrs = |frag: u64| -> Vec<u64> {
            bytes[frag as usize * fsize..frag as usize * fsize + bsize]
                .chunks_exact(8)
                .map(|c| u64::from_le_bytes(c.try_into().unwrap()))
                .collect()
        };
        let mut ndir_cg = vec![0u32; ncg];
        let mut used_ino_cg = vec![0u32; ncg];
        let mut max_ino_seen = 2usize;
        for c in 0..ncg {
            let iused_off = i32at(&bytes[(c * fpg + cblkno) * fsize..], 0x5C) as usize;
            let bm = &bytes[(c * fpg + cblkno) * fsize + iused_off..];
            for i in 0..ipg {
                if bm[i / 8] & (1 << (i % 8)) != 0 {
                    used_ino_cg[c] += 1;
                    let ino = c * ipg + i;
                    max_ino_seen = max_ino_seen.max(ino);
                    if ino < 2 {
                        continue;
                    }
                    let off = (c * fpg + iblkno) * fsize + i * 256;
                    let di = Dinode::parse(&bytes[off..off + 256]);
                    assert!(
                        di.mode & 0xF000 == IFDIR || di.mode & 0xF000 == IFREG,
                        "inode {ino} sem tipo"
                    );
                    if di.mode & 0xF000 == IFDIR {
                        ndir_cg[c] += 1;
                    }
                    let nb = (di.size as usize).div_ceil(bsize);
                    let mut data = 0usize;
                    let mut meta = 0usize;
                    for k in 0..NDADDR.min(nb) {
                        note(di.db[k] as u64, "db");
                        data += 1;
                    }
                    let mut rem = nb.saturating_sub(NDADDR);
                    for (lvl, &ib) in di.ib.iter().enumerate() {
                        if rem == 0 {
                            break;
                        }
                        fn walk(
                            f: u64,
                            level: usize,
                            rem: &mut usize,
                            nindir: usize,
                            read: &dyn Fn(u64) -> Vec<u64>,
                            note: &mut dyn FnMut(u64, &str),
                            data: &mut usize,
                            meta: &mut usize,
                        ) {
                            note(f, "indireto");
                            *meta += 1;
                            for p in read(f) {
                                if *rem == 0 {
                                    break;
                                }
                                if level == 1 {
                                    note(p, "dado");
                                    *data += 1;
                                    *rem -= 1;
                                } else {
                                    walk(p, level - 1, rem, nindir, read, note, data, meta);
                                }
                            }
                        }
                        let _ = nindir;
                        walk(
                            ib as u64,
                            lvl + 1,
                            &mut rem,
                            nindir,
                            &read_ptrs,
                            &mut note,
                            &mut data,
                            &mut meta,
                        );
                    }
                    assert_eq!(rem, 0, "inode {ino}: blocos faltando");
                    assert_eq!(data, nb, "inode {ino}: dados");
                    assert_eq!(
                        di.blocks as usize,
                        (data + meta) * (fsize / 512),
                        "inode {ino}: di_blocks"
                    );
                }
            }
        }
        assert!(max_ino_seen >= 2);

        let mut tot_free = 0u64;
        let mut tot_ndir = 0u64;
        let mut tot_ifree = 0u64;
        for c in 0..ncg {
            let cg = &bytes[(c * fpg + cblkno) * fsize..(c * fpg + cblkno) * fsize + bsize];
            assert_eq!(i32at(cg, 0x04) as u32, CG_MAGIC);
            assert_eq!(i32at(cg, 0x0C) as usize, c);
            let ndblk = (size - c * fpg).min(fpg);
            assert_eq!(i32at(cg, 0x14) as usize, ndblk);
            let free_off = i32at(cg, 0x60) as usize;
            let mut free_cnt = 0u64;
            for f in 0..ndblk {
                let is_free = cg[free_off + f / 8] & (1 << (f % 8)) != 0;
                let abs = (c * fpg + f) as u64;
                let meta_region = if c == 0 {
                    f < dblkno + cs_frags
                } else {
                    f >= sblkno && f < dblkno
                };
                let boot_region = c == 0 && f < sblkno;
                let referenced = used.contains(&abs);
                if is_free {
                    free_cnt += 1;
                    assert!(
                        !referenced && !meta_region && !boot_region,
                        "frag {abs} (cg {c}+{f}) livre mas em uso"
                    );
                } else {
                    assert!(
                        referenced || meta_region || boot_region,
                        "frag {abs} (cg {c}+{f}) marcado usado mas ninguém referencia"
                    );
                }
            }
            assert_eq!(i32at(cg, 0x1C) as u64, free_cnt, "cg {c}: cs_nbfree");
            assert_eq!(
                i32at(cg, 0x18) as u32,
                ndir_cg[c] + if c == 0 { 0 } else { 0 },
                "cg {c}: cs_ndir"
            );
            assert_eq!(i32at(cg, 0x20) as u32, ipg as u32 - used_ino_cg[c], "cg {c}: cs_nifree");
            tot_free += free_cnt;
            tot_ndir += i32at(cg, 0x18) as u64;
            tot_ifree += i32at(cg, 0x20) as u64;

            let cs = &bytes[dblkno * fsize + c * 16..dblkno * fsize + c * 16 + 16];
            assert_eq!(&cs[0..16], &cg[0x18..0x28], "cg {c}: resumo cs");
        }

        assert_eq!(i64at(sb, 0x3F0) as u64, tot_ndir);
        assert_eq!(i64at(sb, 0x3F8) as u64, tot_free);
        assert_eq!(i64at(sb, 0x400) as u64, tot_ifree);
    }

    fn roundtrip(big: bool) {
        let d = tempfile::tempdir().unwrap();
        make_tree(d.path(), big);
        let vol: Arc<dyn Volume> = Arc::new(FolderVolume::scan(d.path(), &Ctx::new()).unwrap());
        let plan = plan(vol.clone(), &Ufs2Options::default()).unwrap();
        let len = plan.image.len() as usize;
        let mut bytes = vec![0u8; len];
        plan.image.read_exact_at(0, &mut bytes).unwrap();
        check_image(&bytes);

        let back = Ufs2Volume::open(Arc::new(MemReader::new(bytes))).unwrap();
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
            assert!(x == y, "conteúdo de {} difere", e.path);
        }
    }

    #[test]
    fn small_tree_roundtrip_and_fsck() {
        roundtrip(false);
    }

    #[test]
    fn double_indirect_roundtrip_and_fsck() {
        roundtrip(true);
    }
}
