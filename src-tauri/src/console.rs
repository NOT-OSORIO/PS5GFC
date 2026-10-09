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

use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpStream, UdpSocket};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ps5gfc_core::ctl::Snapshot;
use ps5gfc_core::remote::download::download_paths;
use ps5gfc_core::remote::engine::{conns_for, upload_local};
use ps5gfc_core::remote::ftp::FTP_PORTS;
use ps5gfc_core::remote::path as rp;
use ps5gfc_core::remote::prospero::{Conflict, ConsoleInfo, Entry, Job, StorageVolume, TextFile};
use ps5gfc_core::remote::{Client, Endpoint, Prospero, RemoteKind, DEFAULT_PORT};
use ps5gfc_core::util::default_threads;
use ps5gfc_core::{Ctx, Error};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

use crate::commands::AppState;

#[derive(Serialize)]
pub struct ApiError {
    pub kind: String,
    pub message: String,
}

impl ApiError {
    fn new(kind: &str, message: impl Into<String>) -> Self {
        Self {
            kind: kind.into(),
            message: message.into(),
        }
    }
    fn not_connected() -> Self {
        Self::new("not_connected", ps5gfc_core::t!("err.rm.not_connected"))
    }
}

impl From<Error> for ApiError {
    fn from(e: Error) -> Self {
        let kind = match &e {
            Error::Cancelled => "cancelled".to_string(),
            Error::Remote(r) => serde_json::to_value(r.kind)
                .ok()
                .and_then(|v| v.as_str().map(str::to_string))
                .unwrap_or_else(|| "other".into()),
            _ => "other".into(),
        };
        Self {
            kind,
            message: e.localized(),
        }
    }
}

type Res<T> = Result<T, ApiError>;

