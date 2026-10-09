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

use std::fs::{File, OpenOptions};
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use crate::util::long_path;

pub trait ReadAt: Send + Sync {
    fn len(&self) -> u64;

    fn is_empty(&self) -> bool {
        self.len() == 0
    }

    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize>;

    fn read_exact_at(&self, mut off: u64, mut buf: &mut [u8]) -> io::Result<()> {
        while !buf.is_empty() {
            let n = self.read_at(off, buf)?;
            if n == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    format!("leitura além do fim (offset {off})"),
                ));
            }
            off += n as u64;
            buf = &mut buf[n..];
        }
        Ok(())
    }

    fn read_vec(&self, off: u64, len: usize) -> io::Result<Vec<u8>> {
        let avail = self.len().saturating_sub(off).min(len as u64) as usize;
        let mut v = vec![0u8; avail];
        self.read_exact_at(off, &mut v)?;
        Ok(v)
    }
}

impl<T: ReadAt + ?Sized> ReadAt for Arc<T> {
    fn len(&self) -> u64 {
        (**self).len()
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        (**self).read_at(off, buf)
    }
}

impl<T: ReadAt + ?Sized> ReadAt for Box<T> {
    fn len(&self) -> u64 {
        (**self).len()
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        (**self).read_at(off, buf)
    }
}

#[cfg(windows)]
fn pread(f: &File, buf: &mut [u8], off: u64) -> io::Result<usize> {
    use std::os::windows::fs::FileExt;
    f.seek_read(buf, off)
}
#[cfg(unix)]
fn pread(f: &File, buf: &mut [u8], off: u64) -> io::Result<usize> {
    use std::os::unix::fs::FileExt;
    f.read_at(buf, off)
}

#[cfg(windows)]
fn pwrite(f: &File, buf: &[u8], off: u64) -> io::Result<usize> {
    use std::os::windows::fs::FileExt;
    f.seek_write(buf, off)
}
#[cfg(unix)]
fn pwrite(f: &File, buf: &[u8], off: u64) -> io::Result<usize> {
    use std::os::unix::fs::FileExt;
    f.write_at(buf, off)
}

fn open_for_read(path: &Path) -> io::Result<File> {
    OpenOptions::new().read(true).open(path)
}

pub struct FileReader {
    path: PathBuf,
    len: u64,
    pool: Mutex<Vec<File>>,
}

const POOL_MAX: usize = 32;

impl FileReader {
    pub fn open(path: &Path) -> io::Result<Self> {
        let path = long_path(path);
        let f = open_for_read(&path)?;
        let len = f.metadata()?.len();
        Ok(Self {
            path,
            len,
            pool: Mutex::new(vec![f]),
        })
    }

