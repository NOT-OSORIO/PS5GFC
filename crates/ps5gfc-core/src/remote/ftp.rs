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

use std::io::{self, BufRead, BufReader, Read, Write};
use std::net::{IpAddr, Shutdown, TcpStream};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use super::http::{connect as tcp_connect, Endpoint, SentHook, Timeouts};
use super::path as rp;
use super::prospero::{
    map_io, Caps, Conflict, ConsoleInfo, Entry, Job, Preflight, StorageVolume, SystemInfo, TextFile,
};
use super::upload::{backoff, nap, retryable, Stop, Uploader, BUSY_PATIENCE, MAX_ATTEMPTS};
use super::{cancel_io, RemoteError, RemoteKind};
use crate::ctl::Cancel;
use crate::{Error, Result};

pub const DEFAULT_FTP_PORT: u16 = 2121;

pub const FTP_PORTS: [u16; 3] = [2121, 1337, 21];

const SLICE: usize = 256 * 1024;

const RECENT_KEEP: usize = 8 * 1024 * 1024;

const LISTING_LIMIT: usize = 128 * 1024 * 1024;

const TEXT_LIMIT: u64 = 8 * 1024 * 1024;

const IDLE_KEEP: usize = 8;
const TEMP_MARK: &str = ".ps5gfc-part-";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FtpConfig {
    pub ep: Endpoint,
    pub user: String,
    pub pass: String,
}

impl FtpConfig {
    pub fn parse(input: &str) -> Option<FtpConfig> {
        let mut s = input.trim();
        for p in ["ftp://", "FTP://"] {
            if let Some(rest) = s.strip_prefix(p) {
                s = rest;
            }
        }
        let s = s.split(['/', '?', '#']).next().unwrap_or("");
        let (creds, hostport) = match s.rsplit_once('@') {
            Some((c, h)) => (Some(c), h),
            None => (None, s),
        };
        let (user, pass) = match creds {
            Some(c) => match c.split_once(':') {
                Some((u, p)) => (u.to_string(), p.to_string()),
                None => (c.to_string(), String::new()),
            },
            None => ("anonymous".to_string(), "ps5gfc@".to_string()),
        };
        if user.contains(['\r', '\n', '\0']) || pass.contains(['\r', '\n', '\0']) {
            return None;
        }
        let ep = Endpoint::parse(hostport, DEFAULT_FTP_PORT)?;
        Some(FtpConfig { ep, user, pass })
    }
}

fn ftp_error(code: u16, text: &str) -> Error {
    let m = text.to_ascii_lowercase();
    let has = |needles: &[&str]| needles.iter().any(|n| m.contains(n));
    let kind = if matches!(code, 421 | 425 | 426) {
        RemoteKind::Unreachable
    } else if matches!(code, 452 | 552)
        || has(&[
            "no space",
            "space left",
            "quota",
            "disk full",
            "insufficient",
            "not enough space",
        ])
    {
        RemoteKind::NoSpace
    } else if has(&[
        "no such",
        "not found",
        "does not exist",
        "doesn't exist",
        "not exist",
        "cannot find",
    ]) {
        RemoteKind::NotFound
    } else if has(&["already exist", "file exists", "exists"]) {
        RemoteKind::Exists
    } else if matches!(code, 530 | 532 | 553)
        || has(&[
            "permission",
            "denied",
            "not allowed",
            "not permitted",
            "read-only",
            "read only",
            "refus",
        ])
    {
        RemoteKind::Denied
    } else if code == 450 && has(&["busy", "in use"]) {
        RemoteKind::Busy
    } else if code == 550 && !has(&["not a directory", "is a directory", "not empty"]) {
        RemoteKind::NotFound
    } else {
        RemoteKind::Other
    };
    let message = if text.trim().is_empty() {
        format!("FTP {code}")
    } else {
        text.trim().to_string()
    };
    Error::Remote(RemoteError::new(kind, code, message))
}

fn unreachable_msg(msg: impl Into<String>) -> Error {
    Error::Remote(RemoteError::new(RemoteKind::Unreachable, 0, msg))
}

fn protocol(msg: impl Into<String>) -> Error {
    Error::Remote(RemoteError::new(RemoteKind::Protocol, 0, msg))
}

fn unsupported(msg: impl Into<String>) -> Error {
    Error::Remote(RemoteError::new(RemoteKind::Other, 0, msg))
}

struct Reply {
    code: u16,
    lines: Vec<String>,
}

impl Reply {
    fn text(&self) -> String {
        self.lines.join("\n")
    }
    fn first(&self) -> &str {
        self.lines.first().map(String::as_str).unwrap_or("")
    }
    fn is_ok(&self) -> bool {
        (200..300).contains(&self.code)
    }
    fn error(&self) -> Error {
        ftp_error(self.code, &self.text())
    }
}

#[derive(Clone, Copy)]
struct Feats {
    known: bool,
    mlsd: bool,
    mlst: bool,

    unix_mode: bool,

    self_toggle: bool,

    epsv: Option<bool>,
}

struct Session {
    rd: BufReader<TcpStream>,
    peer: IpAddr,
    last_used: Instant,

    dirty: bool,
    dead: bool,
}

impl Session {
    fn sock(&mut self) -> &mut TcpStream {
        self.rd.get_mut()
    }

    fn set_reply_timeout(&mut self, d: Duration) {
        let _ = self.sock().set_read_timeout(Some(d));
    }

    fn alive(&mut self) -> bool {
        if self.dead || !self.rd.buffer().is_empty() {
            return false;
        }
        let s = self.rd.get_ref();
        if s.set_nonblocking(true).is_err() {
            return false;
        }
        let mut b = [0u8; 1];
        let r = s.peek(&mut b);
        let _ = s.set_nonblocking(false);
        matches!(r, Err(ref e) if e.kind() == io::ErrorKind::WouldBlock)
    }

    fn write_line(&mut self, line: &str) -> io::Result<()> {
        let mut b = Vec::with_capacity(line.len() + 2);
        b.extend_from_slice(line.as_bytes());
        b.extend_from_slice(b"\r\n");
        let s = self.sock();
        s.write_all(&b)?;
        s.flush()
    }

