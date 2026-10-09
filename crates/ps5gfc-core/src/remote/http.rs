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
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::Duration;

const SLICE: usize = 256 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Endpoint {
    pub host: String,
    pub port: u16,
}

impl Endpoint {
    pub fn parse(input: &str, default_port: u16) -> Option<Endpoint> {
        let mut s = input.trim();
        for p in ["http://", "HTTP://", "https://"] {
            if let Some(rest) = s.strip_prefix(p) {
                s = rest;
            }
        }
        let s = s.split(['/', '?', '#']).next().unwrap_or("").trim();
        if s.is_empty() {
            return None;
        }
        let (host, port) = if let Some(rest) = s.strip_prefix('[') {
            let (h, tail) = rest.split_once(']')?;
            let port = match tail.strip_prefix(':') {
                Some(p) => p.parse::<u16>().ok()?,
                None if tail.is_empty() => default_port,
                None => return None,
            };
            (h.to_string(), port)
        } else if let Some((h, p)) = s.rsplit_once(':') {
            if h.contains(':') {
                return None;
            }
            (h.to_string(), p.parse::<u16>().ok()?)
        } else {
            (s.to_string(), default_port)
        };
        let ok = !host.is_empty()
            && host.len() <= 253
            && host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | ':'));
        if !ok || port == 0 {
            return None;
        }
        Some(Endpoint { host, port })
    }

    pub fn authority(&self) -> String {
        if self.host.contains(':') {
            format!("[{}]:{}", self.host, self.port)
        } else {
            format!("{}:{}", self.host, self.port)
        }
    }

    fn resolve(&self) -> io::Result<Vec<SocketAddr>> {
        let mut addrs: Vec<SocketAddr> = (self.host.as_str(), self.port).to_socket_addrs()?.collect();

        addrs.sort_by_key(|a| a.is_ipv6());
        if addrs.is_empty() {
            return Err(io::Error::new(io::ErrorKind::NotFound, "endereço não resolvido"));
        }
        Ok(addrs)
    }
}

#[derive(Debug, Clone, Copy)]
pub struct Timeouts {
    pub connect: Duration,

    pub write: Duration,
}

impl Default for Timeouts {
    fn default() -> Self {
        Self {
            connect: Duration::from_secs(5),
            write: Duration::from_secs(30),
        }
    }
}

pub enum Body<'a> {
    Empty,
    Bytes(&'a [u8]),

    Stream { len: u64, src: &'a mut dyn Read },
}

pub struct Request<'a> {
    pub method: &'a str,

    pub target: &'a str,
    pub content_type: Option<&'a str>,
    pub body: Body<'a>,

    pub response_timeout: Duration,
}

pub type SentHook<'a> = &'a mut dyn FnMut(u64) -> io::Result<()>;

enum Mode {
    Length(u64),
    Chunked { left: u64, done: bool },
    UntilClose,
}

pub struct Response {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    rd: BufReader<TcpStream>,
    mode: Mode,
}

impl Response {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    pub fn content_length(&self) -> Option<u64> {
        match self.mode {
            Mode::Length(n) => Some(n),
            _ => None,
        }
    }

    pub fn into_bytes(mut self, limit: usize) -> io::Result<Vec<u8>> {
        let mut out = Vec::new();
        let mut buf = [0u8; 16 * 1024];
        loop {
            let n = self.read(&mut buf)?;
            if n == 0 {
                return Ok(out);
            }
            if out.len() + n > limit {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "resposta grande demais"));
            }
            out.extend_from_slice(&buf[..n]);
        }
    }

    fn read_line(&mut self) -> io::Result<String> {
        let mut line = String::new();
        let n = self.rd.read_line(&mut line)?;
        if n == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "conexão encerrada"));
        }
        Ok(line.trim_end_matches(['\r', '\n']).to_string())
    }
}

