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
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{sync_channel, SyncSender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use super::client::Client;
use super::ftp::FtpUpload;
use super::prospero::{ChunkReq, Prospero};
use super::{cancel_io, path as rp, RemoteError, RemoteKind};
use crate::ctl::Cancel;
use crate::io::ReadAt;
use crate::util::MIB;
use crate::{Error, Result};

pub const DIRECT_MAX: u64 = 128 * MIB;

pub const CHUNK: usize = 32 * 1024 * 1024;

pub(crate) const MAX_ATTEMPTS: u32 = 6;
pub(crate) const BUSY_PATIENCE: Duration = Duration::from_secs(120);

pub trait SeqSink {
    fn write_all(&mut self, buf: &[u8]) -> Result<()>;
}

impl SeqSink for Vec<u8> {
    fn write_all(&mut self, buf: &[u8]) -> Result<()> {
        self.extend_from_slice(buf);
        Ok(())
    }
}

#[derive(Clone)]
pub(crate) struct Stop {
    user: Cancel,
    abandon: Cancel,
}

impl Stop {
    pub(crate) fn is_set(&self) -> bool {
        self.user.is_cancelled() || self.abandon.is_cancelled()
    }
    pub(crate) fn check(&self) -> Result<()> {
        if self.is_set() {
            Err(Error::Cancelled)
        } else {
            Ok(())
        }
    }
}

fn new_upload_id() -> String {
    static N: AtomicU64 = AtomicU64::new(0);
    let t = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!(
        "g{:x}-{:x}-{:x}",
        t,
        std::process::id(),
        N.fetch_add(1, Ordering::Relaxed)
    )
}

pub(crate) fn nap(d: Duration, is_set: &dyn Fn() -> bool) -> Result<()> {
    let t0 = Instant::now();
    while t0.elapsed() < d {
        if is_set() {
            return Err(Error::Cancelled);
        }
        std::thread::sleep(Duration::from_millis(50).min(d));
    }
    Ok(())
}

pub(crate) fn backoff(attempt: u32) -> Duration {
    Duration::from_millis(500u64 << attempt.min(4)).min(Duration::from_secs(8))
}

pub(crate) fn retryable(e: &RemoteError) -> bool {
    match e.kind {
        RemoteKind::Unreachable | RemoteKind::Busy => true,
        RemoteKind::Other => e.message.contains("checkpoint") || e.message.contains("offset"),
        _ => false,
    }
}

pub struct ChunkedUpload {
    client: Prospero,
    path: String,
    id: String,
    total: u64,
    overwrite: bool,

    sent: u64,
    attempted: bool,
    complete: bool,
}

impl ChunkedUpload {
    pub fn new(client: &Prospero, path: &str, total: u64, overwrite: bool) -> Self {
        Self {
            client: client.clone(),
            path: rp::norm(path),
            id: new_upload_id(),
            total,
            overwrite,
            sent: 0,
            attempted: false,
            complete: false,
        }
    }

    pub fn sent(&self) -> u64 {
        self.sent
    }

    pub fn is_complete(&self) -> bool {
        self.complete
    }

    fn partial_name(&self) -> String {
        format!("{}.pmgr-part-{}", rp::file_name(&self.path), self.id)
    }

    fn partial_size(&self) -> Result<Option<u64>> {
        let name = self.partial_name();
        Ok(self
            .client
            .list(&rp::parent(&self.path))?
            .into_iter()
            .find(|e| e.name == name)
            .map(|e| e.size))
    }

    fn send_block(&mut self, data: &[u8], stop: &Stop, on_bytes: &dyn Fn(u64)) -> Result<()> {
        let start = self.sent;
        let end = start + data.len() as u64;
        if data.is_empty() || end > self.total {
            return Err(Error::invalid(format!(
                "bloco fora do tamanho do arquivo ({end} > {})",
                self.total
            )));
        }
        self.attempted = true;
        let mut from = start;
        let mut attempt = 0u32;
        let reported = std::cell::Cell::new(0u64);
        let busy_since = Instant::now();
        loop {
            stop.check()?;
            let base = from - start;
            let body = &data[base as usize..];
            let mut hook = |n: u64| -> io::Result<()> {
                if stop.is_set() {
                    return Err(cancel_io());
                }
                let upto = base + n;
                if upto > reported.get() {
                    on_bytes(upto - reported.get());
                    reported.set(upto);
                }
                Ok(())
            };
            let req = ChunkReq {
                path: &self.path,
                upload_id: &self.id,
                offset: from,
                total: self.total,
                overwrite: self.overwrite,
            };
            match self.client.post_chunk(&req, body, Some(&mut hook)) {
                Ok(ack) => {
                    if ack.next_offset != end {
                        return Err(Error::Remote(RemoteError::new(
                            RemoteKind::Protocol,
                            0,
                            format!("o console confirmou {} em vez de {end}", ack.next_offset),
                        )));
                    }
                    if (end - start) > reported.get() {
                        on_bytes(end - start - reported.get());
                    }
                    self.sent = end;
                    self.complete = end == self.total;
                    return Ok(());
                }
                Err(Error::Remote(e)) if retryable(&e) => {
                    let busy = e.kind == RemoteKind::Busy;
                    attempt += 1;
                    if (busy && busy_since.elapsed() > BUSY_PATIENCE) || (!busy && attempt > MAX_ATTEMPTS) {
                        return Err(Error::Remote(e));
                    }
                    nap(if busy { Duration::from_secs(2) } else { backoff(attempt) }, &|| {
                        stop.is_set()
                    })?;

                    match self.partial_size() {
                        Ok(Some(s)) if s >= start && s <= end => from = if s == end { start } else { s },
                        Ok(Some(s)) => {
                            return Err(Error::Remote(RemoteError::new(
                                RemoteKind::Protocol,
                                0,
                                format!(
                                    "o arquivo parcial no console tem {s} bytes, fora do esperado ({start}..{end})"
                                ),
                            )))
                        }
                        Ok(None) => from = start,
                        Err(_) => {}
                    }
                }
                Err(e) => return Err(e),
            }
        }
    }

    fn abort_partial(&mut self) {
        if !self.attempted || self.complete {
            return;
        }
        self.complete = true;
        let quiet = Cancel::new();
        let never = || false;
        for attempt in 0..4u32 {
            let s = match self.partial_size() {
                Ok(Some(s)) => s,
                Ok(None) => 0,
                Err(_) => {
                    let _ = nap(Duration::from_millis(400), &never);
                    continue;
                }
            };
            let req = ChunkReq {
                path: &self.path,
                upload_id: &self.id,
                offset: s,
                total: s + 1,
                overwrite: false,
            };
            match self.client.post_chunk(&req, &[0u8], None) {
                Ok(ack) if ack.complete => {
                    let _ = self.client.delete_and_wait(&self.path, &quiet);
                    return;
                }
                Ok(_) => return,

                Err(Error::Remote(e)) if e.kind == RemoteKind::Exists => return,
                Err(Error::Remote(e)) if retryable(&e) => {
                    let _ = nap(Duration::from_millis(600 << attempt.min(3)), &never);
                }
                Err(_) => return,
            }
        }
    }
}

pub(crate) trait Uploader: Send {
    fn send_chunk(&mut self, data: &[u8], stop: &Stop, on_bytes: &dyn Fn(u64)) -> Result<()>;

    fn is_complete(&self) -> bool;

    fn abort(&mut self);
}

impl Uploader for ChunkedUpload {
    fn send_chunk(&mut self, data: &[u8], stop: &Stop, on_bytes: &dyn Fn(u64)) -> Result<()> {
        self.send_block(data, stop, on_bytes)
    }
    fn is_complete(&self) -> bool {
        self.complete
    }
    fn abort(&mut self) {
        self.abort_partial()
    }
}

#[derive(Debug, Clone, Copy, Default)]
pub struct DirectOpts {
    pub overwrite: bool,

    pub want_crc: bool,
}

pub fn upload_direct(
    client: &Client,
    path: &str,
    size: u64,
    src: &dyn ReadAt,
    opts: DirectOpts,
    cancel: &Cancel,
    on_bytes: &dyn Fn(u64),
) -> Result<Option<u32>> {
    let DirectOpts { overwrite, want_crc } = opts;
    let mut attempt = 0u32;
    let busy_since = Instant::now();
    loop {
        cancel.check()?;
        let reported = std::cell::Cell::new(0u64);
        let mut reader = PosReader {
            src,
            pos: 0,
            len: size,
            err: None,
            crc: want_crc.then(crc32fast::Hasher::new),
        };
        let mut hook = |n: u64| -> io::Result<()> {
            if cancel.is_cancelled() {
                return Err(cancel_io());
            }
            if n > reported.get() {
                on_bytes(n - reported.get());
                reported.set(n);
            }
            Ok(())
        };
        let res = client.upload_direct(path, size, &mut reader, overwrite, Some(&mut hook));

        if let Some(e) = reader.err.take() {
            return Err(Error::Io(e));
        }
        match res {
            Ok(()) => {
                if size > reported.get() {
                    on_bytes(size - reported.get());
                }
                return Ok(reader.crc.take().map(|h| h.finalize()));
            }
            Err(Error::Remote(e)) if retryable(&e) => {
                let busy = e.kind == RemoteKind::Busy;
                attempt += 1;
                if (busy && busy_since.elapsed() > BUSY_PATIENCE) || (!busy && attempt > 3) {
                    return Err(Error::Remote(e));
                }
                nap(if busy { Duration::from_secs(2) } else { backoff(attempt) }, &|| {
                    cancel.is_cancelled()
                })?;
            }
            Err(e) => return Err(e),
        }
    }
}

struct PosReader<'a> {
    src: &'a dyn ReadAt,
    pos: u64,
    len: u64,
    err: Option<io::Error>,
    crc: Option<crc32fast::Hasher>,
}