    fn read_reply(&mut self) -> io::Result<Reply> {
        let mut lines: Vec<String> = Vec::new();
        let mut code = 0u16;
        let mut closing: Option<String> = None;
        for _ in 0..4000 {
            let mut raw = Vec::new();
            if self.rd.read_until(b'\n', &mut raw)? == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "o servidor FTP fechou a conexão",
                ));
            }
            let line = String::from_utf8_lossy(&raw).trim_end_matches(['\r', '\n']).to_string();
            match &closing {
                None => {
                    let b = line.as_bytes();
                    if b.len() < 3 || !b[..3].iter().all(u8::is_ascii_digit) {
                        return Err(io::Error::new(io::ErrorKind::InvalidData, "resposta que não é FTP"));
                    }
                    code = line[..3].parse().unwrap_or(0);
                    if b.len() > 3 && b[3] == b'-' {
                        closing = Some(format!("{} ", &line[..3]));
                        lines.push(line[4..].to_string());
                        continue;
                    }
                    lines.push(line.get(4..).unwrap_or("").to_string());
                    return Ok(Reply { code, lines });
                }
                Some(end) => {
                    if line.starts_with(end.as_str()) {
                        lines.push(line[4..].to_string());
                        return Ok(Reply { code, lines });
                    }

                    let mid = line
                        .strip_prefix(&end[..3])
                        .and_then(|r| r.strip_prefix('-'))
                        .map(str::to_string);
                    lines.push(mid.unwrap_or(line));
                }
            }
        }
        Err(io::Error::new(io::ErrorKind::InvalidData, "resposta FTP longa demais"))
    }

    fn reply(&mut self) -> Result<Reply> {
        match self.read_reply() {
            Ok(r) => Ok(r),
            Err(e) => {
                self.dead = true;
                Err(map_io(e))
            }
        }
    }

    fn raw(&mut self, line: &str) -> Result<Reply> {
        if line.bytes().any(|b| matches!(b, b'\r' | b'\n' | 0)) {
            return Err(Error::invalid(crate::t!(
                "err.unsafe_path",
                path = line.replace(['\r', '\n', '\0'], "?")
            )));
        }
        if let Err(e) = self.write_line(line) {
            self.dead = true;
            return Err(map_io(e));
        }
        self.reply()
    }

    fn expect(&mut self, line: &str, ok: &[u16]) -> Result<Reply> {
        let r = self.raw(line)?;
        if ok.contains(&r.code) {
            Ok(r)
        } else {
            Err(r.error())
        }
    }

    fn expect_ok(&mut self, line: &str) -> Result<Reply> {
        let r = self.raw(line)?;
        if r.is_ok() {
            Ok(r)
        } else {
            Err(r.error())
        }
    }

    fn open(ftp: &Ftp) -> Result<Session> {
        let inner = &ftp.inner;
        let stream = tcp_connect(&inner.cfg.ep, &inner.tm).map_err(map_io)?;
        let _ = stream.set_read_timeout(Some(inner.reply_timeout));
        let peer = stream.peer_addr().map_err(map_io)?.ip();
        let mut s = Session {
            rd: BufReader::new(stream),
            peer,
            last_used: Instant::now(),
            dirty: false,
            dead: false,
        };
        let mut hello = s.reply()?;
        if hello.code == 120 {
            hello = s.reply()?;
        }
        if hello.code != 220 {
            return Err(if hello.code >= 400 {
                hello.error()
            } else {
                protocol(format!("saudação FTP inesperada ({})", hello.code))
            });
        }
        *inner.banner.lock().unwrap() = hello.text();
        let user = inner.cfg.user.clone();
        let r = s.raw(&format!("USER {user}"))?;
        match r.code {
            230 => {}
            331 | 332 => {
                let pass = inner.cfg.pass.clone();
                let r = s.raw(&format!("PASS {pass}"))?;
                if r.code != 230 && r.code != 202 {
                    return Err(Error::Remote(RemoteError::new(
                        RemoteKind::Denied,
                        r.code,
                        format!("login recusado: {}", r.first()),
                    )));
                }
            }
            _ => {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Denied,
                    r.code,
                    format!("login recusado: {}", r.first()),
                )))
            }
        }
        s.expect_ok("TYPE I")?;

        let _ = s.raw("OPTS UTF8 ON");
        if !inner.feats.lock().unwrap().known {
            let mut f = Feats {
                known: true,
                mlsd: false,
                mlst: false,
                unix_mode: false,
                self_toggle: false,
                epsv: None,
            };
            if let Ok(r) = s.raw("FEAT") {
                if r.code == 211 {
                    for l in &r.lines {
                        let u = l.trim().to_ascii_uppercase();
                        if u.starts_with("MLSD") {
                            f.mlsd = true;
                        } else if u.starts_with("MLST") {
                            f.mlst = true;
                            f.mlsd = true;
                            f.unix_mode = u.contains("UNIX.MODE");
                        } else if u.starts_with("EPSV") {
                            f.epsv = Some(true);
                        } else if u == "SELF" {
                            f.self_toggle = true;
                        }
                    }
                }
            }
            *inner.feats.lock().unwrap() = f;
        }

        if inner.feats.lock().unwrap().self_toggle {
            for _ in 0..2 {
                match s.raw("SELF") {
                    Ok(r) if r.text().to_ascii_lowercase().contains("disabled") => break,
                    Ok(_) => {}
                    Err(e) => return Err(e),
                }
            }
        }
        s.last_used = Instant::now();
        Ok(s)
    }

    fn open_data(&mut self, ftp: &Ftp) -> Result<TcpStream> {
        let inner = &ftp.inner;
        let try_epsv = inner.feats.lock().unwrap().epsv != Some(false);
        let mut port: Option<u16> = None;
        if try_epsv {
            let r = self.raw("EPSV")?;
            if r.code == 229 {
                port = parse_epsv(&r.text());
                if port.is_none() {
                    return Err(protocol("resposta EPSV inválida"));
                }
            } else if r.code >= 500 {
                inner.feats.lock().unwrap().epsv = Some(false);
            } else {
                return Err(r.error());
            }
        }
        let port = match port {
            Some(p) => p,
            None => {
                let r = self.raw("PASV")?;
                if r.code != 227 {
                    return Err(r.error());
                }
                parse_pasv(&r.text()).ok_or_else(|| protocol("resposta PASV inválida"))?
            }
        };

        let ep = Endpoint {
            host: self.peer.to_string(),
            port,
        };
        let d = tcp_connect(&ep, &inner.tm).map_err(map_io)?;
        let _ = d.set_read_timeout(Some(inner.tm.write));
        let _ = d.set_write_timeout(Some(inner.tm.write));
        Ok(d)
    }

    fn start_xfer(&mut self, ftp: &Ftp, cmd: &str, rest: Option<u64>) -> Result<TcpStream> {
        let data = self.open_data(ftp)?;
        if let Some(r) = rest {
            self.expect(&format!("REST {r}"), &[350])?;
        }
        let rep = self.raw(cmd)?;
        if rep.code != 125 && rep.code != 150 {
            return Err(rep.error());
        }
        self.dirty = true;
        Ok(data)
    }

    fn finish_xfer(&mut self, wait: Duration) -> Result<()> {
        self.set_reply_timeout(wait);
        let rep = self.reply()?;
        self.dirty = false;
        if rep.code == 226 || rep.code == 250 {
            Ok(())
        } else {
            self.dead = rep.code == 421;
            Err(rep.error())
        }
    }

    fn explain(&mut self, e: io::Error) -> Error {
        if e.kind() == io::ErrorKind::Interrupted && e.to_string() == super::CANCEL_MARK {
            return Error::Cancelled;
        }
        self.set_reply_timeout(Duration::from_secs(3));
        if let Ok(rep) = self.read_reply() {
            if rep.code >= 400 {
                return rep.error();
            }
        }
        map_io(e)
    }

    fn listing(&mut self, ftp: &Ftp, cmd: &str) -> Result<Vec<u8>> {
        let mut data = self.start_xfer(ftp, cmd, None)?;
        let mut out = Vec::new();
        let mut buf = vec![0u8; 64 * 1024];
        let read: io::Result<()> = (|| loop {
            let n = data.read(&mut buf)?;
            if n == 0 {
                return Ok(());
            }
            if out.len() + n > LISTING_LIMIT {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "listagem grande demais"));
            }
            out.extend_from_slice(&buf[..n]);
        })();
        drop(data);
        if let Err(e) = read {
            self.dead = true;
            return Err(map_io(e));
        }
        self.finish_xfer(ftp.inner.reply_timeout)?;
        Ok(out)
    }
}

pub(crate) fn parse_pasv(text: &str) -> Option<u16> {
    let inner = match (text.find('('), text.rfind(')')) {
        (Some(a), Some(b)) if b > a => &text[a + 1..b],
        _ => text,
    };
    let nums: Vec<u32> = inner
        .split(|c: char| !c.is_ascii_digit())
        .filter(|s| !s.is_empty())
        .filter_map(|s| s.parse().ok())
        .collect();
    if nums.len() < 6 {
        return None;
    }
    let n = &nums[nums.len() - 6..];
    if n[4] > 255 || n[5] > 255 {
        return None;
    }
    let p = n[4] * 256 + n[5];
    (p != 0).then_some(p as u16)
}

