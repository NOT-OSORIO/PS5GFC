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

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ps5gfc_core::convert::{convert_to, ConvertOptions, ConvertReport, Dest};
use ps5gfc_core::ctl::{LogEntry, Snapshot};
use ps5gfc_core::remote::Client;
use ps5gfc_core::source::Format;
use ps5gfc_core::Ctx;
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ConsoleSpec {
    pub host: String,
    pub port: u16,

    #[serde(default)]
    pub protocol: String,

    #[serde(default)]
    pub user: String,

    #[serde(default, skip_serializing)]
    pub pass: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct JobSpec {
    pub source: String,
    pub target: Format,
    pub dest: String,
    pub options: ConvertOptions,

    pub label: Option<String>,
    pub icon: Option<String>,

    #[serde(default)]
    pub console: Option<ConsoleSpec>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Status {
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

struct Job {
    id: u64,
    spec: JobSpec,
    status: Status,
    ctx: Ctx,
    report: Option<ConvertReport>,
    error: Option<String>,
    created_ms: u64,
    started_ms: Option<u64>,
    finished_ms: Option<u64>,
    logs: Vec<LogEntry>,
    snapshot: Option<Snapshot>,
}

#[derive(Serialize, Clone)]
pub struct JobView {
    pub id: u64,
    pub source: String,
    pub target: Format,
    pub dest: String,
    pub console: Option<ConsoleSpec>,
    pub label: Option<String>,
    pub icon: Option<String>,
    pub status: Status,
    pub report: Option<ConvertReport>,
    pub error: Option<String>,
    pub created_ms: u64,
    pub started_ms: Option<u64>,
    pub finished_ms: Option<u64>,
    pub snapshot: Option<Snapshot>,
    pub options: ConvertOptionsView,
}

#[derive(Serialize, Clone)]
pub struct ConvertOptionsView {
    pub threads: usize,
    pub level: u32,
    pub rebuild: bool,
    pub verify: bool,
}

#[derive(Serialize, Clone)]
struct ProgressEvent {
    id: u64,
    snapshot: Snapshot,
}

#[derive(Serialize, Clone)]
struct LogEvent {
    id: u64,
    entries: Vec<LogEntry>,
}

struct Inner {
    jobs: Vec<Job>,
    next_id: u64,

    closing: bool,

    started: bool,
}

pub struct JobManager {
    inner: Mutex<Inner>,
    cv: Condvar,
    app: AppHandle,

    busy: AtomicBool,
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

impl JobManager {
    pub fn start(app: AppHandle) -> Arc<Self> {
        let m = Arc::new(JobManager {
            inner: Mutex::new(Inner {
                jobs: Vec::new(),
                next_id: 1,
                closing: false,
                started: false,
            }),
            cv: Condvar::new(),
            app,
            busy: AtomicBool::new(false),
        });
        let worker = m.clone();
        std::thread::Builder::new()
            .name("ps5gfc-jobs".into())
            .spawn(move || worker.run_loop())
            .expect("jobs thread");
        m
    }

    fn view(j: &Job) -> JobView {
        JobView {
            id: j.id,
            source: j.spec.source.clone(),
            target: j.spec.target,
            dest: j.spec.dest.clone(),
            console: j.spec.console.clone(),
            label: j.spec.label.clone(),
            icon: j.spec.icon.clone(),
            status: j.status,
            report: j.report.clone(),
            error: j.error.clone(),
            created_ms: j.created_ms,
            started_ms: j.started_ms,
            finished_ms: j.finished_ms,
            snapshot: j.snapshot.clone(),
            options: ConvertOptionsView {
                threads: j.spec.options.worker_threads(),
                level: j.spec.options.level,
                rebuild: j.spec.options.rebuild,
                verify: j.spec.options.verify,
            },
        }
    }

    pub fn list(&self) -> Vec<JobView> {
        self.inner.lock().unwrap().jobs.iter().map(Self::view).collect()
    }

    pub fn logs(&self, id: u64) -> Vec<LogEntry> {
        self.inner
            .lock()
            .unwrap()
            .jobs
            .iter()
            .find(|j| j.id == id)
            .map(|j| j.logs.clone())
            .unwrap_or_default()
    }

    fn emit_jobs(&self) {
        let _ = self.app.emit("jobs-changed", self.list());
    }

    pub fn running(&self) -> bool {
        self.inner.lock().unwrap().started
    }

    fn emit_queue(&self) {
        let _ = self.app.emit("queue-state", self.running());
    }

    pub fn start_queue(&self) {
        {
            let mut g = self.inner.lock().unwrap();
            if g.closing {
                return;
            }
            g.started = true;
        }
        self.cv.notify_all();
        self.emit_queue();
    }

    pub fn pause_queue(&self) {
        self.inner.lock().unwrap().started = false;
        self.emit_queue();
    }

    pub fn enqueue(&self, spec: JobSpec) -> u64 {
        let id = {
            let mut g = self.inner.lock().unwrap();
            let id = g.next_id;
            g.next_id += 1;
            g.jobs.push(Job {
                id,
                spec,
                status: Status::Queued,
                ctx: Ctx::new(),
                report: None,
                error: None,
                created_ms: now_ms(),
                started_ms: None,
                finished_ms: None,
                logs: Vec::new(),
                snapshot: None,
            });
            id
        };
        self.cv.notify_all();
        self.emit_jobs();
        id
    }

    pub fn cancel(&self, id: u64) {
        {
            let mut g = self.inner.lock().unwrap();
            if let Some(j) = g.jobs.iter_mut().find(|j| j.id == id) {
                match j.status {
                    Status::Queued => {
                        j.status = Status::Cancelled;
                        j.finished_ms = Some(now_ms());
                        j.error = Some(ps5gfc_core::t!("err.cancelled_user"));
                    }
                    Status::Running => j.ctx.cancel.cancel(),
                    _ => {}
                }
            }
        }
        self.emit_jobs();
    }

    pub fn remove(&self, id: u64) {
        {
            let mut g = self.inner.lock().unwrap();
            g.jobs.retain(|j| !(j.id == id && j.status != Status::Running));
        }
        self.emit_jobs();
    }

    pub fn clear_finished(&self) {
        {
            let mut g = self.inner.lock().unwrap();
            g.jobs.retain(|j| matches!(j.status, Status::Queued | Status::Running));
        }
        self.emit_jobs();
    }

    pub fn shutdown(&self, timeout: Duration) {
        {
            let mut g = self.inner.lock().unwrap();
            g.closing = true;
            for j in g.jobs.iter_mut() {
                match j.status {
                    Status::Queued => {
                        j.status = Status::Cancelled;
                        j.finished_ms = Some(now_ms());
                    }
                    Status::Running => j.ctx.cancel.cancel(),
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
            let (id, spec, ctx) = {
                let mut g = self.inner.lock().unwrap();
                loop {
                    if !g.closing && g.started {
                        if let Some(j) = g.jobs.iter_mut().find(|j| j.status == Status::Queued) {
                            j.status = Status::Running;
                            j.started_ms = Some(now_ms());
                            self.busy.store(true, Ordering::SeqCst);
                            break (j.id, j.spec.clone(), j.ctx.clone());
                        }

                        g.started = false;
                        drop(g);
                        self.emit_queue();
                        g = self.inner.lock().unwrap();
                    }
                    g = self.cv.wait(g).unwrap();
                }
            };
            self.emit_jobs();

            let done = Arc::new(AtomicBool::new(false));
            let ticker = {
                let me = self.clone();
                let ctx = ctx.clone();
                let done = done.clone();
                std::thread::spawn(move || {
                    while !done.load(Ordering::Relaxed) {
                        std::thread::sleep(Duration::from_millis(250));
                        me.pump(id, &ctx);
                    }
                })
            };

            let src = PathBuf::from(&spec.source);
            let dest = match &spec.console {
                Some(c) => Client::open(&c.protocol, &c.host, c.port, &c.user, &c.pass).map(|client| Dest::Console {
                    client,
                    dir: spec.dest.clone(),
                }),
                None => Ok(Dest::Local(PathBuf::from(&spec.dest))),
            };
            let result = dest.and_then(|d| convert_to(&src, &d, spec.target, &spec.options, &ctx));
            done.store(true, Ordering::Relaxed);
            let _ = ticker.join();
            self.pump(id, &ctx);

            {
                let mut g = self.inner.lock().unwrap();
                if let Some(j) = g.jobs.iter_mut().find(|j| j.id == id) {
                    j.finished_ms = Some(now_ms());
                    match &result {
                        Ok(rep) => {
                            j.status = Status::Done;
                            j.report = Some(rep.clone());
                        }
                        Err(e) if e.is_cancelled() => {
                            j.status = Status::Cancelled;
                            j.error = Some(ps5gfc_core::t!("err.cancelled_user"));
                        }
                        Err(e) => {
                            j.status = Status::Failed;
                            j.error = Some(e.localized());
                        }
                    }
                }
            }
            self.busy.store(false, Ordering::SeqCst);
            self.emit_jobs();
        }
    }

    fn pump(&self, id: u64, ctx: &Ctx) {
        let snap = ctx.progress.snapshot();
        let logs = ctx.progress.drain_logs();
        {
            let mut g = self.inner.lock().unwrap();
            if let Some(j) = g.jobs.iter_mut().find(|j| j.id == id) {
                j.snapshot = Some(snap.clone());
                if !logs.is_empty() {
                    j.logs.extend(logs.iter().cloned());
                    if j.logs.len() > 4000 {
                        let drop = j.logs.len() - 4000;
                        j.logs.drain(..drop);
                    }
                }
            }
        }
        let _ = self.app.emit("job-progress", ProgressEvent { id, snapshot: snap });
        if !logs.is_empty() {
            let _ = self.app.emit("job-log", LogEvent { id, entries: logs });
        }
    }
}