impl Read for PosReader<'_> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.pos >= self.len || buf.is_empty() {
            return Ok(0);
        }
        let want = buf.len().min((self.len - self.pos) as usize);
        match self.src.read_at(self.pos, &mut buf[..want]) {
            Ok(n) => {
                self.pos += n as u64;
                if let Some(h) = self.crc.as_mut() {
                    h.update(&buf[..n]);
                }
                Ok(n)
            }
            Err(e) => {
                self.err = Some(io::Error::new(e.kind(), e.to_string()));
                Err(e)
            }
        }
    }
}

struct Shared {
    err: Mutex<Option<Error>>,
}

pub struct RemoteWriter {
    client: Client,
    path: String,
    total: u64,
    overwrite: bool,
    cancel: Cancel,

    abandon: Cancel,
    tx: Option<SyncSender<Vec<u8>>>,
    handle: Option<JoinHandle<()>>,
    shared: Arc<Shared>,
    buf: Vec<u8>,
    written: u64,
    chunk: usize,
}

impl RemoteWriter {
    pub fn create(
        client: &Client,
        path: &str,
        total: u64,
        overwrite: bool,
        cancel: &Cancel,
        on_bytes: Arc<dyn Fn(u64) + Send + Sync>,
    ) -> Result<Self> {
        Self::with_chunk(client, path, total, overwrite, cancel, on_bytes, CHUNK)
    }