pub(crate) fn parse_epsv(text: &str) -> Option<u16> {
    let a = text.find('(')?;
    let b = text[a..].find(')')? + a;
    let inner = &text[a + 1..b];
    let d = inner.chars().next()?;
    let p: u16 = inner.split(d).nth(3)?.trim().parse().ok()?;
    (p != 0).then_some(p)
}

fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (y + (m <= 2) as i64, m as u32, d as u32)
}

fn today() -> (i64, u32, u32) {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    civil_from_days(secs.div_euclid(86_400))
}

fn month_of(s: &str) -> Option<u32> {
    const M: [&str; 12] = [
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
    ];
    let l = s.to_ascii_lowercase();
    M.iter().position(|m| *m == l).map(|i| i as u32 + 1)
}

fn mode_from_perms(p: &str) -> u32 {
    let b = p.as_bytes();
    if b.len() < 10 {
        return 0;
    }
    let mut m = 0u32;
    for (i, bit) in [0o400, 0o200, 0o100, 0o040, 0o020, 0o010, 0o004, 0o002, 0o001]
        .into_iter()
        .enumerate()
    {
        let c = b[1 + i];
        let on = match i % 3 {
            2 => matches!(c, b'x' | b's' | b't'),
            _ => c != b'-',
        };
        if on {
            m |= bit;
        }
    }
    m
}

pub(crate) fn parse_unix_line(line: &str, now: (i64, u32, u32)) -> Option<(Entry, bool)> {
    let line = line.trim_end_matches(['\r', '\n']);
    let first = *line.as_bytes().first()?;
    if !matches!(first, b'-' | b'd' | b'l' | b'b' | b'c' | b'p' | b's') || line.len() < 11 {
        return None;
    }

    let mut toks: Vec<(usize, usize)> = Vec::with_capacity(9);
    let bytes = line.as_bytes();
    let mut i = 0;
    while i < bytes.len() && toks.len() < 9 {
        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() {
            break;
        }
        let s = i;
        while i < bytes.len() && !bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        toks.push((s, i));
    }
    let f = |k: usize| toks.get(k).map(|&(s, e)| &line[s..e]);

    let (size_i, month_i) = if f(5).and_then(month_of).is_some() {
        (4, 5)
    } else if f(4).and_then(month_of).is_some() {
        (3, 4)
    } else {
        return None;
    };
    let name_i = month_i + 3;
    let name_start = toks.get(name_i)?.0;
    let mut name = line[name_start..].to_string();
    let perms = f(0)?;
    let is_link = first == b'l';
    if is_link {
        if let Some((n, _)) = name.split_once(" -> ") {
            name = n.to_string();
        }
    }
    if name == "." || name == ".." || name.is_empty() {
        return None;
    }
    let size: u64 = f(size_i)?.parse().unwrap_or(0);
    let month = month_of(f(month_i)?)?;
    let day: u32 = f(month_i + 1)?.parse().ok()?;
    let ty = f(month_i + 2)?;
    let (year, hm) = if let Some((h, m)) = ty.split_once(':') {
        let (ty_, tm_, td_) = now;
        let y = if (month, day) > (tm_, td_ + 1) { ty_ - 1 } else { ty_ };
        (
            y,
            format!(
                "{:02}{:02}00",
                h.parse::<u32>().unwrap_or(0),
                m.parse::<u32>().unwrap_or(0)
            ),
        )
    } else {
        (ty.parse::<i64>().unwrap_or(1970), "000000".to_string())
    };
    let modified = format!("{year:04}{month:02}{day:02}{hm}");
    let is_dir = first == b'd';
    let mut mode = mode_from_perms(perms);
    if mode == 0 {
        mode = if is_dir { 0o755 } else { 0o644 };
    }
    Some((
        Entry {
            name,
            is_dir,
            size,
            mode,
            modified,
        },
        is_link,
    ))
}

pub(crate) fn parse_mlsd_line(line: &str) -> Option<(Entry, bool)> {
    let line = line.trim_end_matches(['\r', '\n']);
    let line = line.strip_prefix(' ').unwrap_or(line);
    let (facts, name) = line
        .split_once("; ")
        .or_else(|| line.split_once(' ').filter(|(f, _)| f.contains('=')))?;
    let mut ty = String::new();
    let (mut size, mut mode, mut modified) = (0u64, 0u32, String::new());
    for f in facts.split(';') {
        let Some((k, v)) = f.split_once('=') else { continue };
        match k.to_ascii_lowercase().as_str() {
            "type" => ty = v.to_ascii_lowercase(),
            "size" | "sizd" => size = v.parse().unwrap_or(0),
            "modify" => modified = v.chars().take(14).collect(),
            "unix.mode" => mode = u32::from_str_radix(v.trim_start_matches('0'), 8).unwrap_or(0),
            _ => {}
        }
    }
    if matches!(ty.as_str(), "cdir" | "pdir") || name == "." || name == ".." || name.is_empty() {
        return None;
    }
    let is_link = ty.contains("slink");
    let is_dir = ty == "dir";
    if mode == 0 {
        mode = if is_dir { 0o755 } else { 0o644 };
    }
    Some((
        Entry {
            name: name.to_string(),
            is_dir,
            size,
            mode,
            modified,
        },
        is_link,
    ))
}

pub(crate) fn parse_listing(bytes: &[u8], mlsd: bool) -> Vec<(Entry, bool)> {
    let text = String::from_utf8_lossy(bytes);
    let now = today();
    text.lines()
        .filter_map(|l| {
            if mlsd {
                parse_mlsd_line(l)
            } else {
                parse_unix_line(l, now)
            }
        })
        .filter(|(e, _)| rp::valid_name(&e.name))
        .collect()
}

fn crc_version(bytes: &[u8]) -> String {
    format!("{:08x}-{}", crc32fast::hash(bytes), bytes.len())
}

struct JobEntry {
    job: Job,
    cancel: Cancel,
}

struct Inner {
    cfg: FtpConfig,
    tm: Timeouts,

    reply_timeout: Duration,
    idle: Mutex<Vec<Session>>,
    feats: Mutex<Feats>,
    banner: Mutex<String>,
    jobs: Mutex<Vec<JobEntry>>,
    next_job: AtomicU64,
    next_tmp: AtomicU64,
}

#[derive(Clone)]
pub struct Ftp {
    inner: Arc<Inner>,
}

struct JobCtx {
    id: u64,
    ftp: Ftp,
    cancel: Cancel,
}

impl JobCtx {
    fn update(&self, f: impl FnOnce(&mut Job)) {
        let mut g = self.ftp.inner.jobs.lock().unwrap();
        if let Some(e) = g.iter_mut().find(|e| e.job.id == self.id) {
            f(&mut e.job);
        }
    }
}

pub struct FtpReader {
    ftp: Ftp,
    sess: Option<Session>,
    data: Option<TcpStream>,
}

impl Read for FtpReader {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let Some(data) = self.data.as_mut() else { return Ok(0) };
        let n = data.read(buf)?;
        if n == 0 {
            self.data = None;
            if let Some(mut s) = self.sess.take() {
                match s.finish_xfer(self.ftp.inner.reply_timeout) {
                    Ok(()) => self.ftp.checkin(s),
                    Err(e) => return Err(io::Error::new(io::ErrorKind::ConnectionAborted, e.localized())),
                }
            }
        }
        Ok(n)
    }
}

