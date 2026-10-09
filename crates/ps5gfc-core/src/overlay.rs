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
use std::sync::Arc;

use crate::io::{MemReader, ReadAt};
use crate::volume::{Entry, Volume, VolumeKind};
use crate::{Error, Result};

pub struct OverlayVolume {
    base: Arc<dyn Volume>,
    entries: Vec<Entry>,

    mem: HashMap<usize, Arc<Vec<u8>>>,
    base_len: usize,
}

impl OverlayVolume {
    pub fn new(base: Arc<dyn Volume>) -> Self {
        let entries = base.entries().to_vec();
        let base_len = entries.len();
        Self {
            base,
            entries,
            mem: HashMap::new(),
            base_len,
        }
    }

    pub fn with_file(mut self, path: &str, data: Vec<u8>) -> Self {
        let key = path.to_ascii_lowercase();
        let size = data.len() as u64;
        let mtime = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .ok()
            .map(|d| d.as_secs() as i64);
        if let Some(i) = self
            .entries
            .iter()
            .position(|e| !e.is_dir && e.path.to_ascii_lowercase() == key)
        {
            self.entries[i].size = size;
            self.entries[i].mtime = mtime;
            self.mem.insert(i, Arc::new(data));
        } else {
            self.entries.push(Entry {
                path: path.to_string(),
                is_dir: false,
                size,
                mtime,
            });
            self.mem.insert(self.entries.len() - 1, Arc::new(data));
        }
        self
    }

    pub fn added(&self) -> usize {
        self.entries.len() - self.base_len
    }
}

impl Volume for OverlayVolume {
    fn kind(&self) -> VolumeKind {
        self.base.kind()
    }
    fn entries(&self) -> &[Entry] {
        &self.entries
    }
    fn label(&self) -> String {
        self.base.label()
    }
    fn describe(&self) -> Vec<(String, String)> {
        self.base.describe()
    }
    fn open(&self, index: usize) -> Result<Arc<dyn ReadAt>> {
        if let Some(m) = self.mem.get(&index) {
            return Ok(Arc::new(MemReader(m.clone())));
        }
        if index >= self.base_len {
            return Err(Error::invalid(crate::t!("err.bad_index")));
        }
        self.base.open(index)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::volume::VolumeKind;

    struct V(Vec<Entry>);
    impl Volume for V {
        fn kind(&self) -> VolumeKind {
            VolumeKind::Folder
        }
        fn entries(&self) -> &[Entry] {
            &self.0
        }
        fn open(&self, _: usize) -> Result<Arc<dyn ReadAt>> {
            Ok(Arc::new(MemReader::new(b"base".to_vec())))
        }
    }

    #[test]
    fn adds_and_replaces() {
        let base: Arc<dyn Volume> = Arc::new(V(vec![Entry {
            path: "a.bin".into(),
            is_dir: false,
            size: 4,
            mtime: None,
        }]));
        let o = OverlayVolume::new(base)
            .with_file("x.idx", b"hello".to_vec())
            .with_file("A.BIN", b"zz".to_vec());
        assert_eq!(o.entries().len(), 2);
        assert_eq!(o.open(0).unwrap().read_vec(0, 10).unwrap(), b"zz");
        assert_eq!(o.open(1).unwrap().read_vec(0, 10).unwrap(), b"hello");
        assert_eq!(o.added(), 1);
    }
}
