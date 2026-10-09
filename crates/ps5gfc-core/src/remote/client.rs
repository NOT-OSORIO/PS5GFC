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

use std::io::{self, Read};
use std::time::Duration;

use serde::Serialize;

use super::ftp::{Ftp, FtpReader};
use super::http::{Endpoint, Response, SentHook};
use super::prospero::{Caps, Conflict, ConsoleInfo, Entry, Job, Preflight, Prospero, StorageVolume, TextFile};
use super::{path as rp, RemoteError, RemoteKind};
use crate::ctl::Cancel;
use crate::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Protocol {
    Prospero,
    Ftp,
}

#[derive(Clone)]
pub enum Client {
    Prospero(Prospero),
    Ftp(Ftp),
}

impl From<Prospero> for Client {
    fn from(p: Prospero) -> Self {
        Client::Prospero(p)
    }
}

impl From<Ftp> for Client {
    fn from(f: Ftp) -> Self {
        Client::Ftp(f)
    }
}

pub enum Download {
    Http(Response),
    Ftp(FtpReader),
}

impl Read for Download {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match self {
            Download::Http(r) => r.read(buf),
            Download::Ftp(r) => r.read(buf),
        }
    }
}

fn no_zip() -> Error {
    Error::Remote(RemoteError::new(
        RemoteKind::Other,
        0,
        "compactar e extrair só funcionam com o Prospero Manager (HTTP)",
    ))
}

impl Client {
    pub fn connect_to(addr: &str) -> Result<Self> {
        if addr.trim().to_ascii_lowercase().starts_with("ftp://") {
            Ok(Client::Ftp(Ftp::connect_to(addr)?))
        } else {
            Ok(Client::Prospero(Prospero::connect_to(addr)?))
        }
    }

    pub fn open(protocol: &str, host: &str, port: u16, user: &str, pass: &str) -> Result<Self> {
        let authority = if host.contains(':') && !host.starts_with('[') {
            format!("[{host}]:{port}")
        } else {
            format!("{host}:{port}")
        };
        let ep = Endpoint::parse(&authority, port)
            .ok_or_else(|| Error::invalid(crate::t!("err.rm.bad_addr", addr = host)))?;
        if protocol.eq_ignore_ascii_case("ftp") {
            if user.contains(['\r', '\n', '\0']) || pass.contains(['\r', '\n', '\0']) {
                return Err(Error::invalid(crate::t!("err.rm.bad_addr", addr = host)));
            }
            let (user, pass) = if user.is_empty() {
                ("anonymous", "ps5gfc@")
            } else {
                (user, pass)
            };
            Ok(Client::Ftp(Ftp::new(super::ftp::FtpConfig {
                ep,
                user: user.to_string(),
                pass: pass.to_string(),
            })))
        } else {
            Ok(Client::Prospero(Prospero::new(ep)))
        }
    }

    pub fn protocol(&self) -> Protocol {
        match self {
            Client::Prospero(_) => Protocol::Prospero,
            Client::Ftp(_) => Protocol::Ftp,
        }
    }

    pub fn endpoint(&self) -> &Endpoint {
        match self {
            Client::Prospero(p) => p.endpoint(),
            Client::Ftp(f) => f.endpoint(),
        }
    }

    pub fn caps(&self) -> Caps {
        match self {
            Client::Prospero(p) => p.caps(),
            Client::Ftp(f) => f.caps(),
        }
    }

    pub fn with_busy_wait(self, d: Duration) -> Self {
        match self {
            Client::Prospero(p) => Client::Prospero(p.with_busy_wait(d)),
            f => f,
        }
    }

    pub fn retry_busy<T>(&self, cancel: Option<&Cancel>, f: impl FnMut() -> Result<T>) -> Result<T> {
        match self {
            Client::Prospero(p) => p.retry_busy(cancel, f),
            Client::Ftp(_) => {
                let mut f = f;
                f()
            }
        }
    }

    pub fn connect(&self) -> Result<ConsoleInfo> {
        match self {
            Client::Prospero(p) => p.connect(),
            Client::Ftp(f) => f.connect(),
        }
    }

    pub fn storage(&self) -> Result<Vec<StorageVolume>> {
        match self {
            Client::Prospero(p) => p.storage(),
            Client::Ftp(f) => f.storage(),
        }
    }