impl Ftp {
    pub fn new(cfg: FtpConfig) -> Self {
        Self {
            inner: Arc::new(Inner {
                cfg,
                tm: Timeouts::default(),
                reply_timeout: Duration::from_secs(60),
                idle: Mutex::new(Vec::new()),
                feats: Mutex::new(Feats {
                    known: false,
                    mlsd: false,
                    mlst: false,
                    unix_mode: false,
                    self_toggle: false,
                    epsv: None,
                }),
                banner: Mutex::new(String::new()),
                jobs: Mutex::new(Vec::new()),
                next_job: AtomicU64::new(1),
                next_tmp: AtomicU64::new(0),
            }),
        }
    }

    pub fn connect_to(addr: &str) -> Result<Self> {
        let cfg = FtpConfig::parse(addr).ok_or_else(|| Error::invalid(crate::t!("err.rm.bad_addr", addr = addr)))?;
        Ok(Self::new(cfg))
    }

    pub fn endpoint(&self) -> &Endpoint {
        &self.inner.cfg.ep
    }

    pub fn caps(&self) -> Caps {
        Caps {
            zip: false,
            chmod: true,
            storage: false,
            server_copy: false,
        }
    }

    fn checkout(&self) -> Result<Session> {
        loop {
            let cand = self.inner.idle.lock().unwrap().pop();
            match cand {
                Some(mut s) => {
                    if !s.alive() {
                        continue;
                    }

                    if s.last_used.elapsed() > Duration::from_secs(20) {
                        s.set_reply_timeout(Duration::from_secs(5));
                        let ok = matches!(s.raw("NOOP"), Ok(r) if r.code == 200);
                        s.set_reply_timeout(self.inner.reply_timeout);
                        if !ok {
                            continue;
                        }
                    }
                    return Ok(s);
                }
                None => return Session::open(self),
            }
        }
    }

    fn checkin(&self, mut s: Session) {
        if s.dead || s.dirty {
            return;
        }
        s.set_reply_timeout(self.inner.reply_timeout);
        s.last_used = Instant::now();
        let mut g = self.inner.idle.lock().unwrap();
        if g.len() < IDLE_KEEP {
            g.push(s);
        }
    }

    fn with<T>(&self, f: impl FnOnce(&mut Session) -> Result<T>) -> Result<T> {
        let mut s = self.checkout()?;
        let r = f(&mut s);
        self.checkin(s);
        r
    }

    fn feats(&self) -> Feats {
        *self.inner.feats.lock().unwrap()
    }

    pub fn connect(&self) -> Result<ConsoleInfo> {
        self.with(|_| Ok(()))?;

        self.list("/")?;
        let banner = self.inner.banner.lock().unwrap().clone();
        let ep = &self.inner.cfg.ep;
        Ok(ConsoleInfo {
            host: ep.host.clone(),
            port: ep.port,
            name: "FTP".to_string(),
            version: banner
                .lines()
                .find_map(|l| l.trim().strip_prefix("Version:"))
                .or_else(|| banner.lines().next())
                .unwrap_or("")
                .trim()
                .chars()
                .take(80)
                .collect(),
            privileged: true,
            uptime_seconds: 0,
            instance_id: String::new(),
            system: SystemInfo::default(),
            volumes: Vec::new(),
            protocol: "ftp".to_string(),
            caps: self.caps(),
        })
    }

    pub fn storage(&self) -> Result<Vec<StorageVolume>> {
        Ok(Vec::new())
    }

    pub fn list(&self, path: &str) -> Result<Vec<Entry>> {
        let p = rp::norm(path);
        let f = self.feats();

        let mlsd = f.mlsd && f.unix_mode;
        let bytes = self.with(|s| {
            if mlsd {
                s.expect_ok(&format!("CWD {p}"))?;
                s.listing(self, "MLSD")
            } else {
                s.listing(self, &format!("LIST {p}"))
            }
        })?;
        let mut out = Vec::new();
        for (mut e, link) in parse_listing(&bytes, mlsd) {
            if link {
                let target = rp::join(&p, &e.name);
                if self.is_dir(&target).unwrap_or(false) {
                    e.is_dir = true;
                } else if let Ok(Some(sz)) = self.size_of(&target) {
                    e.size = sz;
                }
            }
            out.push(e);
        }
        Ok(out)
    }

    pub fn is_dir(&self, path: &str) -> Result<bool> {
        let p = rp::norm(path);
        self.with(|s| {
            let r = s.raw(&format!("CWD {p}"))?;
            Ok(r.is_ok())
        })
    }

    fn size_of(&self, path: &str) -> Result<Option<u64>> {
        let p = rp::norm(path);
        self.with(|s| {
            let r = s.raw(&format!("SIZE {p}"))?;
            Ok(if r.code == 213 {
                r.first().trim().parse().ok()
            } else {
                None
            })
        })
    }

    pub fn stat(&self, path: &str) -> Result<Option<Entry>> {
        let p = rp::norm(path);
        if p == "/" {
            return Ok(Some(Entry {
                name: String::new(),
                is_dir: true,
                size: 0,
                mode: 0o777,
                modified: String::new(),
            }));
        }
        let f = self.feats();
        if f.mlst {
            let r = self.with(|s| s.raw(&format!("MLST {p}")))?;
            if r.code == 250 {
                return Ok(r.lines.iter().find_map(|l| parse_mlsd_line(l)).map(|(mut e, _)| {
                    e.name = rp::file_name(&p);
                    e
                }));
            }
            if r.code == 550 {
                return Ok(None);
            }
        }
        match self.list(&rp::parent(&p)) {
            Ok(list) => {
                let name = rp::file_name(&p);
                Ok(list.into_iter().find(|e| e.name == name))
            }
            Err(Error::Remote(e)) if matches!(e.kind, RemoteKind::NotFound | RemoteKind::Other) => Ok(None),
            Err(e) => Err(e),
        }
    }

    pub fn exists(&self, path: &str) -> Result<bool> {
        let p = rp::norm(path);
        if p == "/" {
            return Ok(true);
        }
        if self.size_of(&p)?.is_some() {
            return Ok(true);
        }
        self.is_dir(&p)
    }

    pub fn dir_size(&self, path: &str) -> Result<u64> {
        let mut total = 0u64;
        let mut stack = vec![rp::norm(path)];
        while let Some(d) = stack.pop() {
            for e in self.list(&d)? {
                if e.is_dir {
                    stack.push(rp::join(&d, &e.name));
                } else {
                    total += e.size;
                }
            }
        }
        Ok(total)
    }

    pub fn preflight(&self, _path: &str, _size: u64, _is_dir: bool) -> Result<Preflight> {
        Ok(Preflight {
            ok: true,
            available: u64::MAX,
            error: String::new(),
        })
    }

    pub fn conflicts(&self, paths: &[String]) -> Result<Vec<String>> {
        let mut parents: std::collections::HashMap<String, Option<std::collections::HashSet<String>>> =
            std::collections::HashMap::new();
        let mut out = Vec::new();
        for orig in paths {
            let p = rp::norm(orig);
            if p == "/" {
                out.push(orig.clone());
                continue;
            }
            let parent = rp::parent(&p);
            if !parents.contains_key(&parent) {
                let names = match self.list(&parent) {
                    Ok(l) => Some(l.into_iter().map(|e| e.name).collect()),
                    Err(Error::Remote(e)) if matches!(e.kind, RemoteKind::NotFound | RemoteKind::Other) => None,
                    Err(e) => return Err(e),
                };
                parents.insert(parent.clone(), names);
            }
            if parents[&parent]
                .as_ref()
                .map(|s| s.contains(&rp::file_name(&p)))
                .unwrap_or(false)
            {
                out.push(orig.clone());
            }
        }
        Ok(out)
    }

