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

use std::collections::BTreeMap;
use std::sync::Arc;

use super::format::*;
use crate::image::{ExtentBuilder, ExtentImage};
use crate::tree::Tree;
use crate::util::ceil_div;
use crate::volume::Volume;
use crate::{Error, Result};

#[derive(Debug, Clone)]
pub struct PfsTreeOptions {
    pub block_size: u32,
    pub version: i64,
    pub case_insensitive: bool,
    pub timestamp: i64,
}

impl Default for PfsTreeOptions {
    fn default() -> Self {
        Self {
            block_size: DEFAULT_BLOCK,
            version: VERSION_PS5,
            case_insensitive: true,
            timestamp: std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0),
        }
    }
}

pub struct PfsTreePlan {
    pub image: ExtentImage,
    pub files: u64,
    pub dirs: u64,
}

fn pack_dirents(items: &[(u32, i32, Vec<u8>)], bs: usize) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::new();
    let mut last_at: Option<usize> = None;
    for (ino, typ, name) in items {
        let ent = dirent_size(name.len());
        let in_block = out.len() % bs;
        if in_block + ent > bs {
            if let Some(at) = last_at {
                let block_end = (at / bs + 1) * bs;
                let new_len = block_end - at;
                out[at + 12..at + 16].copy_from_slice(&(new_len as i32).to_le_bytes());
                out.resize(block_end, 0);
            }
        }
        last_at = Some(out.len());
        push_dirent(&mut out, *ino, *typ, name);
    }
    out
}