    pub fn open_with_len(path: &Path, len: u64) -> io::Result<Self> {
        let path = long_path(path);
        let f = open_for_read(&path)?;
        Ok(Self {
            path,
            len,
            pool: Mutex::new(vec![f]),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn take(&self) -> io::Result<File> {
        if let Some(f) = self.pool.lock().unwrap().pop() {
            return Ok(f);
        }
        open_for_read(&self.path)
    }

    fn give(&self, f: File) {
        let mut p = self.pool.lock().unwrap();
        if p.len() < POOL_MAX {
            p.push(f);
        }
    }
}

impl ReadAt for FileReader {
    fn len(&self) -> u64 {
        self.len
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        if off >= self.len {
            return Ok(0);
        }
        let f = self.take()?;
        let r = pread(&f, buf, off);
        self.give(f);
        r
    }
}

pub struct SubReader {
    inner: Arc<dyn ReadAt>,
    off: u64,
    len: u64,
}

impl SubReader {
    pub fn new(inner: Arc<dyn ReadAt>, off: u64, len: u64) -> Self {
        let avail = inner.len().saturating_sub(off);
        Self {
            inner,
            off,
            len: len.min(avail),
        }
    }
}

impl ReadAt for SubReader {
    fn len(&self) -> u64 {
        self.len
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        if off >= self.len {
            return Ok(0);
        }
        let want = (buf.len() as u64).min(self.len - off) as usize;
        self.inner.read_at(self.off + off, &mut buf[..want])
    }
}

pub struct MemReader(pub Arc<Vec<u8>>);

impl MemReader {
    pub fn new(v: Vec<u8>) -> Self {
        Self(Arc::new(v))
    }
}

impl ReadAt for MemReader {
    fn len(&self) -> u64 {
        self.0.len() as u64
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        let data = &self.0;
        if off >= data.len() as u64 {
            return Ok(0);
        }
        let start = off as usize;
        let n = buf.len().min(data.len() - start);
        buf[..n].copy_from_slice(&data[start..start + n]);
        Ok(n)
    }
}

pub struct OutFile {
    file: File,
    path: PathBuf,
}

impl OutFile {
    pub fn create(path: &Path, size: u64) -> io::Result<Self> {
        let p = long_path(path);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(&p)?;
        if size > 0 {
            file.set_len(size)?;
        }
        Ok(Self { file, path: p })
    }

    pub fn create_light(path: &Path) -> io::Result<Self> {
        let p = long_path(path);
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(&p)?;
        Ok(Self { file, path: p })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn write_all_at(&self, mut off: u64, mut buf: &[u8]) -> io::Result<()> {
        while !buf.is_empty() {
            let n = pwrite(&self.file, buf, off)?;
            if n == 0 {
                return Err(io::Error::new(io::ErrorKind::WriteZero, "escrita retornou 0 bytes"));
            }
            off += n as u64;
            buf = &buf[n..];
        }
        Ok(())
    }

    pub fn set_len(&self, len: u64) -> io::Result<()> {
        self.file.set_len(len)
    }

    pub fn discard(&self) {
        let _ = self.file.set_len(0);
    }

    pub fn sync(&self) -> io::Result<()> {
        self.file.sync_data()
    }

    pub fn file(&self) -> &File {
        &self.file
    }
}

impl ReadAt for OutFile {
    fn len(&self) -> u64 {
        self.file.metadata().map(|m| m.len()).unwrap_or(0)
    }
    fn read_at(&self, off: u64, buf: &mut [u8]) -> io::Result<usize> {
        pread(&self.file, buf, off)
    }
}

pub struct Closer {
    tx: Option<crossbeam_channel::Sender<OutFile>>,
    handles: Vec<std::thread::JoinHandle<()>>,
}

impl Closer {
    pub fn new(threads: usize, bound: usize) -> Self {
        let (tx, rx) = crossbeam_channel::bounded::<OutFile>(bound.max(1));
        let handles = (0..threads.max(1))
            .map(|k| {
                let rx = rx.clone();
                std::thread::Builder::new()
                    .name(format!("ps5gfc-close-{k}"))
                    .spawn(move || {
                        for f in rx {
                            drop(f);
                        }
                    })
                    .expect("thread de fechamento")
            })
            .collect();
        Self { tx: Some(tx), handles }
    }

    pub fn close(&self, f: OutFile) {
        match &self.tx {
            Some(tx) => {
                if let Err(e) = tx.send(f) {
                    drop(e.into_inner());
                }
            }
            None => drop(f),
        }
    }

    pub fn finish(mut self) {
        self.join_all();
    }

    fn join_all(&mut self) {
        self.tx.take();
        for h in self.handles.drain(..) {
            let _ = h.join();
        }
    }
}

impl Drop for Closer {
    fn drop(&mut self) {
        self.join_all();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn discard_shrinks_a_reserved_output_before_it_is_closed() {
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("out.bin");
        let f = OutFile::create(&p, 64 << 20).unwrap();
        f.write_all_at(0, &[7u8; 4096]).unwrap();
        assert_eq!(f.len(), 64 << 20, "a reserva fica até o discard");
        f.discard();
        assert_eq!(f.len(), 0);
        drop(f);
        assert_eq!(std::fs::metadata(&p).unwrap().len(), 0);
    }

    #[test]
    fn sub_reader_clamps() {
        let m: Arc<dyn ReadAt> = Arc::new(MemReader::new((0u8..100).collect()));
        let s = SubReader::new(m, 10, 1000);
        assert_eq!(s.len(), 90);
        let mut b = [0u8; 4];
        s.read_exact_at(0, &mut b).unwrap();
        assert_eq!(b, [10, 11, 12, 13]);
        assert_eq!(s.read_at(90, &mut b).unwrap(), 0);
    }

    #[test]
    fn out_file_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("x.bin");
        let o = OutFile::create(&p, 16).unwrap();
        o.write_all_at(4, b"abcd").unwrap();
        let mut b = [0u8; 8];
        o.read_exact_at(0, &mut b).unwrap();
        assert_eq!(&b, &[0, 0, 0, 0, b'a', b'b', b'c', b'd']);
    }
}