    pub fn unique_names(&self, paths: &[String]) -> Result<Vec<String>> {
        let mut taken: std::collections::HashSet<String> = std::collections::HashSet::new();
        let mut out = Vec::with_capacity(paths.len());
        for p in paths {
            let p = rp::norm(p);
            let mut cand = p.clone();
            if taken.contains(&cand) || self.exists(&cand)? {
                let (parent, name) = (rp::parent(&p), rp::file_name(&p));
                let (stem, ext) = rp::split_ext(&name);
                let (stem, ext) = (stem.to_string(), ext.to_string());
                let mut n = 1;
                loop {
                    cand = rp::join(&parent, &format!("{stem} ({n}){ext}"));
                    if !taken.contains(&cand) && !self.exists(&cand)? {
                        break;
                    }
                    n += 1;
                    if n > 10_000 {
                        return Err(unsupported("nomes demais iguais"));
                    }
                }
            }
            taken.insert(cand.clone());
            out.push(cand);
        }
        Ok(out)
    }

    pub fn mkdir(&self, path: &str) -> Result<()> {
        let p = rp::norm(path);
        self.with(|s| {
            let r = s.raw(&format!("MKD {p}"))?;
            if r.is_ok() {
                return Ok(());
            }

            let c = s.raw(&format!("CWD {p}"))?;
            if c.is_ok() {
                Ok(())
            } else {
                Err(r.error())
            }
        })
    }

    pub fn mkdir_all(&self, path: &str, cancel: Option<&Cancel>) -> Result<()> {
        let comps = rp::components(path);
        let mut start = 0;
        for k in (1..=comps.len()).rev() {
            if self.is_dir(&format!("/{}", comps[..k].join("/")))? {
                start = k;
                break;
            }
        }
        for k in start + 1..=comps.len() {
            if let Some(c) = cancel {
                c.check()?;
            }
            self.mkdir(&format!("/{}", comps[..k].join("/")))?;
        }
        Ok(())
    }

    pub fn rename(&self, from: &str, to: &str) -> Result<()> {
        let (f, t) = (rp::norm(from), rp::norm(to));
        self.with(|s| {
            s.expect(&format!("RNFR {f}"), &[350])?;
            s.expect_ok(&format!("RNTO {t}"))?;
            Ok(())
        })
    }

    pub fn chmod(&self, path: &str, mode: u32) -> Result<()> {
        let p = rp::norm(path);
        self.with(|s| {
            let r = s.raw(&format!("SITE CHMOD {mode:o} {p}"))?;
            if r.is_ok() {
                Ok(())
            } else if matches!(r.code, 500 | 501 | 502 | 504) {
                Err(unsupported("este servidor FTP não permite alterar permissões"))
            } else {
                Err(r.error())
            }
        })
    }

    fn dele(&self, path: &str) -> Result<()> {
        let p = rp::norm(path);
        self.with(|s| s.expect_ok(&format!("DELE {p}")).map(|_| ()))
    }

    fn rmd(&self, path: &str) -> Result<()> {
        let p = rp::norm(path);
        self.with(|s| s.expect_ok(&format!("RMD {p}")).map(|_| ()))
    }

    fn rm_tree(&self, path: &str, cancel: &Cancel, on_item: &mut dyn FnMut(&str)) -> Result<()> {
        let p = rp::norm(path);
        let Some(top) = self.stat(&p)? else { return Ok(()) };
        if !top.is_dir {
            self.dele(&p)?;
            on_item(&p);
            return Ok(());
        }
        let mut stack: Vec<(String, bool)> = vec![(p, false)];
        while let Some((d, visited)) = stack.pop() {
            cancel.check()?;
            if visited {
                self.rmd(&d)?;
                on_item(&d);
                continue;
            }
            stack.push((d.clone(), true));
            for e in self.list(&d)? {
                cancel.check()?;
                let child = rp::join(&d, &e.name);
                if e.is_dir {
                    stack.push((child, false));
                } else {
                    self.dele(&child)?;
                    on_item(&child);
                }
            }
        }
        Ok(())
    }

    fn spawn_job(
        &self,
        kind: &str,
        source: &str,
        destination: &str,
        policy: Conflict,
        work: impl FnOnce(&JobCtx) -> Result<()> + Send + 'static,
    ) -> u64 {
        let id = self.inner.next_job.fetch_add(1, Ordering::Relaxed);
        let cancel = Cancel::new();
        {
            let mut g = self.inner.jobs.lock().unwrap();

            while g.len() >= 200 {
                match g.iter().position(|e| e.job.finished()) {
                    Some(i) => {
                        g.remove(i);
                    }
                    None => break,
                }
            }
            g.push(JobEntry {
                job: Job {
                    id,
                    kind: kind.to_string(),
                    state: "queued".into(),
                    source: source.to_string(),
                    destination: destination.to_string(),
                    total_items: 1,
                    conflict_policy: policy.as_str().to_string(),
                    ..Default::default()
                },
                cancel: cancel.clone(),
            });
        }
        let ctx = JobCtx {
            id,
            ftp: self.clone(),
            cancel,
        };
        let spawned = std::thread::Builder::new()
            .name("ps5gfc-ftp-job".into())
            .spawn(move || {
                ctx.update(|j| j.state = "running".into());
                let r = work(&ctx);
                ctx.update(|j| match &r {
                    Ok(()) => j.state = "done".into(),
                    Err(Error::Cancelled) => j.state = "canceled".into(),
                    Err(e) => {
                        j.state = "error".into();
                        j.error = e.localized();
                        j.error_code = match e {
                            Error::Remote(r) => serde_json::to_value(r.kind)
                                .ok()
                                .and_then(|v| v.as_str().map(str::to_string))
                                .unwrap_or_default(),
                            _ => String::new(),
                        };
                    }
                });
            });
        if spawned.is_err() {
            let mut g = self.inner.jobs.lock().unwrap();
            if let Some(e) = g.iter_mut().find(|e| e.job.id == id) {
                e.job.state = "error".into();
                e.job.error = "não foi possível iniciar a tarefa".into();
            }
        }
        id
    }

    pub fn delete(&self, path: &str) -> Result<u64> {
        let p = rp::norm(path);
        if p == "/" {
            return Err(Error::Remote(RemoteError::new(
                RemoteKind::Denied,
                0,
                "recusado: a raiz do sistema de arquivos",
            )));
        }
        let src = p.clone();
        Ok(self.spawn_job("delete", &p, "", Conflict::Cancel, move |h| {
            let mut n = 0u64;
            h.ftp.rm_tree(&src, &h.cancel, &mut |cur| {
                n += 1;
                let (cur, n) = (cur.to_string(), n);
                h.update(|j| {
                    j.current = cur;
                    j.completed = n;
                    j.current_index = n;
                });
            })
        }))
    }