async fn blocking<T: Send + 'static>(f: impl FnOnce() -> Res<T> + Send + 'static) -> Res<T> {
    tauri::async_runtime::spawn_blocking(f)
        .await
        .map_err(|e| ApiError::new("other", e.to_string()))?
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

pub struct ConsoleState {
    client: Mutex<Option<Client>>,
    pub transfers: Arc<Transfers>,
}

impl ConsoleState {
    pub fn new(app: AppHandle) -> Self {
        Self {
            client: Mutex::new(None),
            transfers: Transfers::start(app),
        }
    }

    fn client(&self) -> Res<Client> {
        self.client.lock().unwrap().clone().ok_or_else(ApiError::not_connected)
    }
}

fn parse_policy(s: &str) -> Conflict {
    match s {
        "skip" => Conflict::Skip,
        "replace" => Conflict::Replace,
        "keep_both" => Conflict::KeepBoth,
        _ => Conflict::Cancel,
    }
}

#[derive(Deserialize)]
pub struct ConnectReq {
    pub host: String,
    pub port: u16,

    #[serde(default)]
    pub protocol: String,

    #[serde(default)]
    pub user: String,
    #[serde(default)]
    pub pass: String,
}

#[tauri::command]
pub async fn console_connect(state: State<'_, AppState>, req: ConnectReq) -> Res<ConsoleInfo> {
    let client = Client::open(&req.protocol, &req.host, req.port, &req.user, &req.pass)?;
    let c2 = client.clone();
    let info = blocking(move || c2.connect().map_err(ApiError::from)).await?;
    *state.console.client.lock().unwrap() = Some(client);
    Ok(info)
}

#[tauri::command]
pub fn console_disconnect(state: State<'_, AppState>) {
    *state.console.client.lock().unwrap() = None;
}

#[tauri::command]
pub async fn console_ping(state: State<'_, AppState>) -> Res<ConsoleInfo> {
    let c = state.console.client()?;
    blocking(move || c.connect().map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_list(state: State<'_, AppState>, path: String) -> Res<Vec<Entry>> {
    let c = state.console.client()?;
    blocking(move || c.list(&path).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_storage(state: State<'_, AppState>) -> Res<Vec<StorageVolume>> {
    let c = state.console.client()?;
    blocking(move || c.storage().map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_free(state: State<'_, AppState>, path: String) -> Res<Option<u64>> {
    let c = state.console.client()?;
    blocking(move || {
        let v = c.storage()?;
        Ok(c.volume_of(&v, &path).map(|x| x.free))
    })
    .await
}

#[tauri::command]
pub async fn console_mkdir(state: State<'_, AppState>, path: String) -> Res<()> {
    let c = state.console.client()?;
    blocking(move || c.mkdir_all(&path, None).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_rename(state: State<'_, AppState>, from: String, to: String) -> Res<()> {
    let c = state.console.client()?;
    blocking(move || c.rename(&from, &to).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_chmod(
    state: State<'_, AppState>,
    paths: Vec<String>,
    mode: u32,
    recursive: bool,
    dirs: Vec<String>,
) -> Res<u64> {
    let c = state.console.client()?;
    blocking(move || {
        let mut count = 0u64;
        for p in &paths {
            c.chmod(p, mode)?;
            count += 1;
            if recursive && dirs.iter().any(|d| d == p) {
                count += c.chmod_tree(p, mode)?;
            }
        }
        Ok(count)
    })
    .await
}

#[tauri::command]
pub async fn console_delete(state: State<'_, AppState>, paths: Vec<String>) -> Res<Vec<u64>> {
    let c = state.console.client()?;
    blocking(move || {
        let mut ids = Vec::new();
        for p in paths {
            ids.push(c.delete(&p)?);
        }
        Ok(ids)
    })
    .await
}

#[tauri::command]
pub async fn console_paste(
    state: State<'_, AppState>,
    sources: Vec<String>,
    dest_dir: String,
    mode: String,
    policy: String,
) -> Res<Vec<u64>> {
    let c = state.console.client()?;
    let pol = parse_policy(&policy);
    blocking(move || {
        let mut ids = Vec::new();
        for s in sources {
            let mut target = rp::join(&dest_dir, &rp::file_name(&s));

            if target == rp::norm(&s) && mode != "move" {
                target = c.unique_names(&[target])?.into_iter().next().unwrap_or(s.clone());
            }
            let id = if mode == "move" {
                c.move_to(&s, &target, pol)?
            } else {
                c.copy_to(&s, &target, pol)?
            };
            ids.push(id);
        }
        Ok(ids)
    })
    .await
}

#[tauri::command]
pub async fn console_conflicts(state: State<'_, AppState>, paths: Vec<String>) -> Res<Vec<String>> {
    let c = state.console.client()?;
    blocking(move || c.conflicts(&paths).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_jobs(state: State<'_, AppState>) -> Res<Vec<Job>> {
    let c = state.console.client()?;
    blocking(move || c.jobs().map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_cancel_job(state: State<'_, AppState>, id: u64) -> Res<()> {
    let c = state.console.client()?;
    blocking(move || c.cancel_job(id).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_clear_jobs(state: State<'_, AppState>) -> Res<()> {
    let c = state.console.client()?;
    blocking(move || c.clear_jobs().map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_dir_size(state: State<'_, AppState>, path: String) -> Res<u64> {
    let c = state.console.client()?;
    blocking(move || c.dir_size(&path).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_zip(
    state: State<'_, AppState>,
    paths: Vec<String>,
    dir: String,
    name: String,
    compression: String,
) -> Res<u64> {
    let c = state.console.client()?;
    blocking(move || c.zip(&paths, &dir, &name, &compression).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_unzip(
    state: State<'_, AppState>,
    path: String,
    dest: Option<String>,
    policy: String,
    delete_source: bool,
    password: Option<String>,
) -> Res<u64> {
    let c = state.console.client()?;
    let pol = parse_policy(&policy);
    blocking(move || {
        c.unzip(&path, dest.as_deref(), pol, delete_source, password.as_deref())
            .map_err(ApiError::from)
    })
    .await
}

#[tauri::command]
pub async fn console_read_text(state: State<'_, AppState>, path: String) -> Res<TextFile> {
    let c = state.console.client()?;
    blocking(move || c.read_text(&path).map_err(ApiError::from)).await
}

#[tauri::command]
pub async fn console_write_text(
    state: State<'_, AppState>,
    path: String,
    text: String,
    version: String,
    newline: String,
    bom: bool,
) -> Res<String> {
    let c = state.console.client()?;
    blocking(move || {
        c.write_text(&path, &text, &version, &newline, bom)
            .map_err(ApiError::from)
    })
    .await
}

#[derive(Serialize, Clone)]
pub struct Found {
    pub host: String,
    pub port: u16,

    pub protocol: String,
    pub name: String,
    pub version: String,
    pub model: String,
}

fn local_ipv4() -> Option<Ipv4Addr> {
    let s = UdpSocket::bind("0.0.0.0:0").ok()?;
    s.connect("192.0.2.1:9").ok()?;
    match s.local_addr().ok()?.ip() {
        IpAddr::V4(v) if !v.is_loopback() && !v.is_unspecified() => Some(v),
        _ => None,
    }
}

fn ftp_banner(mut s: TcpStream) -> Option<String> {
    use std::io::Read;
    let _ = s.set_read_timeout(Some(Duration::from_millis(900)));
    let mut buf = [0u8; 256];
    let n = s.read(&mut buf).ok()?;
    let line = String::from_utf8_lossy(&buf[..n]);
    let first = line.lines().next()?.trim();
    first
        .strip_prefix("220")
        .map(|r| r.trim_start_matches([' ', '-']).chars().take(80).collect())
}

#[tauri::command]
pub async fn console_scan() -> Res<Vec<Found>> {
    blocking(|| {
        let Some(me) = local_ipv4() else { return Ok(Vec::new()) };
        let o = me.octets();
        let found: Mutex<Vec<Found>> = Mutex::new(Vec::new());
        let next = AtomicUsize::new(1);
        std::thread::scope(|s| {
            for _ in 0..48 {
                s.spawn(|| loop {
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    if i > 254 {
                        break;
                    }
                    let ip = Ipv4Addr::new(o[0], o[1], o[2], i as u8);
                    if ip == me {
                        continue;
                    }
                    let open = |port: u16| {
                        TcpStream::connect_timeout(&SocketAddr::new(IpAddr::V4(ip), port), Duration::from_millis(350))
                            .ok()
                    };
                    if open(DEFAULT_PORT).is_some() {
                        let client = Prospero::new(Endpoint {
                            host: ip.to_string(),
                            port: DEFAULT_PORT,
                        });
                        if let Ok(info) = client.connect() {
                            found.lock().unwrap().push(Found {
                                host: ip.to_string(),
                                port: DEFAULT_PORT,
                                protocol: "prospero".into(),
                                name: info.name,
                                version: info.version,
                                model: info.system.model,
                            });
                        }
                    }
                    for port in FTP_PORTS.iter().copied().filter(|p| *p != 21) {
                        if let Some(banner) = open(port).and_then(ftp_banner) {
                            found.lock().unwrap().push(Found {
                                host: ip.to_string(),
                                port,
                                protocol: "ftp".into(),
                                name: "FTP".into(),
                                version: banner,
                                model: String::new(),
                            });
                            break;
                        }
                    }
                });
            }
        });
        let mut v = found.into_inner().unwrap();
        v.sort_by(|a, b| a.host.cmp(&b.host).then(a.port.cmp(&b.port)));
        Ok(v)
    })
    .await
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TStatus {
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TKind {
    Upload,
    Download,
}

struct Transfer {
    id: u64,
    kind: TKind,

    items: Vec<String>,

    dest: String,
    policy: Conflict,
    client: Client,
    status: TStatus,
    ctx: Ctx,
    error: Option<String>,
    created_ms: u64,
    started_ms: Option<u64>,
    finished_ms: Option<u64>,
    snapshot: Option<Snapshot>,
    result: Option<TResult>,
}

#[derive(Serialize, Clone, Default)]
pub struct TResult {
    pub files: u64,
    pub dirs: u64,
    pub bytes: u64,
    pub skipped: u64,
}

#[derive(Serialize, Clone)]
pub struct TransferView {
    pub id: u64,
    pub kind: TKind,
    pub items: Vec<String>,
    pub dest: String,
    pub status: TStatus,
    pub error: Option<String>,
    pub created_ms: u64,
    pub started_ms: Option<u64>,
    pub finished_ms: Option<u64>,
    pub snapshot: Option<Snapshot>,
    pub result: Option<TResult>,
}

#[derive(Serialize, Clone)]
struct TProgress {
    id: u64,
    snapshot: Snapshot,
}

struct TInner {
    list: Vec<Transfer>,
    next_id: u64,
    closing: bool,
}

pub struct Transfers {
    inner: Mutex<TInner>,
    cv: Condvar,
    app: AppHandle,
    busy: AtomicBool,
}

impl Transfers {
    fn start(app: AppHandle) -> Arc<Self> {
        let t = Arc::new(Transfers {
            inner: Mutex::new(TInner {
                list: Vec::new(),
                next_id: 1,
                closing: false,
            }),
            cv: Condvar::new(),
            app,
            busy: AtomicBool::new(false),
        });
        let w = t.clone();
        std::thread::Builder::new()
            .name("ps5gfc-transfers".into())
            .spawn(move || w.run_loop())
            .expect("transfers thread");
        t
    }

    fn view(t: &Transfer) -> TransferView {
        TransferView {
            id: t.id,
            kind: t.kind,
            items: t.items.clone(),
            dest: t.dest.clone(),
            status: t.status,
            error: t.error.clone(),
            created_ms: t.created_ms,
            started_ms: t.started_ms,
            finished_ms: t.finished_ms,
            snapshot: t.snapshot.clone(),
            result: t.result.clone(),
        }
    }

    pub fn list(&self) -> Vec<TransferView> {
        self.inner.lock().unwrap().list.iter().map(Self::view).collect()
    }

    fn emit(&self) {
        let _ = self.app.emit("transfers-changed", self.list());
    }

    fn add(&self, kind: TKind, items: Vec<String>, dest: String, policy: Conflict, client: Client) -> u64 {
        let id = {
            let mut g = self.inner.lock().unwrap();
            let id = g.next_id;
            g.next_id += 1;
            g.list.push(Transfer {
                id,
                kind,
                items,
                dest,
                policy,
                client,
                status: TStatus::Queued,
                ctx: Ctx::new(),
                error: None,
                created_ms: now_ms(),
                started_ms: None,
                finished_ms: None,
                snapshot: None,
                result: None,
            });
            id
        };
        self.cv.notify_all();
        self.emit();
        id
    }

    pub fn cancel(&self, id: u64) {
        {
            let mut g = self.inner.lock().unwrap();
            if let Some(t) = g.list.iter_mut().find(|t| t.id == id) {
                match t.status {
                    TStatus::Queued => {
                        t.status = TStatus::Cancelled;
                        t.finished_ms = Some(now_ms());
                        t.error = Some(ps5gfc_core::t!("err.cancelled"));
                    }
                    TStatus::Running => t.ctx.cancel.cancel(),
                    _ => {}
                }
            }
        }
        self.emit();
    }

    pub fn remove(&self, id: u64) {
        self.inner
            .lock()
            .unwrap()
            .list
            .retain(|t| !(t.id == id && t.status != TStatus::Running));
        self.emit();
    }

    pub fn clear_finished(&self) {
        self.inner
            .lock()
            .unwrap()
            .list
            .retain(|t| matches!(t.status, TStatus::Queued | TStatus::Running));
        self.emit();
    }

    pub fn shutdown(&self, timeout: Duration) {
        {
            let mut g = self.inner.lock().unwrap();
            g.closing = true;
            for t in g.list.iter_mut() {
                match t.status {
                    TStatus::Queued => t.status = TStatus::Cancelled,
                    TStatus::Running => t.ctx.cancel.cancel(),
                    _ => {}
                }
            }
        }
        self.cv.notify_all();
        let t0 = Instant::now();
        while self.busy.load(Ordering::SeqCst) && t0.elapsed() < timeout {
            std::thread::sleep(Duration::from_millis(15));
        }
    }

    fn run_loop(self: Arc<Self>) {
        loop {
            let (id, kind, items, dest, policy, client, ctx) = {
                let mut g = self.inner.lock().unwrap();
                loop {
                    if g.closing {
                    } else if let Some(t) = g.list.iter_mut().find(|t| t.status == TStatus::Queued) {
                        t.status = TStatus::Running;
                        t.started_ms = Some(now_ms());
                        self.busy.store(true, Ordering::SeqCst);
                        break (
                            t.id,
                            t.kind,
                            t.items.clone(),
                            t.dest.clone(),
                            t.policy,
                            t.client.clone(),
                            t.ctx.clone(),
                        );
                    }
                    g = self.cv.wait(g).unwrap();
                }
            };
            self.emit();

            let done = Arc::new(AtomicBool::new(false));
            let ticker = {
                let me = self.clone();
                let (ctx, done) = (ctx.clone(), done.clone());
                std::thread::spawn(move || {
                    while !done.load(Ordering::Relaxed) {
                        std::thread::sleep(Duration::from_millis(250));
                        me.pump(id, &ctx);
                    }
                })
            };

            let threads = default_threads();
            let result: Result<TResult, Error> = match kind {
                TKind::Upload => {
                    let paths: Vec<PathBuf> = items.iter().map(PathBuf::from).collect();
                    upload_local(&client, &paths, &dest, policy, conns_for(threads), &ctx).map(|s| TResult {
                        files: s.files,
                        dirs: s.dirs,
                        bytes: s.bytes,
                        skipped: s.skipped,
                    })
                }
                TKind::Download => {
                    download_paths(&client, &items, std::path::Path::new(&dest), policy, 3, &ctx).map(|s| TResult {
                        files: s.files,
                        dirs: s.dirs,
                        bytes: s.bytes,
                        skipped: s.skipped,
                    })
                }
            };
            done.store(true, Ordering::Relaxed);
            let _ = ticker.join();
            self.pump(id, &ctx);

            {
                let mut g = self.inner.lock().unwrap();
                if let Some(t) = g.list.iter_mut().find(|t| t.id == id) {
                    t.finished_ms = Some(now_ms());
                    match &result {
                        Ok(r) => {
                            t.status = TStatus::Done;
                            t.result = Some(r.clone());
                        }
                        Err(e) if e.is_cancelled() => {
                            t.status = TStatus::Cancelled;
                            t.error = Some(ps5gfc_core::t!("err.cancelled_user"));
                        }
                        Err(e) => {
                            t.status = TStatus::Failed;
                            t.error = Some(e.localized());
                        }
                    }
                }
            }
            self.busy.store(false, Ordering::SeqCst);
            self.emit();
        }
    }

    fn pump(&self, id: u64, ctx: &Ctx) {
        let snap = ctx.progress.snapshot();
        {
            let mut g = self.inner.lock().unwrap();
            if let Some(t) = g.list.iter_mut().find(|t| t.id == id) {
                t.snapshot = Some(snap.clone());
            }
        }
        let _ = self.app.emit("transfer-progress", TProgress { id, snapshot: snap });
    }
}

#[derive(Deserialize)]
pub struct UploadReq {
    pub paths: Vec<String>,
    pub dest_dir: String,
    pub policy: String,
}

#[tauri::command]
pub fn transfer_upload(state: State<'_, AppState>, req: UploadReq) -> Res<u64> {
    let c = state.console.client()?;
    Ok(state.console.transfers.add(
        TKind::Upload,
        req.paths,
        rp::norm(&req.dest_dir),
        parse_policy(&req.policy),
        c,
    ))
}

#[derive(Deserialize)]
pub struct DownloadReq {
    pub paths: Vec<String>,
    pub local_dir: String,
    pub policy: String,
}

#[tauri::command]
pub fn transfer_download(state: State<'_, AppState>, req: DownloadReq) -> Res<u64> {
    let c = state.console.client()?;
    Ok(state.console.transfers.add(
        TKind::Download,
        req.paths.iter().map(|p| rp::norm(p)).collect(),
        req.local_dir,
        parse_policy(&req.policy),
        c,
    ))
}

#[tauri::command]
pub fn transfer_cancel(state: State<'_, AppState>, id: u64) {
    state.console.transfers.cancel(id)
}

#[tauri::command]
pub fn transfer_remove(state: State<'_, AppState>, id: u64) {
    state.console.transfers.remove(id)
}

#[tauri::command]
pub fn transfer_clear(state: State<'_, AppState>) {
    state.console.transfers.clear_finished()
}

#[tauri::command]
pub fn transfer_list(state: State<'_, AppState>) -> Vec<TransferView> {
    state.console.transfers.list()
}

#[allow(dead_code)]
pub fn is_unreachable(e: &Error) -> bool {
    matches!(e, Error::Remote(r) if r.kind == RemoteKind::Unreachable)
}