impl Read for Response {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        match &mut self.mode {
            Mode::Length(left) => {
                if *left == 0 {
                    return Ok(0);
                }
                let want = buf.len().min(*left as usize);
                let n = self.rd.read(&mut buf[..want])?;
                if n == 0 {
                    return Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "o console encerrou a conexão no meio da resposta",
                    ));
                }
                *left -= n as u64;
                Ok(n)
            }
            Mode::UntilClose => self.rd.read(buf),
            Mode::Chunked { .. } => loop {
                let (left, done) = match &self.mode {
                    Mode::Chunked { left, done } => (*left, *done),
                    _ => unreachable!(),
                };
                if done {
                    return Ok(0);
                }
                if left == 0 {
                    let line = self.read_line()?;
                    let hex = line.split(';').next().unwrap_or("").trim();
                    let size = u64::from_str_radix(hex, 16)
                        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "bloco chunked inválido"))?;
                    if size == 0 {
                        loop {
                            if self.read_line()?.is_empty() {
                                break;
                            }
                        }
                        self.mode = Mode::Chunked { left: 0, done: true };
                        return Ok(0);
                    }
                    self.mode = Mode::Chunked {
                        left: size,
                        done: false,
                    };
                    continue;
                }
                let want = buf.len().min(left as usize);
                let n = self.rd.read(&mut buf[..want])?;
                if n == 0 {
                    return Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "o console encerrou a conexão no meio da resposta",
                    ));
                }
                let left = left - n as u64;
                if left == 0 {
                    let _ = self.read_line()?;
                }
                self.mode = Mode::Chunked { left, done: false };
                return Ok(n);
            },
        }
    }
}

pub(crate) fn connect(ep: &Endpoint, tm: &Timeouts) -> io::Result<TcpStream> {
    let mut last: Option<io::Error> = None;
    for addr in ep.resolve()? {
        match TcpStream::connect_timeout(&addr, tm.connect) {
            Ok(s) => {
                let _ = s.set_nodelay(true);
                let _ = s.set_write_timeout(Some(tm.write));
                return Ok(s);
            }
            Err(e) => last = Some(e),
        }
    }
    Err(last.unwrap_or_else(|| io::Error::new(io::ErrorKind::NotFound, "sem endereço")))
}

fn read_response(stream: TcpStream, timeout: Duration, head_only: bool) -> io::Result<Response> {
    stream.set_read_timeout(Some(timeout))?;
    let mut rd = BufReader::with_capacity(256 * 1024, stream);
    let mut status_line = String::new();
    if rd.read_line(&mut status_line)? == 0 {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "o console fechou a conexão sem responder",
        ));
    }
    let mut parts = status_line.split_whitespace();
    let version = parts.next().unwrap_or("");
    let status: u16 = parts
        .next()
        .and_then(|s| s.parse().ok())
        .filter(|_| version.starts_with("HTTP/"))
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "resposta que não é HTTP"))?;
    let mut headers = Vec::new();
    loop {
        let mut line = String::new();
        if rd.read_line(&mut line)? == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "cabeçalho incompleto"));
        }
        let line = line.trim_end_matches(['\r', '\n']);
        if line.is_empty() {
            break;
        }
        if let Some((k, v)) = line.split_once(':') {
            headers.push((k.trim().to_string(), v.trim().to_string()));
        }
        if headers.len() > 100 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "cabeçalhos demais"));
        }
    }
    let find = |n: &str| {
        headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(n))
            .map(|(_, v)| v.as_str())
    };
    let mode = if head_only || status == 204 || status == 304 || (100..200).contains(&status) {
        Mode::Length(0)
    } else if find("transfer-encoding")
        .map(|v| v.to_ascii_lowercase().contains("chunked"))
        .unwrap_or(false)
    {
        Mode::Chunked { left: 0, done: false }
    } else if let Some(n) = find("content-length").and_then(|v| v.parse::<u64>().ok()) {
        Mode::Length(n)
    } else {
        Mode::UntilClose
    };
    Ok(Response {
        status,
        headers,
        rd,
        mode,
    })
}