    pub fn copy_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        let (s, d) = (rp::norm(source), rp::norm(destination));
        if s == d {
            return Err(Error::Remote(RemoteError::new(
                RemoteKind::Other,
                0,
                "origem e destino precisam ser diferentes",
            )));
        }
        let (s2, d2) = (s.clone(), d.clone());
        Ok(self.spawn_job("copy", &s, &d, policy, move |h| job_copy(h, &s2, &d2, policy, false)))
    }

    pub fn move_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        let (s, d) = (rp::norm(source), rp::norm(destination));
        if s == d {
            return Err(Error::Remote(RemoteError::new(
                RemoteKind::Other,
                0,
                "origem e destino precisam ser diferentes",
            )));
        }
        let (s2, d2) = (s.clone(), d.clone());
        Ok(self.spawn_job("move", &s, &d, policy, move |h| job_copy(h, &s2, &d2, policy, true)))
    }

    pub fn jobs(&self) -> Result<Vec<Job>> {
        Ok(self.inner.jobs.lock().unwrap().iter().map(|e| e.job.clone()).collect())
    }

    pub fn cancel_job(&self, id: u64) -> Result<()> {
        if let Some(e) = self.inner.jobs.lock().unwrap().iter().find(|e| e.job.id == id) {
            e.cancel.cancel();
        }
        Ok(())
    }

    pub fn clear_jobs(&self) -> Result<()> {
        self.inner.jobs.lock().unwrap().retain(|e| !e.job.finished());
        Ok(())
    }

    pub fn wait_job(&self, id: u64, cancel: &Cancel, mut on_tick: impl FnMut(&Job)) -> Result<Job> {
        loop {
            cancel.check()?;
            let job = self
                .inner
                .jobs
                .lock()
                .unwrap()
                .iter()
                .find(|e| e.job.id == id)
                .map(|e| e.job.clone());
            match job {
                Some(j) => {
                    on_tick(&j);
                    if j.finished() {
                        return Ok(j);
                    }
                }
                None => {
                    return Ok(Job {
                        id,
                        state: "done".into(),
                        ..Default::default()
                    })
                }
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    }

    pub fn read_text(&self, path: &str) -> Result<TextFile> {
        let bytes = self.read_all(path, TEXT_LIMIT)?;
        if bytes.contains(&0) {
            return Err(unsupported("não é um arquivo de texto"));
        }
        let version = crc_version(&bytes);
        let (bom, body) = match bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
            Some(b) => (true, b),
            None => (false, &bytes[..]),
        };
        let text = std::str::from_utf8(body).map_err(|_| unsupported("o arquivo não está em UTF-8"))?;
        let crlf = text.matches("\r\n").count();
        let lf = text.matches('\n').count() - crlf;
        let cr = text.matches('\r').count() - crlf;
        let newline = if crlf > lf && crlf >= cr {
            "crlf"
        } else if cr > lf && cr > crlf {
            "cr"
        } else {
            "lf"
        };
        let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
        Ok(TextFile {
            text: normalized,
            version,
            newline: newline.to_string(),
            bom,
        })
    }

    pub fn write_text(&self, path: &str, text: &str, version: &str, newline: &str, bom: bool) -> Result<String> {
        let p = rp::norm(path);
        let old = self.stat(&p)?;
        if let Some(e) = &old {
            let cur = self.read_all(&p, TEXT_LIMIT.max(e.size))?;
            if !version.is_empty() && crc_version(&cur) != version {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Other,
                    409,
                    "o arquivo mudou no console desde que foi aberto",
                )));
            }
        }
        let nl = match newline {
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => "\n",
        };
        let mut body: Vec<u8> = Vec::with_capacity(text.len() + 3);
        if bom {
            body.extend_from_slice(&[0xEF, 0xBB, 0xBF]);
        }
        body.extend_from_slice(
            text.replace("\r\n", "\n")
                .replace('\r', "\n")
                .replace('\n', nl)
                .as_bytes(),
        );
        let mut src: &[u8] = &body;
        self.put(&p, body.len() as u64, &mut src, true, None)?;
        if let Some(e) = old {
            if e.mode != 0 {
                let _ = self.chmod(&p, e.mode & 0o7777);
            }
        }
        Ok(crc_version(&body))
    }

    fn read_all(&self, path: &str, limit: u64) -> Result<Vec<u8>> {
        let (mut dl, size) = self.open_download(path)?;
        if size > limit {
            return Err(unsupported("arquivo grande demais para abrir como texto"));
        }
        let mut out = Vec::with_capacity(size as usize);
        dl.read_to_end(&mut out).map_err(map_io)?;
        Ok(out)
    }

    pub fn open_download(&self, path: &str) -> Result<(FtpReader, u64)> {
        let p = rp::norm(path);
        let mut s = self.checkout()?;
        let size = {
            let r = s.raw(&format!("SIZE {p}"))?;
            if r.code == 213 {
                match r.first().trim().parse::<u64>() {
                    Ok(n) => n,
                    Err(_) => {
                        self.checkin(s);
                        return Err(protocol("tamanho inválido"));
                    }
                }
            } else {
                let e = r.error();
                self.checkin(s);
                return Err(e);
            }
        };
        match s.start_xfer(self, &format!("RETR {p}"), None) {
            Ok(data) => Ok((
                FtpReader {
                    ftp: self.clone(),
                    sess: Some(s),
                    data: Some(data),
                },
                size,
            )),
            Err(e) => {
                self.checkin(s);
                Err(e)
            }
        }
    }

    fn tmp_path(&self, path: &str) -> String {
        let p = rp::norm(path);
        let id = format!(
            "{:x}-{:x}-{:x}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0),
            std::process::id(),
            self.inner.next_tmp.fetch_add(1, Ordering::Relaxed)
        );
        let name = rp::file_name(&p);

        if name.len() + TEMP_MARK.len() + id.len() > 250 {
            return p;
        }
        format!("{p}{TEMP_MARK}{id}")
    }

    fn discard_partial(&self, tmp: &str) {
        for attempt in 0..4u32 {
            match self.dele(tmp) {
                Ok(()) => return,
                Err(Error::Remote(e)) if e.kind == RemoteKind::NotFound => return,
                Err(_) => std::thread::sleep(Duration::from_millis(300 << attempt)),
            }
        }
    }

    fn commit(&self, s: &mut Session, tmp: &str, path: &str, overwrite: bool) -> Result<()> {
        if tmp == path {
            return Ok(());
        }
        s.expect(&format!("RNFR {tmp}"), &[350])?;
        let r = s.raw(&format!("RNTO {path}"))?;
        if r.is_ok() {
            return Ok(());
        }
        if overwrite {
            let del = s.raw(&format!("DELE {path}"))?;
            if del.is_ok() {
                s.expect(&format!("RNFR {tmp}"), &[350])?;
                s.expect_ok(&format!("RNTO {path}"))?;
                return Ok(());
            }
        }
        Err(r.error())
    }

    pub fn put(
        &self,
        path: &str,
        size: u64,
        src: &mut dyn Read,
        overwrite: bool,
        mut hook: Option<SentHook<'_>>,
    ) -> Result<()> {
        let path = rp::norm(path);
        if rp::file_name(&path).is_empty() {
            return Err(Error::invalid(crate::t!("err.unsafe_path", path = path)));
        }
        if !overwrite && self.exists(&path)? {
            return Err(Error::Remote(RemoteError::new(
                RemoteKind::Exists,
                553,
                crate::t!("err.exists", path = path),
            )));
        }
        let tmp = self.tmp_path(&path);
        let mut s = self.checkout()?;
        let res = (|| -> Result<()> {
            let mut data = s.start_xfer(self, &format!("STOR {tmp}"), None)?;
            let body: io::Result<()> = (|| {
                let mut buf = vec![0u8; SLICE.min(size.max(1) as usize)];
                let mut sent = 0u64;
                while sent < size {
                    let want = buf.len().min((size - sent) as usize);
                    let n = src.read(&mut buf[..want])?;
                    if n == 0 {
                        return Err(io::Error::new(
                            io::ErrorKind::UnexpectedEof,
                            "a origem terminou antes do tamanho anunciado",
                        ));
                    }
                    data.write_all(&buf[..n])?;
                    sent += n as u64;
                    if let Some(h) = hook.as_mut() {
                        h(sent)?;
                    }
                }
                data.flush()
            })();
            if let Err(e) = body {
                drop(data);
                return Err(s.explain(e));
            }
            let _ = data.shutdown(Shutdown::Write);
            drop(data);
            s.finish_xfer(Duration::from_secs(300))?;
            if tmp != path {
                let r = s.raw(&format!("SIZE {tmp}"))?;
                if r.code == 213 && r.first().trim().parse::<u64>().ok() != Some(size) {
                    return Err(unreachable_msg(format!(
                        "o console gravou {} em vez de {size} bytes",
                        r.first().trim()
                    )));
                }
            }
            self.commit(&mut s, &tmp, &path, overwrite)
        })();
        self.checkin(s);
        if res.is_err() {
            self.discard_partial(&tmp);
        }
        res
    }
}

