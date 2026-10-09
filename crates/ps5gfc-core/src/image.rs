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

use std::collections::HashMap;
use std::io;
use std::sync::{Arc, Mutex};

use crate::io::ReadAt;
use crate::volume::Volume;

#[derive(Clone)]
pub enum Kind {
    Meta { data: Arc<Vec<u8>>, offset: usize },

    File { entry: usize, file_off: u64, file_len: u64 },
}

#[derive(Clone)]
pub struct Extent {
    pub start: u64,
    pub len: u64,
    pub kind: Kind,
}

#[derive(Default)]
pub struct ExtentBuilder {
    extents: Vec<Extent>,
}

impl ExtentBuilder {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn meta(&mut self, start: u64, data: Arc<Vec<u8>>, offset: usize, len: u64) {
        if len > 0 {
            self.extents.push(Extent {
                start,
                len,
                kind: Kind::Meta { data, offset },
            });
        }
    }

    pub fn meta_vec(&mut self, start: u64, data: Vec<u8>) {
        let len = data.len() as u64;
        self.meta(start, Arc::new(data), 0, len);
    }

    pub fn file(&mut self, start: u64, entry: usize, file_len: u64) {
        self.file_part(start, entry, 0, file_len, file_len);
    }

    pub fn file_part(&mut self, start: u64, entry: usize, file_off: u64, len: u64, file_len: u64) {
        if len > 0 {
            self.extents.push(Extent {
                start,
                len,
                kind: Kind::File {
                    entry,
                    file_off,
                    file_len,
                },
            });
        }
    }

    pub fn finish(mut self, total_len: u64, volume: Arc<dyn Volume>) -> ExtentImage {
        self.extents.sort_by_key(|e| e.start);
        debug_assert!(
            self.extents.windows(2).all(|w| w[0].start + w[0].len <= w[1].start),
            "extensões sobrepostas"
        );
        debug_assert!(self.extents.last().map_or(true, |e| e.start + e.len <= total_len));
        ExtentImage {
            len: total_len,
            extents: self.extents,
            volume,
            cache: Mutex::new(HandleCache::default()),
        }
    }
}

const CACHE_CAP: usize = 256;

#[derive(Default)]
struct HandleCache {
    map: HashMap<usize, (Arc<dyn ReadAt>, u64)>,
    tick: u64,
}

pub struct ExtentImage {
    len: u64,
    extents: Vec<Extent>,
    volume: Arc<dyn Volume>,
    cache: Mutex<HandleCache>,
}

impl ExtentImage {
    pub fn extents(&self) -> &[Extent] {
        &self.extents
    }
    pub fn volume(&self) -> &Arc<dyn Volume> {
        &self.volume
    }

    fn reader(&self, entry: usize) -> io::Result<Arc<dyn ReadAt>> {
        {
            let mut c = self.cache.lock().unwrap();
            c.tick += 1;
            let t = c.tick;
            if let Some((r, last)) = c.map.get_mut(&entry) {
                *last = t;
                return Ok(r.clone());
            }
        }

        let r = self.volume.open(entry).map_err(|e| match e {
            crate::Error::Io(e) => e,
            other => io::Error::other(other.to_string()),
        })?;
        let mut c = self.cache.lock().unwrap();
        if c.map.len() >= CACHE_CAP {
            if let Some((&k, _)) = c.map.iter().min_by_key(|(_, (_, t))| *t) {
                c.map.remove(&k);
            }
        }
        c.tick += 1;
        let t = c.tick;
        c.map.insert(entry, (r.clone(), t));
        Ok(r)
    }
}

impl ReadAt for ExtentImage {
    fn len(&self) -> u64 {
        self.len
    }

    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        if off >= self.len || buf.is_empty() {
            return Ok(0);
        }
        let want = (buf.len() as u64).min(self.len - off) as usize;
        let mut done = 0usize;
        let mut pos = off;
        let mut i = self.extents.partition_point(|e| e.start + e.len <= pos);
        while done < want {
            let remaining = want - done;
            match self.extents.get(i) {
                Some(e) if e.start <= pos => {
                    let within = pos - e.start;
                    let n = (remaining as u64).min(e.len - within) as usize;
                    match &e.kind {
                        Kind::Meta { data, offset } => {
                            let s = *offset + within as usize;
                            buf[done..done + n].copy_from_slice(&data[s..s + n]);
                        }
                        Kind::File {
                            entry,
                            file_off,
                            file_len,
                        } => {
                            let fpos = file_off + within;
                            let avail = file_len.saturating_sub(fpos);
                            let rd = (n as u64).min(avail) as usize;
                            if rd > 0 {
                                self.reader(*entry)?.read_exact_at(fpos, &mut buf[done..done + rd])?;
                            }
                            buf[done + rd..done + n].fill(0);
                        }
                    }
                    done += n;
                    pos += n as u64;
                    i += 1;
                }
                next => {
                    let gap_end = next.map_or(u64::MAX, |e| e.start);
                    let n = (remaining as u64).min(gap_end - pos) as usize;
                    buf[done..done + n].fill(0);
                    done += n;
                    pos += n as u64;
                }
            }
        }
        Ok(want)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::io::MemReader;
    use crate::volume::{Entry, VolumeKind};

    struct MemVolume {
        entries: Vec<Entry>,
        data: Vec<Arc<Vec<u8>>>,
    }
    impl Volume for MemVolume {
        fn kind(&self) -> VolumeKind {
            VolumeKind::Folder
        }
        fn entries(&self) -> &[Entry] {
            &self.entries
        }
        fn open(&self, i: usize) -> crate::Result<Arc<dyn ReadAt>> {
            Ok(Arc::new(MemReader(self.data[i].clone())))
        }
    }

    #[test]
    fn composes_meta_file_and_gaps() {
        let vol = Arc::new(MemVolume {
            entries: vec![Entry {
                path: "a".into(),
                is_dir: false,
                size: 5,
                mtime: None,
            }],
            data: vec![Arc::new(b"HELLO".to_vec())],
        });
        let mut b = ExtentBuilder::new();
        b.meta_vec(2, vec![1, 2, 3]);
        b.file(10, 0, 5);
        let img = b.finish(20, vol);
        let mut all = vec![0xAAu8; 20];
        assert_eq!(img.read_at(0, &mut all).unwrap(), 20);
        assert_eq!(
            &all[..],
            &[0, 0, 1, 2, 3, 0, 0, 0, 0, 0, b'H', b'E', b'L', b'L', b'O', 0, 0, 0, 0, 0]
        );

        let mut part = [0u8; 6];
        img.read_exact_at(8, &mut part).unwrap();
        assert_eq!(&part, &[0, 0, b'H', b'E', b'L', b'L']);
    }
}