impl Endpoint {
    pub fn send(&self, tm: &Timeouts, req: Request<'_>, mut on_sent: Option<SentHook<'_>>) -> io::Result<Response> {
        let mut stream = connect(self, tm)?;
        let len = match &req.body {
            Body::Empty => 0,
            Body::Bytes(b) => b.len() as u64,
            Body::Stream { len, .. } => *len,
        };
        let has_body = !matches!(req.body, Body::Empty) || req.method == "POST" || req.method == "PUT";
        let mut head = String::with_capacity(256);
        head.push_str(&format!(
            "{} {} HTTP/1.1\r\nHost: {}\r\n",
            req.method,
            req.target,
            self.authority()
        ));
        head.push_str("User-Agent: PS5GFC\r\nAccept: application/json, */*\r\nConnection: close\r\n");
        if let Some(ct) = req.content_type {
            head.push_str(&format!("Content-Type: {ct}\r\n"));
        }
        if has_body {
            head.push_str(&format!("Content-Length: {len}\r\n"));
        }
        head.push_str("\r\n");

        let mut hook_err: Option<io::Error> = None;
        let sent: io::Result<()> = (|| {
            stream.write_all(head.as_bytes())?;
            let mut total = 0u64;
            let mut notify = |total: u64, hook_err: &mut Option<io::Error>| -> io::Result<()> {
                if let Some(h) = on_sent.as_mut() {
                    if let Err(e) = h(total) {
                        *hook_err = Some(io::Error::new(e.kind(), e.to_string()));
                        return Err(e);
                    }
                }
                Ok(())
            };
            match req.body {
                Body::Empty => {}
                Body::Bytes(b) => {
                    for part in b.chunks(SLICE) {
                        stream.write_all(part)?;
                        total += part.len() as u64;
                        notify(total, &mut hook_err)?;
                    }
                }
                Body::Stream { len, src } => {
                    let mut buf = vec![0u8; SLICE.min(len.max(1) as usize)];
                    while total < len {
                        let want = buf.len().min((len - total) as usize);
                        let n = src.read(&mut buf[..want])?;
                        if n == 0 {
                            return Err(io::Error::new(
                                io::ErrorKind::UnexpectedEof,
                                "a origem terminou antes do tamanho anunciado",
                            ));
                        }
                        stream.write_all(&buf[..n])?;
                        total += n as u64;
                        notify(total, &mut hook_err)?;
                    }
                }
            }
            stream.flush()
        })();

        let head_only = req.method == "HEAD";
        match sent {
            Ok(()) => read_response(stream, req.response_timeout, head_only),
            Err(e) => {
                if let Some(h) = hook_err {
                    return Err(h);
                }

                match read_response(stream, Duration::from_secs(3), head_only) {
                    Ok(resp) if resp.status >= 400 => Ok(resp),
                    _ => Err(e),
                }
            }
        }
    }
}