struct TreeItem {
    rel: String,
    is_dir: bool,
    size: u64,
}

fn scan_tree(ftp: &Ftp, root: &str, cancel: &Cancel) -> Result<Vec<TreeItem>> {
    let top = ftp
        .stat(root)?
        .ok_or_else(|| Error::Remote(RemoteError::new(RemoteKind::NotFound, 550, root.to_string())))?;
    if !top.is_dir {
        return Ok(vec![TreeItem {
            rel: String::new(),
            is_dir: false,
            size: top.size,
        }]);
    }
    let mut items = vec![TreeItem {
        rel: String::new(),
        is_dir: true,
        size: 0,
    }];
    let mut stack = vec![String::new()];
    while let Some(rel) = stack.pop() {
        cancel.check()?;
        for e in ftp.list(&rp::join(root, &rel))? {
            let child = if rel.is_empty() {
                e.name.clone()
            } else {
                format!("{rel}/{}", e.name)
            };
            if e.is_dir {
                stack.push(child.clone());
            }
            items.push(TreeItem {
                rel: child,
                is_dir: e.is_dir,
                size: if e.is_dir { 0 } else { e.size },
            });
        }
    }
    Ok(items)
}

fn job_copy(h: &JobCtx, src: &str, dst_in: &str, policy: Conflict, mv: bool) -> Result<()> {
    let ftp = &h.ftp;
    if rp::is_within(dst_in, src) {
        return Err(Error::Remote(RemoteError::new(
            RemoteKind::Other,
            0,
            "não é possível copiar uma pasta para dentro dela mesma",
        )));
    }
    if ftp.stat(src)?.is_none() {
        return Err(Error::Remote(RemoteError::new(
            RemoteKind::NotFound,
            550,
            src.to_string(),
        )));
    }
    let mut dst = dst_in.to_string();
    let mut dst_exists = ftp.exists(&dst)?;
    if dst_exists {
        match policy {
            Conflict::Cancel => {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Exists,
                    553,
                    crate::t!("err.exists", path = dst),
                )))
            }
            Conflict::Skip => return Ok(()),
            Conflict::KeepBoth => {
                dst = ftp
                    .unique_names(&[dst])?
                    .into_iter()
                    .next()
                    .unwrap_or(dst_in.to_string());
                dst_exists = false;
            }
            Conflict::Replace => {}
        }
    }
    ftp.mkdir_all(&rp::parent(&dst), Some(&h.cancel))?;
    if mv && !dst_exists && ftp.rename(src, &dst).is_ok() {
        return Ok(());
    }

    h.update(|j| j.state = "scanning".into());
    let items = scan_tree(ftp, src, &h.cancel)?;
    let total: u64 = items.iter().map(|i| i.size).sum();
    let nfiles = items.iter().filter(|i| !i.is_dir).count() as u64;
    h.update(|j| {
        j.state = "running".into();
        j.total = total;
        j.total_items = nfiles.max(1);
    });
    for it in items.iter().filter(|i| i.is_dir) {
        h.cancel.check()?;
        ftp.mkdir_all(&rp::join(&dst, &it.rel), Some(&h.cancel))?;
    }
    let mut done = 0u64;
    for (k, it) in items.iter().filter(|i| !i.is_dir).enumerate() {
        h.cancel.check()?;
        let from = rp::join(src, &it.rel);
        let to = rp::join(&dst, &it.rel);
        h.update(|j| {
            j.current = from.clone();
            j.current_index = k as u64 + 1;
        });
        let (mut dl, size) = ftp.open_download(&from)?;
        let base = done;
        let mut hook = |sent: u64| -> io::Result<()> {
            if h.cancel.is_cancelled() {
                return Err(cancel_io());
            }
            h.update(|j| j.completed = base + sent);
            Ok(())
        };
        ftp.put(&to, size, &mut dl, true, Some(&mut hook))?;
        done += size;
        h.update(|j| j.completed = done);
    }
    if mv {
        h.cancel.check()?;
        ftp.rm_tree(src, &h.cancel, &mut |_| {})?;
    }
    Ok(())
}

struct Conn {
    sess: Session,
    data: TcpStream,
}

pub struct FtpUpload {
    ftp: Ftp,
    path: String,
    tmp: String,
    total: u64,
    overwrite: bool,

    next: u64,

    pos: u64,

    reported: u64,
    conn: Option<Conn>,

    recent: Vec<u8>,
    recent_start: u64,

    created: bool,
    complete: bool,
}

impl FtpUpload {
    pub fn new(ftp: &Ftp, path: &str, total: u64, overwrite: bool) -> Self {
        let path = rp::norm(path);
        let tmp = ftp.tmp_path(&path);
        Self {
            ftp: ftp.clone(),
            path,
            tmp,
            total,
            overwrite,
            next: 0,
            pos: 0,
            reported: 0,
            conn: None,
            recent: Vec::new(),
            recent_start: 0,
            created: false,
            complete: false,
        }
    }

    fn remember(&mut self, data: &[u8]) {
        self.recent.extend_from_slice(data);
        self.next += data.len() as u64;
        if self.recent.len() > RECENT_KEEP {
            let cut = self.recent.len() - RECENT_KEEP;
            self.recent.drain(..cut);
            self.recent_start += cut as u64;
        }
    }

    fn remote_size(&self) -> Result<Option<u64>> {
        let mut last = self.ftp.size_of(&self.tmp)?;
        for _ in 0..8 {
            std::thread::sleep(Duration::from_millis(250));
            let now = self.ftp.size_of(&self.tmp)?;
            if now == last {
                break;
            }
            last = now;
        }
        Ok(last)
    }

    fn open_conn(&mut self, end: u64) -> Result<()> {
        let from = if self.created {
            self.remote_size()?.unwrap_or(0)
        } else {
            0
        };
        if from > end {
            return Err(protocol(format!(
                "o parcial no console tem {from} bytes, mais do que o esperado ({end})"
            )));
        }
        if from < self.recent_start {
            return Err(protocol(format!(
                "o console perdeu {} bytes já enviados; envie o arquivo de novo",
                self.recent_start - from
            )));
        }
        self.pos = from;
        if from == self.total {
            return Ok(());
        }
        let mut sess = self.ftp.checkout()?;
        let cmd = if from == 0 {
            format!("STOR {}", self.tmp)
        } else {
            format!("APPE {}", self.tmp)
        };
        match sess.start_xfer(&self.ftp, &cmd, None) {
            Ok(data) => {
                self.created = true;
                self.conn = Some(Conn { sess, data });
                Ok(())
            }
            Err(e) => {
                self.ftp.checkin(sess);
                Err(e)
            }
        }
    }

    fn push(&mut self, data: &[u8], end: u64, stop: &Stop, on_bytes: &dyn Fn(u64)) -> Result<()> {
        if self.conn.is_none() {
            self.open_conn(end)?;
        }
        while self.pos < end {
            stop.check()?;
            let slice: &[u8] = if self.pos < self.next {
                let off = (self.pos - self.recent_start) as usize;
                let upto = ((self.next - self.recent_start) as usize).min(off + SLICE);
                &self.recent[off..upto]
            } else {
                let off = (self.pos - self.next) as usize;
                &data[off..(off + SLICE).min(data.len())]
            };
            let Some(conn) = self.conn.as_mut() else { break };
            if let Err(e) = conn.data.write_all(slice) {
                let mut c = self.conn.take().unwrap();
                return Err(c.sess.explain(e));
            }
            self.pos += slice.len() as u64;
            if self.pos > self.reported {
                on_bytes(self.pos - self.reported);
                self.reported = self.pos;
            }
        }
        Ok(())
    }

