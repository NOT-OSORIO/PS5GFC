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
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use super::http::{urlencode, Body, Endpoint, Request, Response, SentHook, Timeouts};
use super::path as rp;
use super::{RemoteError, RemoteKind, CANCEL_MARK, DEFAULT_PORT};
use crate::ctl::Cancel;
use crate::{Error, Result};

const JSON_LIMIT: usize = 64 * 1024 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Entry {
    pub name: String,
    pub is_dir: bool,
    #[serde(default)]
    pub size: u64,

    #[serde(default)]
    pub mode: u32,

    #[serde(default)]
    pub modified: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct StorageVolume {
    pub label: String,

    pub kind: String,
    #[serde(default)]
    pub index: i64,
    pub path: String,
    #[serde(default)]
    pub total: u64,
    #[serde(default)]
    pub free: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct SystemInfo {
    pub platform: String,
    pub firmware: String,
    pub model: String,
    pub hostname: String,
    pub ip_address: String,
    pub user_name: String,
}

#[derive(Debug, Clone, Copy, Serialize)]
pub struct Caps {
    pub zip: bool,
    pub chmod: bool,

    pub storage: bool,

    pub server_copy: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct ConsoleInfo {
    pub host: String,
    pub port: u16,
    pub name: String,
    pub version: String,
    pub privileged: bool,
    pub uptime_seconds: u64,
    pub instance_id: String,
    pub system: SystemInfo,
    pub volumes: Vec<StorageVolume>,

    pub protocol: String,
    pub caps: Caps,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
pub struct Job {
    pub id: u64,
    #[serde(rename = "type")]
    pub kind: String,

    pub state: String,
    pub source: String,
    pub destination: String,
    pub current: String,
    pub current_index: u64,
    pub total_items: u64,
    pub completed: u64,
    pub total: u64,
    pub error: String,
    pub error_code: String,
    pub conflict_policy: String,
}

impl Job {
    pub fn finished(&self) -> bool {
        matches!(self.state.as_str(), "done" | "attention" | "canceled" | "error")
    }
    pub fn failed(&self) -> bool {
        matches!(self.state.as_str(), "error" | "attention")
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Conflict {
    Cancel,
    Skip,
    Replace,
    KeepBoth,
}

impl Conflict {
    pub fn as_str(self) -> &'static str {
        match self {
            Conflict::Cancel => "cancel",
            Conflict::Skip => "skip",
            Conflict::Replace => "replace",
            Conflict::KeepBoth => "keep_both",
        }
    }
}

#[derive(Debug, Clone, Serialize)]
pub struct Preflight {
    pub ok: bool,
    pub available: u64,
    pub error: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct TextFile {
    pub text: String,
    pub version: String,

    pub newline: String,
    pub bom: bool,
}

#[derive(Debug, Clone, Copy)]
pub struct ChunkReq<'a> {
    pub path: &'a str,
    pub upload_id: &'a str,

    pub offset: u64,

    pub total: u64,
    pub overwrite: bool,
}

#[derive(Debug, Clone, Copy)]
pub struct ChunkAck {
    pub next_offset: u64,
    pub complete: bool,
}

#[derive(Clone)]
pub struct Prospero {
    ep: Endpoint,
    tm: Timeouts,
    busy_wait: Duration,
}

pub(crate) fn map_io(e: io::Error) -> Error {
    if e.kind() == io::ErrorKind::Interrupted && e.to_string() == CANCEL_MARK {
        return Error::Cancelled;
    }
    let kind = match e.kind() {
        io::ErrorKind::InvalidData => RemoteKind::Protocol,
        _ => RemoteKind::Unreachable,
    };
    Error::Remote(RemoteError::new(kind, 0, e.to_string()))
}

fn error_from(status: u16, body: &[u8]) -> Error {
    let v: Value = serde_json::from_slice(body).unwrap_or(Value::Null);
    let msg = v.get("error").and_then(Value::as_str).unwrap_or("");
    Error::Remote(RemoteError::from_response(status, msg))
}

fn protocol(msg: impl Into<String>) -> Error {
    Error::Remote(RemoteError::new(RemoteKind::Protocol, 0, msg))
}

impl Prospero {
    pub fn new(ep: Endpoint) -> Self {
        Self {
            ep,
            tm: Timeouts::default(),
            busy_wait: Duration::from_secs(15),
        }
    }

    pub fn connect_to(addr: &str) -> Result<Self> {
        let ep = Endpoint::parse(addr, DEFAULT_PORT)
            .ok_or_else(|| Error::invalid(crate::t!("err.rm.bad_addr", addr = addr)))?;
        Ok(Self::new(ep))
    }

    pub fn with_busy_wait(mut self, d: Duration) -> Self {
        self.busy_wait = d;
        self
    }

    pub fn endpoint(&self) -> &Endpoint {
        &self.ep
    }

    fn send(&self, req: Request<'_>, hook: Option<SentHook<'_>>) -> Result<Response> {
        self.ep.send(&self.tm, req, hook).map_err(map_io)
    }

    fn call(&self, method: &str, target: &str, body: Option<&Value>, timeout: Duration) -> Result<Value> {
        let payload = body.map(|v| serde_json::to_vec(v).unwrap_or_default());
        let resp = self.send(
            Request {
                method,
                target,
                content_type: payload.as_ref().map(|_| "application/json"),
                body: payload.as_deref().map(Body::Bytes).unwrap_or(Body::Empty),
                response_timeout: timeout,
            },
            None,
        )?;
        let status = resp.status;
        let bytes = resp.into_bytes(JSON_LIMIT).map_err(map_io)?;
        if status >= 400 {
            return Err(error_from(status, &bytes));
        }
        if bytes.is_empty() {
            return Ok(Value::Null);
        }
        serde_json::from_slice(&bytes).map_err(|_| protocol("resposta que não é JSON"))
    }

    fn get(&self, target: &str) -> Result<Value> {
        self.call("GET", target, None, Duration::from_secs(20))
    }

    fn post(&self, target: &str, body: &Value) -> Result<Value> {
        self.call("POST", target, Some(body), Duration::from_secs(30))
    }

    pub fn retry_busy<T>(&self, cancel: Option<&Cancel>, mut f: impl FnMut() -> Result<T>) -> Result<T> {
        let t0 = Instant::now();
        let mut delay = Duration::from_millis(200);
        loop {
            match f() {
                Err(Error::Remote(e)) if e.kind == RemoteKind::Busy && t0.elapsed() < self.busy_wait => {
                    if let Some(c) = cancel {
                        c.check()?;
                    }
                    std::thread::sleep(delay);
                    delay = (delay * 2).min(Duration::from_secs(2));
                }
                r => return r,
            }
        }
    }

    pub fn connect(&self) -> Result<ConsoleInfo> {
        let st = self.get("/api/status")?;
        let name = st.get("name").and_then(Value::as_str).unwrap_or("").to_string();
        if !name.to_ascii_lowercase().contains("prospero") {
            return Err(protocol(format!(
                "{} respondeu, mas não é um Prospero Manager",
                self.ep.authority()
            )));
        }

        let system = self
            .get("/api/system/info")
            .ok()
            .and_then(|v| serde_json::from_value::<SystemInfo>(v).ok())
            .unwrap_or_default();
        let volumes = self.storage().unwrap_or_default();
        Ok(ConsoleInfo {
            host: self.ep.host.clone(),
            port: self.ep.port,
            name,
            version: st.get("version").and_then(Value::as_str).unwrap_or("").to_string(),
            privileged: st.get("privileged").and_then(Value::as_bool).unwrap_or(false),
            uptime_seconds: st.get("uptime_seconds").and_then(Value::as_u64).unwrap_or(0),
            instance_id: st.get("instance_id").and_then(Value::as_str).unwrap_or("").to_string(),
            system,
            volumes,
            protocol: "prospero".to_string(),
            caps: self.caps(),
        })
    }

    pub fn caps(&self) -> Caps {
        Caps {
            zip: true,
            chmod: true,
            storage: true,
            server_copy: true,
        }
    }

    pub fn storage(&self) -> Result<Vec<StorageVolume>> {
        let v = self.get("/api/ftp/storage")?;
        serde_json::from_value(v.get("volumes").cloned().unwrap_or(Value::Null))
            .map_err(|_| protocol("lista de volumes inválida"))
    }

    pub fn volume_of<'a>(&self, volumes: &'a [StorageVolume], path: &str) -> Option<&'a StorageVolume> {
        volumes
            .iter()
            .filter(|v| rp::is_within(path, &v.path))
            .max_by_key(|v| v.path.len())
    }

    pub fn list(&self, path: &str) -> Result<Vec<Entry>> {
        let v = self.get(&format!("/api/ftp/list?path={}", urlencode(&rp::norm(path))))?;
        serde_json::from_value(v.get("entries").cloned().unwrap_or(Value::Null))
            .map_err(|_| protocol("listagem inválida"))
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
        match self.list(&rp::parent(&p)) {
            Ok(list) => {
                let name = rp::file_name(&p);
                Ok(list.into_iter().find(|e| e.name == name))
            }
            Err(Error::Remote(e)) if matches!(e.kind, RemoteKind::NotFound | RemoteKind::Other) && e.status >= 400 => {
                Ok(None)
            }
            Err(e) => Err(e),
        }
    }

    pub fn dir_size(&self, path: &str) -> Result<u64> {
        let v = self.call(
            "GET",
            &format!("/api/ftp/dirsize?path={}", urlencode(&rp::norm(path))),
            None,
            Duration::from_secs(120),
        )?;
        Ok(v.get("size").and_then(Value::as_u64).unwrap_or(0))
    }

    pub fn preflight(&self, path: &str, size: u64, is_dir: bool) -> Result<Preflight> {
        let target = format!(
            "/api/ftp/preflight?path={}&size={}{}",
            urlencode(&rp::norm(path)),
            size,
            if is_dir { "&is_dir=1" } else { "" }
        );
        let resp = self.send(
            Request {
                method: "GET",
                target: &target,
                content_type: None,
                body: Body::Empty,
                response_timeout: Duration::from_secs(20),
            },
            None,
        )?;
        let status = resp.status;
        let bytes = resp.into_bytes(JSON_LIMIT).map_err(map_io)?;
        let v: Value = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
        if status != 200 && status != 507 {
            return Err(error_from(status, &bytes));
        }
        Ok(Preflight {
            ok: v.get("ok").and_then(Value::as_bool).unwrap_or(false),
            available: v.get("available").and_then(Value::as_u64).unwrap_or(0),
            error: v.get("error").and_then(Value::as_str).unwrap_or("").to_string(),
        })
    }

    pub fn conflicts(&self, paths: &[String]) -> Result<Vec<String>> {
        if paths.is_empty() {
            return Ok(Vec::new());
        }
        let v = self.post("/api/ftp/conflicts", &json!({ "paths": paths }))?;
        Ok(v.get("paths")
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(|s| s.as_str().map(str::to_string)).collect())
            .unwrap_or_default())
    }

    pub fn unique_names(&self, paths: &[String]) -> Result<Vec<String>> {
        if paths.is_empty() {
            return Ok(Vec::new());
        }
        let v = self.post("/api/ftp/unique-names", &json!({ "paths": paths }))?;
        let out: Vec<String> = v
            .get("paths")
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(|s| s.as_str().map(str::to_string)).collect())
            .unwrap_or_default();
        if out.len() != paths.len() {
            return Err(protocol("lista de nomes únicos incompleta"));
        }
        Ok(out)
    }

    pub fn mkdir(&self, path: &str) -> Result<()> {
        let p = rp::norm(path);
        self.retry_busy(None, || self.post("/api/ftp/mkdir", &json!({ "path": p })).map(|_| ()))
    }

    pub fn mkdir_all(&self, path: &str, cancel: Option<&Cancel>) -> Result<()> {
        let comps = rp::components(path);
        let mut cur = String::new();

        let mut start = 0;
        for k in (1..=comps.len()).rev() {
            let p = format!("/{}", comps[..k].join("/"));
            if matches!(self.stat(&p), Ok(Some(e)) if e.is_dir) {
                start = k;
                break;
            }
        }
        if start > 0 {
            cur = format!("/{}", comps[..start].join("/"));
        }
        for c in &comps[start..] {
            if let Some(cn) = cancel {
                cn.check()?;
            }
            cur = format!("{cur}/{c}");
            self.retry_busy(cancel, || {
                self.post("/api/ftp/mkdir", &json!({ "path": cur })).map(|_| ())
            })?;
        }
        Ok(())
    }

    pub fn rename(&self, from: &str, to: &str) -> Result<()> {
        let (f, t) = (rp::norm(from), rp::norm(to));
        self.retry_busy(None, || {
            self.post("/api/ftp/rename", &json!({ "from": f, "to": t })).map(|_| ())
        })
    }

    pub fn chmod(&self, path: &str, mode: u32) -> Result<()> {
        let p = rp::norm(path);
        let m = format!("{mode:o}");
        self.retry_busy(None, || {
            self.post("/api/ftp/chmod", &json!({ "path": p, "mode": m }))
                .map(|_| ())
        })
    }

    fn job_id(v: &Value) -> Result<u64> {
        v.get("job_id")
            .and_then(Value::as_u64)
            .ok_or_else(|| protocol("resposta sem número da tarefa"))
    }

    pub fn delete(&self, path: &str) -> Result<u64> {
        let p = rp::norm(path);
        self.retry_busy(None, || {
            Self::job_id(&self.post("/api/ftp/delete", &json!({ "path": p }))?)
        })
    }

    pub fn copy_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        let (s, d) = (rp::norm(source), rp::norm(destination));
        self.retry_busy(None, || {
            Self::job_id(&self.post(
                "/api/ftp/copy",
                &json!({ "source": s, "destination": d, "conflict_policy": policy.as_str() }),
            )?)
        })
    }

    pub fn move_to(&self, source: &str, destination: &str, policy: Conflict) -> Result<u64> {
        let (s, d) = (rp::norm(source), rp::norm(destination));
        self.retry_busy(None, || {
            Self::job_id(&self.post(
                "/api/ftp/move",
                &json!({ "source": s, "destination": d, "conflict_policy": policy.as_str() }),
            )?)
        })
    }

    pub fn zip(&self, paths: &[String], dir: &str, name: &str, compression: &str) -> Result<u64> {
        let body = json!({ "paths": paths, "dir": rp::norm(dir), "name": name, "compression": compression });
        self.retry_busy(None, || Self::job_id(&self.post("/api/ftp/zip-selection", &body)?))
    }

    pub fn unzip(
        &self,
        path: &str,
        destination: Option<&str>,
        policy: Conflict,
        delete_source: bool,
        password: Option<&str>,
    ) -> Result<u64> {
        let mut body =
            json!({ "path": rp::norm(path), "conflict_policy": policy.as_str(), "delete_source": delete_source });
        if let Some(d) = destination {
            body["destination"] = json!(rp::norm(d));
        }
        if let Some(p) = password {
            body["password"] = json!(p);
        }
        self.retry_busy(None, || Self::job_id(&self.post("/api/ftp/unzip", &body)?))
    }

    pub fn jobs(&self) -> Result<Vec<Job>> {
        let v = self.get("/api/files/jobs")?;
        serde_json::from_value(v.get("jobs").cloned().unwrap_or(Value::Null))
            .map_err(|_| protocol("lista de tarefas inválida"))
    }

    pub fn cancel_job(&self, id: u64) -> Result<()> {
        self.post("/api/files/jobs/cancel", &json!({ "id": id })).map(|_| ())
    }

    pub fn clear_jobs(&self) -> Result<()> {
        self.post("/api/files/jobs/clear", &json!({})).map(|_| ())
    }

    pub fn wait_job(&self, id: u64, cancel: &Cancel, mut on_tick: impl FnMut(&Job)) -> Result<Job> {
        let mut missing = 0;
        loop {
            cancel.check()?;
            let jobs = self.jobs()?;
            match jobs.into_iter().find(|j| j.id == id) {
                Some(j) => {
                    on_tick(&j);
                    if j.finished() {
                        return Ok(j);
                    }
                    missing = 0;
                }
                None => {
                    missing += 1;
                    if missing > 3 {
                        return Ok(Job {
                            id,
                            state: "done".into(),
                            ..Default::default()
                        });
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(350));
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
        let v = self.get(&format!("/api/ftp/text?path={}", urlencode(&rp::norm(path))))?;
        Ok(TextFile {
            text: v.get("text").and_then(Value::as_str).unwrap_or("").to_string(),
            version: v.get("version").and_then(Value::as_str).unwrap_or("").to_string(),
            newline: v.get("newline").and_then(Value::as_str).unwrap_or("lf").to_string(),
            bom: v.get("bom").and_then(Value::as_bool).unwrap_or(false),
        })
    }

    pub fn write_text(&self, path: &str, text: &str, version: &str, newline: &str, bom: bool) -> Result<String> {
        let nl = match newline {
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => "\n",
        };
        let mut body = String::with_capacity(text.len() + 3);
        if bom {
            body.push('\u{feff}');
        }
        body.push_str(&text.replace("\r\n", "\n").replace('\r', "\n").replace('\n', nl));
        let target = format!(
            "/api/ftp/text/write?path={}&version={}",
            urlencode(&rp::norm(path)),
            urlencode(version)
        );
        let resp = self.send(
            Request {
                method: "POST",
                target: &target,
                content_type: Some("text/plain;charset=utf-8"),
                body: Body::Bytes(body.as_bytes()),
                response_timeout: Duration::from_secs(30),
            },
            None,
        )?;
        let status = resp.status;
        let bytes = resp.into_bytes(JSON_LIMIT).map_err(map_io)?;
        if status >= 400 {
            return Err(error_from(status, &bytes));
        }
        let v: Value = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
        Ok(v.get("version").and_then(Value::as_str).unwrap_or("").to_string())
    }

    pub fn open_download(&self, path: &str) -> Result<(Response, u64)> {
        let target = format!("/api/ftp/download?path={}", urlencode(&rp::norm(path)));
        let resp = self.send(
            Request {
                method: "GET",
                target: &target,
                content_type: None,
                body: Body::Empty,
                response_timeout: Duration::from_secs(60),
            },
            None,
        )?;
        if resp.status >= 400 {
            let status = resp.status;
            let bytes = resp.into_bytes(1 << 20).map_err(map_io)?;
            return Err(error_from(status, &bytes));
        }
        let len = resp.content_length().ok_or_else(|| protocol("download sem tamanho"))?;
        Ok((resp, len))
    }

    pub fn upload_direct(
        &self,
        path: &str,
        size: u64,
        src: &mut dyn Read,
        overwrite: bool,
        hook: Option<SentHook<'_>>,
    ) -> Result<()> {
        let target = format!(
            "/api/ftp/upload?path={}{}",
            urlencode(&rp::norm(path)),
            if overwrite { "&overwrite=1" } else { "" }
        );
        let resp = self.send(
            Request {
                method: "POST",
                target: &target,
                content_type: Some("application/octet-stream"),
                body: Body::Stream { len: size, src },
                response_timeout: Duration::from_secs(300),
            },
            hook,
        )?;
        let status = resp.status;
        let bytes = resp.into_bytes(JSON_LIMIT).map_err(map_io)?;
        if status >= 400 {
            return Err(error_from(status, &bytes));
        }
        Ok(())
    }

    pub fn post_chunk(&self, req: &ChunkReq<'_>, body: &[u8], hook: Option<SentHook<'_>>) -> Result<ChunkAck> {
        let target = format!(
            "/api/ftp/upload/chunk?path={}&upload_id={}&offset={}&total={}{}",
            urlencode(&rp::norm(req.path)),
            urlencode(req.upload_id),
            req.offset,
            req.total,
            if req.overwrite { "&overwrite=1" } else { "" }
        );
        let resp = self.send(
            Request {
                method: "POST",
                target: &target,
                content_type: Some("application/octet-stream"),
                body: Body::Bytes(body),
                response_timeout: Duration::from_secs(300),
            },
            hook,
        )?;
        let status = resp.status;
        let bytes = resp.into_bytes(JSON_LIMIT).map_err(map_io)?;
        if status >= 400 {
            return Err(error_from(status, &bytes));
        }
        let v: Value = serde_json::from_slice(&bytes).map_err(|_| protocol("resposta de bloco inválida"))?;
        Ok(ChunkAck {
            next_offset: v
                .get("next_offset")
                .and_then(Value::as_u64)
                .ok_or_else(|| protocol("resposta de bloco sem next_offset"))?,
            complete: v.get("complete").and_then(Value::as_bool).unwrap_or(false),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn error_classification() {
        let k = |s: u16, m: &str| RemoteError::from_response(s, m).kind;
        assert_eq!(k(409, "another file operation is in progress"), RemoteKind::Busy);
        assert_eq!(k(409, "this upload checkpoint is already in use"), RemoteKind::Busy);
        assert_eq!(k(409, "destination already exists"), RemoteKind::Exists);
        assert_eq!(
            k(404, "file does not exist or is not a regular file"),
            RemoteKind::NotFound
        );
        assert_eq!(k(507, "x"), RemoteKind::NoSpace);
        assert_eq!(k(507, "failed while writing upload staging file"), RemoteKind::NoSpace);

        assert_eq!(
            k(507, "upload offset does not match the saved checkpoint"),
            RemoteKind::Other
        );
        assert_eq!(
            k(500, "refusing to delete a protected filesystem root"),
            RemoteKind::Denied
        );
        assert_eq!(k(500, "boom"), RemoteKind::Other);
        assert_eq!(RemoteError::from_response(500, "").message, "HTTP 500");
    }

    #[test]
    fn job_states() {
        let j = |s: &str| Job {
            state: s.into(),
            ..Default::default()
        };
        assert!(j("done").finished() && !j("done").failed());
        assert!(j("error").finished() && j("error").failed());
        assert!(j("attention").failed());
        assert!(!j("running").finished());
    }
}
