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

use std::collections::{BTreeMap, HashMap};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use serde_json::{json, Value};

use super::client::Client;
use super::http::Endpoint;
use super::path as rp;
use super::prospero::Prospero;

#[derive(Clone)]
enum Node {
    Dir,
    File(Vec<u8>),
}

#[derive(Default)]
pub struct Faults {
    pub cut_next_chunk_after: Option<usize>,

    pub drop_reply_next_chunk: bool,

    pub busy_mutations: u32,

    pub cut_next_download_after: Option<usize>,

    pub chunks_seen: u32,
}

struct Reservation {
    token: String,
    in_use: bool,
}

struct State {
    fs: BTreeMap<String, Node>,
    uploads: HashMap<String, Reservation>,
    jobs: Vec<Value>,
    next_job: u64,
    faults: Faults,
    requests: Vec<String>,
    free: u64,
}

pub struct MockConsole {
    pub endpoint: Endpoint,
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

fn err(status: u16, msg: &str) -> (u16, Value) {
    (status, json!({ "error": msg }))
}

impl State {
    fn is_dir(&self, p: &str) -> bool {
        p == "/" || matches!(self.fs.get(p), Some(Node::Dir))
    }
    fn exists(&self, p: &str) -> bool {
        p == "/" || self.fs.contains_key(p)
    }
    fn list(&self, dir: &str) -> Option<Vec<Value>> {
        if !self.is_dir(dir) {
            return None;
        }
        let prefix = if dir == "/" { "/".to_string() } else { format!("{dir}/") };
        Some(
            self.fs
                .iter()
                .filter(|(k, _)| k.starts_with(&prefix) && !k[prefix.len()..].contains('/') && k.len() > prefix.len())
                .map(|(k, n)| {
                    let (is_dir, size) = match n {
                        Node::Dir => (true, 0),
                        Node::File(d) => (false, d.len()),
                    };
                    json!({ "name": &k[prefix.len()..], "is_dir": is_dir, "size": size, "mode": 511, "modified": "20260101000000" })
                })
                .collect(),
        )
    }
    fn mutation_blocked(&mut self) -> bool {
        if self.faults.busy_mutations > 0 {
            self.faults.busy_mutations -= 1;
            return true;
        }
        !self.uploads.is_empty()
    }
}

impl MockConsole {
    pub fn start() -> Self {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        l.set_nonblocking(true).unwrap();
        let port = l.local_addr().unwrap().port();
        let mut fs = BTreeMap::new();
        for d in ["/data", "/mnt", "/user"] {
            fs.insert(d.to_string(), Node::Dir);
        }
        let state = Arc::new(Mutex::new(State {
            fs,
            uploads: HashMap::new(),
            jobs: Vec::new(),
            next_job: 1,
            faults: Faults::default(),
            requests: Vec::new(),
            free: 500 << 30,
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let (st2, stop2) = (state.clone(), stop.clone());
        let handle = std::thread::spawn(move || {
            while !stop2.load(Ordering::Relaxed) {
                match l.accept() {
                    Ok((s, _)) => {
                        let _ = s.set_nonblocking(false);
                        let st = st2.clone();
                        std::thread::spawn(move || {
                            let _ = handle(s, st);
                        });
                    }
                    Err(_) => std::thread::sleep(Duration::from_millis(2)),
                }
            }
        });
        MockConsole {
            endpoint: Endpoint {
                host: "127.0.0.1".into(),
                port,
            },
            state,
            stop,
            handle: Some(handle),
        }
    }

    pub fn client(&self) -> Client {
        Client::Prospero(Prospero::new(self.endpoint.clone()).with_busy_wait(Duration::from_secs(3)))
    }

    pub fn put_dir(&self, p: &str) {
        let mut s = self.state.lock().unwrap();
        let mut cur = String::new();
        for c in rp::components(p) {
            cur = format!("{cur}/{c}");
            s.fs.entry(cur.clone()).or_insert(Node::Dir);
        }
    }

    pub fn put_file(&self, p: &str, data: &[u8]) {
        self.put_dir(&rp::parent(p));
        self.state
            .lock()
            .unwrap()
            .fs
            .insert(rp::norm(p), Node::File(data.to_vec()));
    }

    pub fn file(&self, p: &str) -> Option<Vec<u8>> {
        match self.state.lock().unwrap().fs.get(&rp::norm(p)) {
            Some(Node::File(d)) => Some(d.clone()),
            _ => None,
        }
    }

    pub fn is_dir(&self, p: &str) -> bool {
        self.state.lock().unwrap().is_dir(&rp::norm(p))
    }

    pub fn paths(&self) -> Vec<String> {
        self.state.lock().unwrap().fs.keys().cloned().collect()
    }

    pub fn reservations(&self) -> usize {
        self.state.lock().unwrap().uploads.len()
    }

    pub fn faults<R>(&self, f: impl FnOnce(&mut Faults) -> R) -> R {
        f(&mut self.state.lock().unwrap().faults)
    }

    pub fn requests(&self) -> Vec<String> {
        self.state.lock().unwrap().requests.clone()
    }

    pub fn set_free(&self, bytes: u64) {
        self.state.lock().unwrap().free = bytes;
    }
}

impl Drop for MockConsole {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

fn pct_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            if let Ok(v) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn parse_query(q: &str) -> HashMap<String, String> {
    q.split('&')
        .filter(|p| !p.is_empty())
        .map(|p| match p.split_once('=') {
            Some((k, v)) => (pct_decode(k), pct_decode(v)),
            None => (pct_decode(p), String::new()),
        })
        .collect()
}

fn reply(s: &mut TcpStream, status: u16, body: &[u8]) {
    let head = format!(
        "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: {}\r\n\r\n",
        body.len()
    );
    let _ = s.write_all(head.as_bytes());
    let _ = s.write_all(body);
}

fn reply_json(s: &mut TcpStream, r: (u16, Value)) {
    reply(s, r.0, r.1.to_string().as_bytes());
}

fn handle(mut s: TcpStream, st: Arc<Mutex<State>>) -> std::io::Result<()> {
    s.set_read_timeout(Some(Duration::from_secs(10)))?;

    let mut buf = Vec::new();
    let mut tmp = [0u8; 8192];
    let head_end = loop {
        if let Some(p) = buf.windows(4).position(|w| w == b"\r\n\r\n") {
            break p;
        }
        let n = s.read(&mut tmp)?;
        if n == 0 {
            return Ok(());
        }
        buf.extend_from_slice(&tmp[..n]);
    };
    let head = String::from_utf8_lossy(&buf[..head_end]).into_owned();
    let mut lines = head.lines();
    let first = lines.next().unwrap_or("");
    let mut parts = first.split_whitespace();
    let method = parts.next().unwrap_or("").to_string();
    let target = parts.next().unwrap_or("").to_string();
    let content_length: usize = lines
        .filter_map(|l| {
            l.to_ascii_lowercase()
                .strip_prefix("content-length:")
                .map(|v| v.trim().parse().unwrap_or(0))
        })
        .next()
        .unwrap_or(0);
    let (path, query) = target.split_once('?').unwrap_or((&target, ""));
    let q = parse_query(query);
    let mut body: Vec<u8> = buf[head_end + 4..].to_vec();
    st.lock().unwrap().requests.push(format!("{method} {path}"));

    let is_chunk = path == "/api/ftp/upload/chunk" && method == "POST";
    if is_chunk {
        return chunk(&mut s, &st, &q, body, content_length);
    }
    while body.len() < content_length {
        let n = s.read(&mut tmp)?;
        if n == 0 {
            break;
        }
        body.extend_from_slice(&tmp[..n]);
    }
    let jbody: Value = serde_json::from_slice(&body).unwrap_or(Value::Null);

    if path == "/api/ftp/download" {
        let p = rp::norm(q.get("path").map(String::as_str).unwrap_or("/"));
        let mut g = st.lock().unwrap();
        let data = match g.fs.get(&p) {
            Some(Node::File(d)) => d.clone(),
            _ => {
                drop(g);
                reply_json(&mut s, err(404, "file does not exist or is not a regular file"));
                return Ok(());
            }
        };
        let cut = g.faults.cut_next_download_after.take();
        drop(g);
        let head = format!("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nConnection: close\r\nContent-Length: {}\r\n\r\n", data.len());
        s.write_all(head.as_bytes())?;
        match cut {
            Some(n) => {
                s.write_all(&data[..n.min(data.len())])?;
            }
            None => s.write_all(&data)?,
        }
        return Ok(());
    }

    let r = route(&st, &method, path, &q, &jbody, &body);
    reply_json(&mut s, r);
    Ok(())
}

fn route(
    st: &Arc<Mutex<State>>,
    method: &str,
    path: &str,
    q: &HashMap<String, String>,
    j: &Value,
    body: &[u8],
) -> (u16, Value) {
    let mut g = st.lock().unwrap();
    let qp = |k: &str| rp::norm(q.get(k).map(String::as_str).unwrap_or("/"));
    let jp = |k: &str| rp::norm(j.get(k).and_then(Value::as_str).unwrap_or("/"));
    match (method, path) {
        ("GET", "/api/status") => (
            200,
            json!({ "name": "Prospero Manager", "version": "1.1", "uptime_seconds": 5, "instance_id": "mock", "privileged": true }),
        ),
        ("GET", "/api/system/info") => (
            200,
            json!({ "platform": "PlayStation 5", "firmware": "13.60", "model": "PS5 (mock)", "hostname": "", "ip_address": "127.0.0.1", "user_name": "tester" }),
        ),
        ("GET", "/api/ftp/storage") => (
            200,
            json!({ "volumes": [
                { "label": "Internal data", "kind": "internal", "index": -1, "path": "/data", "total": 1000u64 << 30, "free": g.free },
                { "label": "USB 0", "kind": "usb", "index": 0, "path": "/mnt/usb0", "total": 100u64 << 30, "free": 50u64 << 30 },
            ] }),
        ),
        ("GET", "/api/ftp/list") => match g.list(&qp("path")) {
            Some(v) => (200, json!({ "path": qp("path"), "entries": v })),
            None => err(500, "could not list directory: No such file or directory"),
        },
        ("GET", "/api/ftp/dirsize") => {
            let p = qp("path");
            let prefix = format!("{p}/");
            let total: usize =
                g.fs.iter()
                    .filter(|(k, _)| k.starts_with(&prefix))
                    .map(|(_, n)| if let Node::File(d) = n { d.len() } else { 0 })
                    .sum();
            (200, json!({ "ok": true, "size": total }))
        }
        ("GET", "/api/ftp/preflight") => {
            let size: u64 = q.get("size").and_then(|s| s.parse().ok()).unwrap_or(0);
            if size > g.free {
                (
                    507,
                    json!({ "ok": false, "available": g.free, "error": "not enough free space" }),
                )
            } else {
                (200, json!({ "ok": true, "available": g.free }))
            }
        }
        ("POST", "/api/ftp/conflicts") => {
            let paths: Vec<String> = j
                .get("paths")
                .and_then(Value::as_array)
                .map(|a| a.iter().filter_map(|v| v.as_str().map(rp::norm)).collect())
                .unwrap_or_default();
            let existing: Vec<&String> = paths.iter().filter(|p| g.exists(p)).collect();
            (200, json!({ "paths": existing }))
        }
        ("POST", "/api/ftp/unique-names") => {
            let paths: Vec<String> = j
                .get("paths")
                .and_then(Value::as_array)
                .map(|a| a.iter().filter_map(|v| v.as_str().map(rp::norm)).collect())
                .unwrap_or_default();
            let out: Vec<String> = paths
                .iter()
                .map(|p| {
                    if !g.exists(p) {
                        return p.clone();
                    }
                    let name = rp::file_name(p);
                    let (stem, ext) = rp::split_ext(&name);
                    (1..)
                        .map(|i| rp::join(&rp::parent(p), &format!("{stem} ({i}){ext}")))
                        .find(|c| !g.exists(c))
                        .unwrap()
                })
                .collect();
            (200, json!({ "paths": out }))
        }
        ("POST", "/api/ftp/mkdir") => {
            if g.mutation_blocked() {
                return err(409, "another file operation is in progress");
            }
            let p = jp("path");
            if g.is_dir(&p) {
                return (200, json!({ "ok": true }));
            }
            if g.exists(&p) || !g.is_dir(&rp::parent(&p)) {
                return err(500, "could not create directory: No such file or directory");
            }
            g.fs.insert(p, Node::Dir);
            (200, json!({ "ok": true }))
        }
        ("POST", "/api/ftp/rename") => {
            if g.mutation_blocked() {
                return err(409, "another file operation is in progress");
            }
            let (from, to) = (jp("from"), jp("to"));
            if !g.exists(&from) {
                return err(500, "could not rename path: No such file or directory");
            }
            let prefix = format!("{from}/");
            let moved: Vec<(String, Node)> =
                g.fs.iter()
                    .filter(|(k, _)| **k == from || k.starts_with(&prefix))
                    .map(|(k, v)| (k.clone(), v.clone()))
                    .collect();
            for (k, _) in &moved {
                g.fs.remove(k);
            }
            for (k, v) in moved {
                g.fs.insert(format!("{to}{}", &k[from.len()..]), v);
            }
            (200, json!({ "ok": true }))
        }
        ("POST", "/api/ftp/chmod") => {
            if g.mutation_blocked() {
                return err(409, "another file operation is in progress");
            }
            (200, json!({ "ok": true }))
        }
        ("POST", "/api/ftp/delete") => {
            let p = jp("path");
            if p == "/" || p == "/data" {
                return err(500, "refusing to delete a protected filesystem root");
            }
            if !g.exists(&p) {
                return err(409, "path does not exist");
            }
            if g.uploads.keys().any(|u| rp::is_within(u, &p) || rp::is_within(&p, u)) {
                return err(409, "that path overlaps an active upload");
            }
            let prefix = format!("{p}/");
            let keys: Vec<String> =
                g.fs.keys()
                    .filter(|k| **k == p || k.starts_with(&prefix))
                    .cloned()
                    .collect();
            for k in keys {
                g.fs.remove(&k);
            }
            let id = g.next_job;
            g.next_job += 1;
            g.jobs.push(json!({ "id": id, "type": "delete", "state": "done", "source": p, "destination": p, "completed": 1, "total": 1 }));
            (200, json!({ "ok": true, "job_id": id }))
        }
        ("POST", "/api/ftp/copy") | ("POST", "/api/ftp/move") => {
            let (src, dst) = (jp("source"), jp("destination"));
            if !g.exists(&src) {
                return err(409, "source does not exist");
            }
            let prefix = format!("{src}/");
            let items: Vec<(String, Node)> =
                g.fs.iter()
                    .filter(|(k, _)| **k == src || k.starts_with(&prefix))
                    .map(|(k, v)| (k.clone(), v.clone()))
                    .collect();
            for (k, v) in &items {
                g.fs.insert(format!("{dst}{}", &k[src.len()..]), v.clone());
            }
            if path.ends_with("move") {
                for (k, _) in &items {
                    g.fs.remove(k);
                }
            }
            let id = g.next_job;
            g.next_job += 1;
            g.jobs.push(json!({ "id": id, "type": &path[path.rfind('/').unwrap() + 1..], "state": "done", "source": src, "destination": dst, "completed": 1, "total": 1 }));
            (200, json!({ "ok": true, "job_id": id }))
        }
        ("GET", "/api/files/jobs") => (200, json!({ "jobs": g.jobs })),
        ("POST", "/api/files/jobs/cancel") | ("POST", "/api/files/jobs/clear") => {
            if path.ends_with("clear") {
                g.jobs.clear();
            }
            (200, json!({ "ok": true }))
        }
        ("POST", "/api/ftp/upload") => {
            let p = qp("path");
            let overwrite = q.get("overwrite").map(String::as_str) == Some("1");
            if g.uploads.keys().any(|u| rp::is_within(u, &p) || rp::is_within(&p, u)) {
                return err(409, "another upload is writing this path");
            }
            if !g.is_dir(&rp::parent(&p)) {
                return err(507, "could not create upload staging file");
            }
            if g.exists(&p) && !overwrite {
                return err(409, "destination already exists");
            }
            g.fs.insert(p, Node::File(body.to_vec()));
            (200, json!({ "ok": true }))
        }
        _ => err(404, "not found"),
    }
}

fn chunk(
    s: &mut TcpStream,
    st: &Arc<Mutex<State>>,
    q: &HashMap<String, String>,
    mut body: Vec<u8>,
    content_length: usize,
) -> std::io::Result<()> {
    let target = rp::norm(q.get("path").map(String::as_str).unwrap_or("/"));
    let id = q.get("upload_id").cloned().unwrap_or_default();
    let offset: u64 = q.get("offset").and_then(|v| v.parse().ok()).unwrap_or(0);
    let total: u64 = q.get("total").and_then(|v| v.parse().ok()).unwrap_or(0);
    let overwrite = q.get("overwrite").map(String::as_str) == Some("1");
    let partial = format!("{target}.pmgr-part-{id}");

    let mut write_error: Option<(u16, &str)> = None;
    let mut drain = false;
    {
        let mut g = st.lock().unwrap();
        g.faults.chunks_seen += 1;
        if offset > total || (content_length as u64) > total.saturating_sub(offset) {
            write_error = Some((400, "invalid resumable-upload checkpoint"));
        } else if g.faults.busy_mutations > 0 && g.uploads.is_empty() {
            g.faults.busy_mutations -= 1;
            write_error = Some((409, "another file operation is in progress"));
        } else if g
            .uploads
            .iter()
            .any(|(u, r)| *u != target && (rp::is_within(u, &target) || rp::is_within(&target, u)) && r.token != id)
        {
            write_error = Some((409, "another upload is writing this path"));
        } else if !g.is_dir(&rp::parent(&target)) {
            write_error = Some((507, "could not open resumable upload checkpoint"));
        } else if g
            .uploads
            .get(&target)
            .map(|r| r.token == id && r.in_use)
            .unwrap_or(false)
        {
            write_error = Some((409, "this upload checkpoint is already in use"));
        } else {
            g.uploads.insert(
                target.clone(),
                Reservation {
                    token: id.clone(),
                    in_use: true,
                },
            );
            let psize = match g.fs.get(&partial) {
                Some(Node::File(d)) => Some(d.len() as u64),
                _ => None,
            };
            let published = offset > 0
                && psize.is_none()
                && matches!(g.fs.get(&target), Some(Node::File(d)) if d.len() as u64 == total)
                && offset + content_length as u64 == total;
            if published || psize == Some(offset + content_length as u64) {
                drain = true;
            } else if (psize.is_some() && psize != Some(offset)) || (psize.is_none() && offset != 0) {
                write_error = Some((507, "upload offset does not match the saved checkpoint"));
            } else {
                if offset == 0 {
                    g.fs.insert(partial.clone(), Node::File(Vec::new()));
                } else if psize.is_none() {
                    g.fs.insert(partial.clone(), Node::File(Vec::new()));
                }
            }
        }
    }

    let cut = if write_error.is_none() && !drain {
        st.lock().unwrap().faults.cut_next_chunk_after.take()
    } else {
        None
    };
    let mut tmp = vec![0u8; 64 * 1024];
    let mut received = 0usize;
    let mut chunk_bytes: Vec<u8> = Vec::new();
    let absorb = |data: &[u8], chunk_bytes: &mut Vec<u8>| {
        chunk_bytes.extend_from_slice(data);
    };
    absorb(&std::mem::take(&mut body), &mut chunk_bytes);
    received += chunk_bytes.len();
    let mut dropped = false;
    while received < content_length {
        if let Some(c) = cut {
            if received >= c {
                dropped = true;
                break;
            }
        }
        let n = s.read(&mut tmp).unwrap_or(0);
        if n == 0 {
            dropped = true;
            break;
        }
        absorb(&tmp[..n], &mut chunk_bytes);
        received += n;
    }
    if let Some(c) = cut {
        if received >= c {
            dropped = true;
            chunk_bytes.truncate(c.max(0));
        }
    }

    let mut g = st.lock().unwrap();
    g.requests.push(format!("  chunk offset={offset} len={content_length} got={received} drain={drain} err={write_error:?} dropped={dropped} cut={cut:?}"));
    let release_idle = |g: &mut State| {
        if let Some(r) = g.uploads.get_mut(&target) {
            r.in_use = false;
        }
    };
    if let Some((status, msg)) = write_error {
        if status != 409 || !msg.contains("in use") {
            release_idle(&mut g);
        }
        drop(g);
        reply_json(s, err(status, msg));
        return Ok(());
    }
    if !drain {
        if let Some(Node::File(d)) = g.fs.get_mut(&partial) {
            d.truncate(offset as usize);
            d.extend_from_slice(&chunk_bytes);
        }
    }
    if dropped {
        release_idle(&mut g);
        return Ok(());
    }
    let next = offset + content_length as u64;
    let complete = next == total;
    if complete {
        let exists = g.exists(&target);
        if exists && !overwrite {
            g.fs.remove(&partial);
            g.uploads.remove(&target);
            drop(g);
            reply_json(s, err(409, "destination already exists"));
            return Ok(());
        }
        if let Some(node) = g.fs.remove(&partial) {
            g.fs.insert(target.clone(), node);
        }
        g.uploads.remove(&target);
    } else {
        release_idle(&mut g);
    }
    let drop_reply = std::mem::take(&mut g.faults.drop_reply_next_chunk);
    drop(g);
    if drop_reply {
        return Ok(());
    }
    reply_json(
        s,
        (200, json!({ "ok": true, "complete": complete, "next_offset": next })),
    );
    Ok(())
}