    pub fn volume_of<'a>(&self, volumes: &'a [StorageVolume], path: &str) -> Option<&'a StorageVolume> {
        volumes
            .iter()
            .filter(|v| rp::is_within(path, &v.path))
            .max_by_key(|v| v.path.len())
    }

    pub fn list(&self, path: &str) -> Result<Vec<Entry>> {
        match self {
            Client::Prospero(p) => p.list(path),
            Client::Ftp(f) => f.list(path),
        }
    }

    pub fn stat(&self, path: &str) -> Result<Option<Entry>> {
        match self {
            Client::Prospero(p) => p.stat(path),
            Client::Ftp(f) => f.stat(path),
        }
    }

    pub fn dir_size(&self, path: &str) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.dir_size(path),
            Client::Ftp(f) => f.dir_size(path),
        }
    }

    pub fn preflight(&self, path: &str, size: u64, is_dir: bool) -> Result<Preflight> {
        match self {
            Client::Prospero(p) => p.preflight(path, size, is_dir),
            Client::Ftp(f) => f.preflight(path, size, is_dir),
        }
    }

    pub fn conflicts(&self, paths: &[String]) -> Result<Vec<String>> {
        match self {
            Client::Prospero(p) => p.conflicts(paths),
            Client::Ftp(f) => f.conflicts(paths),
        }
    }

    pub fn unique_names(&self, paths: &[String]) -> Result<Vec<String>> {
        match self {
            Client::Prospero(p) => p.unique_names(paths),
            Client::Ftp(f) => f.unique_names(paths),
        }
    }

    pub fn mkdir(&self, path: &str) -> Result<()> {
        match self {
            Client::Prospero(p) => p.mkdir(path),
            Client::Ftp(f) => f.mkdir(path),
        }
    }

    pub fn mkdir_all(&self, path: &str, cancel: Option<&Cancel>) -> Result<()> {
        match self {
            Client::Prospero(p) => p.mkdir_all(path, cancel),
            Client::Ftp(f) => f.mkdir_all(path, cancel),
        }
    }

    pub fn rename(&self, from: &str, to: &str) -> Result<()> {
        match self {
            Client::Prospero(p) => p.rename(from, to),
            Client::Ftp(f) => f.rename(from, to),
        }
    }

    pub fn chmod(&self, path: &str, mode: u32) -> Result<()> {
        match self {
            Client::Prospero(p) => p.chmod(path, mode),
            Client::Ftp(f) => f.chmod(path, mode),
        }
    }

    /// Applies `mode` to every file and folder inside `path` (not `path` itself). Protocol-agnostic:
    /// it walks the tree with `list()` and calls `chmod()` one entry at a time, since neither backend
    /// has a native recursive chmod. Returns how many entries were touched.
    pub fn chmod_tree(&self, path: &str, mode: u32) -> Result<u64> {
        let mut count = 0u64;
        let mut stack = vec![rp::norm(path)];
        while let Some(dir) = stack.pop() {
            for e in self.list(&dir)? {
                let p = rp::join(&dir, &e.name);
                self.chmod(&p, mode)?;
                count += 1;
                if e.is_dir {
                    stack.push(p);
                }
            }
        }
        Ok(count)
    }

    pub fn delete(&self, path: &str) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.delete(path),
            Client::Ftp(f) => f.delete(path),
        }
    }

    pub fn copy_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.copy_to(source, destination, policy),
            Client::Ftp(f) => f.copy_to(source, destination, policy),
        }
    }

    pub fn move_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.move_to(source, destination, policy),
            Client::Ftp(f) => f.move_to(source, destination, policy),
        }
    }

    pub fn zip(&self, paths: &[String], dir: &str, name: &str, compression: &str) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.zip(paths, dir, name, compression),
            Client::Ftp(_) => Err(no_zip()),
        }
    }

    pub fn unzip(
        &self,
        path: &str,
        destination: Option<&str>,
        policy: Conflict,
        delete_source: bool,
        password: Option<&str>,
    ) -> Result<u64> {
        match self {
            Client::Prospero(p) => p.unzip(path, destination, policy, delete_source, password),
            Client::Ftp(_) => Err(no_zip()),
        }
    }

    pub fn jobs(&self) -> Result<Vec<Job>> {
        match self {
            Client::Prospero(p) => p.jobs(),
            Client::Ftp(f) => f.jobs(),
        }
    }

    pub fn cancel_job(&self, id: u64) -> Result<()> {
        match self {
            Client::Prospero(p) => p.cancel_job(id),
            Client::Ftp(f) => f.cancel_job(id),
        }
    }

    pub fn clear_jobs(&self) -> Result<()> {
        match self {
            Client::Prospero(p) => p.clear_jobs(),
            Client::Ftp(f) => f.clear_jobs(),
        }
    }

    pub fn wait_job(&self, id: u64, cancel: &Cancel, on_tick: impl FnMut(&Job)) -> Result<Job> {
        match self {
            Client::Prospero(p) => p.wait_job(id, cancel, on_tick),
            Client::Ftp(f) => f.wait_job(id, cancel, on_tick),
        }
    }

    pub fn delete_and_wait(&self, path: &str, cancel: &Cancel) -> Result<()> {
        let id = self.delete(path)?;
        let j = self.wait_job(id, cancel, |_| {})?;
        if j.failed() {
            return Err(Error::Remote(RemoteError::from_response(500, &j.error)));
        }
        Ok(())
    }

    pub fn read_text(&self, path: &str) -> Result<TextFile> {
        match self {
            Client::Prospero(p) => p.read_text(path),
            Client::Ftp(f) => f.read_text(path),
        }
    }

    pub fn write_text(&self, path: &str, text: &str, version: &str, newline: &str, bom: bool) -> Result<String> {
        match self {
            Client::Prospero(p) => p.write_text(path, text, version, newline, bom),
            Client::Ftp(f) => f.write_text(path, text, version, newline, bom),
        }
    }

    pub fn open_download(&self, path: &str) -> Result<(Download, u64)> {
        match self {
            Client::Prospero(p) => p.open_download(path).map(|(r, n)| (Download::Http(r), n)),
            Client::Ftp(f) => f.open_download(path).map(|(r, n)| (Download::Ftp(r), n)),
        }
    }

    pub fn upload_direct(
        &self,
        path: &str,
        size: u64,
        src: &mut dyn Read,
        overwrite: bool,
        hook: Option<SentHook<'_>>,
    ) -> Result<()> {
        match self {
            Client::Prospero(p) => p.upload_direct(path, size, src, overwrite, hook),
            Client::Ftp(f) => f.put(path, size, src, overwrite, hook),
        }
    }
}