    fn finalize(&mut self) -> Result<()> {
        let mut sess = match self.conn.take() {
            Some(Conn { mut sess, data }) => {
                let _ = data.shutdown(Shutdown::Write);
                drop(data);
                sess.finish_xfer(Duration::from_secs(300))?;
                sess
            }
            None => self.ftp.checkout()?,
        };
        let res = (|| -> Result<()> {
            let r = sess.raw(&format!("SIZE {}", self.tmp))?;
            if r.code == 213 && r.first().trim().parse::<u64>().ok() != Some(self.total) {
                return Err(unreachable_msg(format!(
                    "o console gravou {} em vez de {} bytes",
                    r.first().trim(),
                    self.total
                )));
            }
            if !self.overwrite && self.ftp.exists(&self.path)? {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Exists,
                    553,
                    crate::t!("err.exists", path = self.path.clone()),
                )));
            }
            self.ftp.commit(&mut sess, &self.tmp, &self.path, self.overwrite)
        })();
        self.ftp.checkin(sess);
        res
    }
}

impl Uploader for FtpUpload {
    fn send_chunk(&mut self, data: &[u8], stop: &Stop, on_bytes: &dyn Fn(u64)) -> Result<()> {
        let end = self.next + data.len() as u64;
        if data.is_empty() || end > self.total {
            return Err(Error::invalid(format!(
                "bloco fora do tamanho do arquivo ({end} > {})",
                self.total
            )));
        }
        let mut attempt = 0u32;
        let t0 = Instant::now();
        loop {
            stop.check()?;
            let res =
                self.push(data, end, stop, on_bytes)
                    .and_then(|()| if end == self.total { self.finalize() } else { Ok(()) });
            match res {
                Ok(()) => {
                    self.remember(data);
                    self.complete = end == self.total;
                    return Ok(());
                }
                Err(Error::Remote(e)) if retryable(&e) => {
                    self.conn = None;
                    let busy = e.kind == RemoteKind::Busy;
                    attempt += 1;
                    if (busy && t0.elapsed() > BUSY_PATIENCE) || (!busy && attempt > MAX_ATTEMPTS) {
                        return Err(Error::Remote(e));
                    }
                    nap(if busy { Duration::from_secs(2) } else { backoff(attempt) }, &|| {
                        stop.is_set()
                    })?;
                }
                Err(e) => return Err(e),
            }
        }
    }

    fn is_complete(&self) -> bool {
        self.complete
    }

    fn abort(&mut self) {
        if !self.created || self.complete {
            return;
        }
        self.complete = true;
        self.conn = None;
        self.ftp.discard_partial(&self.tmp);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn address_forms() {
        let c = FtpConfig::parse("192.168.0.5").unwrap();
        assert_eq!(
            (c.ep.host.as_str(), c.ep.port, c.user.as_str()),
            ("192.168.0.5", DEFAULT_FTP_PORT, "anonymous")
        );
        let c = FtpConfig::parse("ftp://10.0.0.2:1337/").unwrap();
        assert_eq!((c.ep.host.as_str(), c.ep.port), ("10.0.0.2", 1337));
        let c = FtpConfig::parse("ftp://root:s3nha@10.0.0.2:21").unwrap();
        assert_eq!((c.user.as_str(), c.pass.as_str(), c.ep.port), ("root", "s3nha", 21));
        assert!(FtpConfig::parse("ftp://").is_none());
        assert!(FtpConfig::parse("ftp://a\r\nb@host").is_none());
    }

    #[test]
    fn passive_replies() {
        assert_eq!(
            parse_pasv("Entering Passive Mode (192,168,1,50,31,64)."),
            Some(31 * 256 + 64)
        );
        assert_eq!(
            parse_pasv("Entering Passive Mode 192,168,1,50,4,1"),
            Some(4 * 256 + 1)
        );
        assert_eq!(parse_pasv("nada"), None);
        assert_eq!(parse_epsv("Entering Extended Passive Mode (|||6446|)"), Some(6446));
        assert_eq!(parse_epsv("Extended (!!!7000!)"), Some(7000));
        assert_eq!(parse_epsv("sem parênteses"), None);
    }

    #[test]
    fn unix_listing() {
        let now = (2026, 10, 5);
        let (e, link) =
            parse_unix_line("drwxr-xr-x   2 root  wheel   512 Oct  3 12:30 PPSA01234 meu jogo", now).unwrap();
        assert!(e.is_dir && !link);
        assert_eq!(e.name, "PPSA01234 meu jogo");
        assert_eq!(e.mode, 0o755);
        assert_eq!(e.modified, "20261003123000");

        let (e, _) = parse_unix_line("-rw-r--r--   1 root  wheel  1234567 Dec 25  2024 jogo.ffpkg", now).unwrap();
        assert!(!e.is_dir);
        assert_eq!(
            (e.size, e.mode, e.modified.as_str()),
            (1_234_567, 0o644, "20241225000000")
        );

        let (e, _) = parse_unix_line("-rw-r--r--   1 root  wheel  10 Dec 25 08:00 a.txt", now).unwrap();
        assert_eq!(e.modified, "20251225080000");

        let (e, _) = parse_unix_line("-rwxr-xr-x 1 root 99 Jan  2 03:04 run.sh", now).unwrap();
        assert_eq!((e.name.as_str(), e.size, e.mode), ("run.sh", 99, 0o755));

        let (e, link) = parse_unix_line("lrwxrwxrwx 1 root wheel 7 Jan 2 2024 atalho -> /data", now).unwrap();
        assert!(link);
        assert_eq!(e.name, "atalho");

        assert!(parse_unix_line("total 12", now).is_none());
        assert!(parse_unix_line("drwxr-xr-x 2 root wheel 512 Oct 3 12:30 .", now).is_none());
        assert!(parse_unix_line("drwxr-xr-x 2 root wheel 512 Oct 3 12:30 ..", now).is_none());
    }

    #[test]
    fn mlsd_listing() {
        let (e, _) =
            parse_mlsd_line("type=file;size=1048576;modify=20240102030405.123;UNIX.mode=0640; arquivo com espaço.bin")
                .unwrap();
        assert_eq!(
            (e.name.as_str(), e.size, e.mode, e.modified.as_str()),
            ("arquivo com espaço.bin", 1_048_576, 0o640, "20240102030405")
        );
        let (e, _) = parse_mlsd_line("type=dir;modify=20240102030405; PPSA01234").unwrap();
        assert!(e.is_dir);
        assert!(parse_mlsd_line("type=cdir;modify=20240102030405; .").is_none());
        assert!(parse_mlsd_line("type=pdir;modify=20240102030405; ..").is_none());
        let (e, link) = parse_mlsd_line("type=OS.unix=slink:/data;size=7; atalho").unwrap();
        assert!(link && !e.is_dir);
    }

    #[test]
    fn error_classes() {
        let k = |c: u16, m: &str| match ftp_error(c, m) {
            Error::Remote(r) => r.kind,
            _ => unreachable!(),
        };
        assert_eq!(k(550, "No such file or directory"), RemoteKind::NotFound);
        assert_eq!(k(550, "/data/x: File exists"), RemoteKind::Exists);
        assert_eq!(k(550, "Permission denied"), RemoteKind::Denied);
        assert_eq!(k(552, "Exceeded storage allocation"), RemoteKind::NoSpace);
        assert_eq!(k(550, "write failed: No space left on device"), RemoteKind::NoSpace);
        assert_eq!(k(530, "Login incorrect"), RemoteKind::Denied);
        assert_eq!(k(421, "Service not available"), RemoteKind::Unreachable);
        assert_eq!(k(550, "Directory not empty"), RemoteKind::Other);
    }
}