pub fn plan(volume: Arc<dyn Volume>, opts: &PfsTreeOptions) -> Result<PfsTreePlan> {
    let bs = opts.block_size as u64;
    if !opts.block_size.is_power_of_two() || bs < 16384 {
        return Err(Error::invalid("PFS block size must be a power of 2 and >= 16 KiB"));
    }
    let tree = Tree::build(volume.entries());
    let n = tree.nodes.len();

    let bad: Vec<String> = (1..n)
        .filter(|&i| !tree.nodes[i].name.is_ascii())
        .map(|i| tree.path_of(i))
        .take(5)
        .collect();
    if !bad.is_empty() {
        return Err(Error::invalid(format!(
            "PFS só suporta nomes ASCII; encontrados nomes com outros caracteres (ex.: {})",
            bad.join(", ")
        )));
    }
    for i in 1..n {
        let l = tree.nodes[i].name.len();
        if l == 0 || l > 255 {
            return Err(Error::invalid(format!("invalid name for PFS: {:?}", tree.path_of(i))));
        }
    }

    let path_of: Vec<String> = (0..n)
        .map(|i| if i == 0 { String::new() } else { tree.path_of(i) })
        .collect();
    let mut dirs: Vec<usize> = (1..n).filter(|&i| tree.nodes[i].is_dir).collect();
    let mut files: Vec<usize> = (1..n).filter(|&i| !tree.nodes[i].is_dir).collect();
    dirs.sort_by_key(|&i| path_of[i].to_lowercase());
    files.sort_by_key(|&i| path_of[i].to_lowercase());

    let hash_of = |i: usize| fpt_hash(&format!("/{}", path_of[i]), opts.case_insensitive);
    let mut by_hash: BTreeMap<u32, Vec<usize>> = BTreeMap::new();
    for &i in dirs.iter().chain(files.iter()) {
        by_hash.entry(hash_of(i)).or_default().push(i);
    }
    let has_collision = by_hash.values().any(|v| v.len() > 1);

    let uroot_ino: u32 = if has_collision { 3 } else { 2 };
    let first_node_ino = uroot_ino + 1;
    let mut ino_of = vec![0u32; n];
    ino_of[0] = uroot_ino;
    for (k, &i) in dirs.iter().chain(files.iter()).enumerate() {
        ino_of[i] = first_node_ino + k as u32;
    }
    let inode_count = first_node_ino as u64 + (dirs.len() + files.len()) as u64;

    let dir_blob = |d: usize| -> Vec<u8> {
        let node = &tree.nodes[d];
        let me = ino_of[d];
        let parent = if d == 0 { me } else { ino_of[node.parent] };
        let mut items: Vec<(u32, i32, Vec<u8>)> =
            vec![(me, DIRENT_DOT, b".".to_vec()), (parent, DIRENT_DOTDOT, b"..".to_vec())];
        let mut sub: Vec<usize> = node
            .children
            .iter()
            .copied()
            .filter(|&c| tree.nodes[c].is_dir)
            .collect();
        let mut fl: Vec<usize> = node
            .children
            .iter()
            .copied()
            .filter(|&c| !tree.nodes[c].is_dir)
            .collect();
        sub.sort_by_key(|&c| tree.nodes[c].name.to_lowercase());
        fl.sort_by_key(|&c| tree.nodes[c].name.to_lowercase());
        for c in sub {
            items.push((ino_of[c], DIRENT_DIR, tree.nodes[c].name.as_bytes().to_vec()));
        }
        for c in fl {
            items.push((ino_of[c], DIRENT_FILE, tree.nodes[c].name.as_bytes().to_vec()));
        }
        pack_dirents(&items, bs as usize)
    };

    let mut fpt: Vec<u8> = Vec::new();
    let mut coll: Vec<u8> = Vec::new();
    for (h, list) in &by_hash {
        let value: u32 = if list.len() == 1 {
            let i = list[0];
            ino_of[i] | if tree.nodes[i].is_dir { 0x2000_0000 } else { 0 }
        } else {
            let off = coll.len() as u32;
            for &i in list {
                let typ = if tree.nodes[i].is_dir { DIRENT_DIR } else { DIRENT_FILE };
                push_dirent(&mut coll, ino_of[i], typ, format!("/{}", path_of[i]).as_bytes());
            }
            coll.extend_from_slice(&[0u8; 0x18]);
            0x8000_0000 | off
        };
        fpt.extend_from_slice(&h.to_le_bytes());
        fpt.extend_from_slice(&value.to_le_bytes());
    }

    let mut sr_items: Vec<(u32, i32, Vec<u8>)> = vec![(1, DIRENT_FILE, b"flat_path_table".to_vec())];
    if has_collision {
        sr_items.push((2, DIRENT_FILE, b"collision_resolver".to_vec()));
    }
    sr_items.push((uroot_ino, DIRENT_DIR, b"uroot".to_vec()));
    let sr_blob = pack_dirents(&sr_items, bs as usize);

    let per_block = bs / INODE_D32_SIZE as u64;
    let inode_blocks = ceil_div(inode_count, per_block);
    let mut next_block = 1 + inode_blocks;
    let blocks_for = |len: u64| ceil_div(len, bs).max(1);

    let b_superroot = next_block;
    next_block += blocks_for(sr_blob.len() as u64);
    let b_fpt = next_block;
    next_block += blocks_for(fpt.len() as u64);
    let b_coll = next_block;
    if has_collision {
        next_block += blocks_for(coll.len() as u64);
    } else {
        next_block += 1;
    }

    let now = opts.timestamp;
    let ro = FLAG_READONLY;
    let mk = |mode: u16, nlink: u16, flags: u32, size: u64, blocks: u64, first: u64| -> Inode {
        let mut db = [-1i32; 12];
        db[0] = first as i32;
        Inode {
            mode,
            nlink,
            flags,
            size: size as i64,
            size_compressed: size as i64,
            blocks: blocks as u32,
            db,
            time: now,
            ..Default::default()
        }
    };
    let mut inodes: Vec<Inode> = Vec::with_capacity(inode_count as usize);

    let mut sr = mk(
        INODE_MODE_DIR | INODE_RX_ONLY,
        1,
        FLAG_INTERNAL | ro,
        bs,
        1,
        b_superroot,
    );
    sr.db = [0; 12];
    sr.db[0] = b_superroot as i32;
    sr.size = bs as i64 * blocks_for(sr_blob.len() as u64) as i64;
    sr.size_compressed = sr.size;
    sr.blocks = blocks_for(sr_blob.len() as u64) as u32;
    inodes.push(sr);
    inodes.push(mk(
        INODE_MODE_FILE | INODE_RX_ONLY,
        1,
        FLAG_INTERNAL | ro,
        fpt.len() as u64,
        blocks_for(fpt.len() as u64),
        b_fpt,
    ));
    if has_collision {
        inodes.push(mk(
            INODE_MODE_FILE | INODE_RX_ONLY,
            1,
            FLAG_INTERNAL | ro,
            coll.len() as u64,
            blocks_for(coll.len() as u64),
            b_coll,
        ));
    }

    let mut eb = ExtentBuilder::new();

    let mut placements: Vec<(usize, u64)> = Vec::new();
    let mut dir_blobs: Vec<(usize, Vec<u8>)> = Vec::new();
    for &d in std::iter::once(&0usize).chain(dirs.iter()) {
        let blob = dir_blob(d);
        let blocks = blocks_for(blob.len() as u64);
        placements.push((d, next_block));
        dir_blobs.push((d, blob));
        next_block += blocks;
    }
    for &f in &files {
        placements.push((f, next_block));
        next_block += blocks_for(tree.nodes[f].size);
    }
    let ndblock = next_block;
    if ndblock > i32::MAX as u64 {
        return Err(Error::invalid("image too large for PFS D32 inodes"));
    }

    let place_of: BTreeMap<usize, u64> = placements.iter().copied().collect();
    let blob_len: BTreeMap<usize, u64> = dir_blobs.iter().map(|(d, b)| (*d, b.len() as u64)).collect();
    let mk_dir = |d: usize| -> Inode {
        let node = &tree.nodes[d];
        let subs = node.children.iter().filter(|&&c| tree.nodes[c].is_dir).count() as u16;
        let blocks = blocks_for(blob_len[&d]);
        let base_nlink = if d == 0 { 3 } else { 2 };
        mk(
            INODE_MODE_DIR | INODE_RX_ONLY,
            base_nlink + subs,
            ro,
            blocks * bs,
            blocks,
            place_of[&d],
        )
    };
    inodes.push(mk_dir(0));
    for &d in &dirs {
        inodes.push(mk_dir(d));
    }
    for &f in &files {
        let sz = tree.nodes[f].size;
        let mut ino = mk(INODE_MODE_FILE | INODE_RX_ONLY, 1, ro, sz, blocks_for(sz), place_of[&f]);
        ino.size_compressed = sz as i64;
        inodes.push(ino);
    }
    debug_assert_eq!(inodes.len() as u64, inode_count);

    let mode: u16 = if opts.case_insensitive {
        MODE_CASE_INSENSITIVE
    } else {
        0
    };
    eb.meta_vec(
        0,
        build_header(
            opts.version,
            mode,
            opts.block_size,
            inode_count,
            ndblock,
            inode_blocks,
            now,
        ),
    );
    let mut table = vec![0u8; (inode_blocks * bs) as usize];
    for (k, ino) in inodes.iter().enumerate() {
        let at = (k as u64 / per_block * bs) as usize + (k as u64 % per_block) as usize * INODE_D32_SIZE;
        table[at..at + INODE_D32_SIZE].copy_from_slice(&ino.to_bytes());
    }
    eb.meta_vec(bs, table);
    eb.meta_vec(b_superroot * bs, sr_blob);
    eb.meta_vec(b_fpt * bs, fpt);
    if has_collision {
        eb.meta_vec(b_coll * bs, coll);
    }
    for (d, blob) in dir_blobs {
        eb.meta_vec(place_of[&d] * bs, blob);
    }
    for &f in &files {
        let node = &tree.nodes[f];
        if node.size > 0 {
            eb.file(place_of[&f] * bs, node.entry.expect("arquivo sem entrada"), node.size);
        }
    }
    let image = eb.finish(ndblock * bs, volume);
    Ok(PfsTreePlan {
        image,
        files: files.len() as u64,
        dirs: dirs.len() as u64,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ctl::Ctx;
    use crate::io::{MemReader, ReadAt};
    use crate::pfs::PfsImage;
    use crate::volume::FolderVolume;

    #[test]
    fn tree_pfs_roundtrip() {
        let d = tempfile::tempdir().unwrap();
        let r = d.path();
        std::fs::create_dir_all(r.join("sce_sys")).unwrap();
        std::fs::create_dir_all(r.join("a/b/c")).unwrap();
        std::fs::create_dir_all(r.join("emptydir")).unwrap();
        std::fs::write(r.join("eboot.bin"), vec![1u8; 200_000]).unwrap();
        std::fs::write(r.join("sce_sys/param.json"), b"{}").unwrap();
        std::fs::write(r.join("a/b/c/deep.bin"), vec![9u8; 70_000]).unwrap();
        std::fs::write(r.join("zero.dat"), b"").unwrap();
        for i in 0..2500 {
            std::fs::write(r.join(format!("a/many_file_name_{i:05}.x")), vec![(i % 200) as u8; 10]).unwrap();
        }
        let vol: Arc<dyn Volume> = Arc::new(FolderVolume::scan(r, &Ctx::new()).unwrap());
        let plan = plan(vol.clone(), &PfsTreeOptions::default()).unwrap();
        let mut bytes = vec![0u8; plan.image.len() as usize];
        plan.image.read_exact_at(0, &mut bytes).unwrap();
        let back = PfsImage::open(Arc::new(MemReader::new(bytes))).unwrap();
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
            assert_eq!(x, y, "{}", e.path);
        }
    }

    #[test]
    fn non_ascii_rejected() {
        let d = tempfile::tempdir().unwrap();
        std::fs::write(d.path().join("ação.bin"), b"x").unwrap();
        let vol: Arc<dyn Volume> = Arc::new(FolderVolume::scan(d.path(), &Ctx::new()).unwrap());
        assert!(plan(vol, &PfsTreeOptions::default()).is_err());
    }
}