pub fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn endpoint_parsing() {
        let e = Endpoint::parse("192.168.1.50", 7070).unwrap();
        assert_eq!((e.host.as_str(), e.port), ("192.168.1.50", 7070));
        let e = Endpoint::parse("http://192.168.1.50:7071/", 7070).unwrap();
        assert_eq!((e.host.as_str(), e.port), ("192.168.1.50", 7071));
        let e = Endpoint::parse("[::1]:80", 7070).unwrap();
        assert_eq!(e.authority(), "[::1]:80");
        assert!(Endpoint::parse("", 7070).is_none());
        assert!(Endpoint::parse("a b", 7070).is_none());
        assert!(Endpoint::parse("host\r\nX: y", 7070).is_none());
        assert!(Endpoint::parse("host:99999", 7070).is_none());
        assert!(Endpoint::parse("host:0", 7070).is_none());
    }

    #[test]
    fn url_encoding() {
        assert_eq!(urlencode("/data/a b/ç+#.exfat"), "%2Fdata%2Fa%20b%2F%C3%A7%2B%23.exfat");
        assert_eq!(urlencode("Az09-_.~"), "Az09-_.~");
    }

    fn serve_once(reply: Vec<u8>) -> (Endpoint, std::thread::JoinHandle<Vec<u8>>) {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = l.local_addr().unwrap().port();
        let h = std::thread::spawn(move || {
            let (mut s, _) = l.accept().unwrap();
            let mut got = Vec::new();
            let mut buf = [0u8; 4096];

            loop {
                let n = s.read(&mut buf).unwrap();
                got.extend_from_slice(&buf[..n]);
                if let Some(p) = got.windows(4).position(|w| w == b"\r\n\r\n") {
                    let head = String::from_utf8_lossy(&got[..p]).to_ascii_lowercase();
                    let cl = head
                        .lines()
                        .find_map(|l| l.strip_prefix("content-length:"))
                        .and_then(|v| v.trim().parse::<usize>().ok())
                        .unwrap_or(0);
                    if got.len() >= p + 4 + cl {
                        break;
                    }
                }
                if n == 0 {
                    break;
                }
            }
            s.write_all(&reply).unwrap();
            got
        });
        (
            Endpoint {
                host: "127.0.0.1".into(),
                port,
            },
            h,
        )
    }

    fn get(ep: &Endpoint) -> Response {
        ep.send(
            &Timeouts::default(),
            Request {
                method: "GET",
                target: "/x?y=1",
                content_type: None,
                body: Body::Empty,
                response_timeout: Duration::from_secs(5),
            },
            None,
        )
        .unwrap()
    }

    #[test]
    fn reads_content_length_body() {
        let (ep, h) = serve_once(b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello".to_vec());
        let r = get(&ep);
        assert_eq!(r.status, 200);
        assert_eq!(r.content_length(), Some(5));
        assert_eq!(r.into_bytes(100).unwrap(), b"hello");
        let sent = String::from_utf8(h.join().unwrap()).unwrap();
        assert!(sent.starts_with("GET /x?y=1 HTTP/1.1\r\nHost: 127.0.0.1:"));
        assert!(sent.contains("Connection: close"));
    }

    #[test]
    fn reads_chunked_body() {
        let (ep, _h) = serve_once(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6;ext=1\r\n world\r\n0\r\nX-T: 1\r\n\r\n".to_vec());
        assert_eq!(get(&ep).into_bytes(100).unwrap(), b"hello world");
    }

    #[test]
    fn reads_until_close_and_detects_truncation() {
        let (ep, _h) = serve_once(b"HTTP/1.0 200 OK\r\n\r\nabc".to_vec());
        assert_eq!(get(&ep).into_bytes(100).unwrap(), b"abc");
        let (ep, _h) = serve_once(b"HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nabc".to_vec());
        let e = get(&ep).into_bytes(100).unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::UnexpectedEof);
    }

    #[test]
    fn rejects_non_http() {
        let (ep, _h) = serve_once(b"SSH-2.0-whatever\r\n".to_vec());
        let e = ep
            .send(
                &Timeouts::default(),
                Request {
                    method: "GET",
                    target: "/",
                    content_type: None,
                    body: Body::Empty,
                    response_timeout: Duration::from_secs(5),
                },
                None,
            )
            .err()
            .unwrap();
        assert_eq!(e.kind(), io::ErrorKind::InvalidData);
    }

    #[test]
    fn posts_body_with_progress_and_cancel() {
        let (ep, h) = serve_once(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok".to_vec());
        let data = vec![7u8; 700_000];
        let mut calls = 0u64;
        let mut hook = |n: u64| -> io::Result<()> {
            calls = n;
            Ok(())
        };
        let r = ep
            .send(
                &Timeouts::default(),
                Request {
                    method: "POST",
                    target: "/up",
                    content_type: Some("application/octet-stream"),
                    body: Body::Bytes(&data),
                    response_timeout: Duration::from_secs(5),
                },
                Some(&mut hook),
            )
            .unwrap();
        assert_eq!(r.status, 200);
        assert_eq!(calls, 700_000);
        let got = h.join().unwrap();
        assert!(String::from_utf8_lossy(&got[..200]).contains("Content-Length: 700000"));

        let (ep, _h) = serve_once(b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n".to_vec());
        let mut stop = |_n: u64| -> io::Result<()> { Err(io::Error::new(io::ErrorKind::Interrupted, "cancelado")) };
        let e = ep
            .send(
                &Timeouts::default(),
                Request {
                    method: "POST",
                    target: "/up",
                    content_type: None,
                    body: Body::Bytes(&data),
                    response_timeout: Duration::from_secs(5),
                },
                Some(&mut stop),
            )
            .err()
            .unwrap();
        assert_eq!(e.kind(), io::ErrorKind::Interrupted);
    }
}