    pub fn with_chunk(
        client: &Client,
        path: &str,
        total: u64,
        overwrite: bool,
        cancel: &Cancel,
        on_bytes: Arc<dyn Fn(u64) + Send + Sync>,
        chunk: usize,
    ) -> Result<Self> {
        let chunk = chunk.max(1);
        let abandon = Cancel::new();
        let shared = Arc::new(Shared { err: Mutex::new(None) });
        let (tx, handle) = if total == 0 {
            (None, None)
        } else {
            let (tx, rx) = sync_channel::<Vec<u8>>(2);
            let mut up: Box<dyn Uploader> = match client {
                Client::Prospero(p) => Box::new(ChunkedUpload::new(p, path, total, overwrite)),
                Client::Ftp(f) => Box::new(FtpUpload::new(f, path, total, overwrite)),
            };
            let (sh, stop) = (
                shared.clone(),
                Stop {
                    user: cancel.clone(),
                    abandon: abandon.clone(),
                },
            );
            let h = std::thread::Builder::new()
                .name("ps5gfc-upload".into())
                .spawn(move || {
                    let mut failed = false;
                    for data in rx {
                        if failed {
                            continue;
                        }
                        if let Err(e) = up.send_chunk(&data, &stop, &*on_bytes) {
                            sh.err.lock().unwrap().get_or_insert(e);
                            failed = true;
                        }
                    }

                    if !up.is_complete() {
                        up.abort();
                    }
                })
                .map_err(Error::Io)?;
            (Some(tx), Some(h))
        };
        Ok(Self {
            client: client.clone(),
            path: rp::norm(path),
            total,
            overwrite,
            cancel: cancel.clone(),
            abandon,
            tx,
            handle,
            shared,
            buf: Vec::with_capacity(chunk.min(total as usize)),
            written: 0,
            chunk,
        })
    }

    fn take_error(&self) -> Option<Error> {
        self.shared.err.lock().unwrap().take()
    }

    fn dispatch(&mut self) -> Result<()> {
        if self.buf.is_empty() {
            return Ok(());
        }
        let data = std::mem::replace(&mut self.buf, Vec::with_capacity(self.chunk));
        let tx = self
            .tx
            .as_ref()
            .ok_or_else(|| Error::invalid("escritor já encerrado"))?;
        if tx.send(data).is_err() {
            return Err(self
                .take_error()
                .unwrap_or_else(|| Error::invalid("o envio ao console foi interrompido")));
        }
        Ok(())
    }

    pub fn finish(mut self) -> Result<()> {
        if self.written != self.total {
            let msg = format!(
                "o arquivo gerado tem {} bytes, mas {} foram anunciados ao console",
                self.written, self.total
            );
            self.abandon.cancel();
            self.close();
            return Err(Error::invalid(msg));
        }
        if self.total == 0 {
            let empty = crate::io::MemReader::new(Vec::new());
            let opts = DirectOpts {
                overwrite: self.overwrite,
                want_crc: false,
            };
            return upload_direct(&self.client, &self.path, 0, &empty, opts, &self.cancel, &|_| {}).map(|_| ());
        }
        let res = self.dispatch();
        if res.is_err() {
            self.abandon.cancel();
        }
        self.close();
        match (res, self.take_error()) {
            (_, Some(e)) => Err(e),
            (Err(e), None) => Err(e),
            _ => Ok(()),
        }
    }

    fn close(&mut self) {
        self.tx.take();
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

impl SeqSink for RemoteWriter {
    fn write_all(&mut self, mut data: &[u8]) -> Result<()> {
        if self.written + data.len() as u64 > self.total {
            return Err(Error::invalid("mais bytes do que o tamanho anunciado ao console"));
        }
        if let Some(e) = self.take_error() {
            return Err(e);
        }
        self.cancel.check()?;
        self.written += data.len() as u64;
        while !data.is_empty() {
            let room = self.chunk - self.buf.len();
            let n = room.min(data.len());
            self.buf.extend_from_slice(&data[..n]);
            data = &data[n..];
            if self.buf.len() >= self.chunk {
                self.dispatch()?;
            }
        }
        Ok(())
    }
}

impl Drop for RemoteWriter {
    fn drop(&mut self) {
        if self.handle.is_some() {
            self.abandon.cancel();
        }
        self.close();
    }
}
