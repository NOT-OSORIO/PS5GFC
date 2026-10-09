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

use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use serde::Serialize;

use crate::{Error, Result};

#[derive(Clone, Default)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::Relaxed)
    }
    pub fn check(&self) -> Result<()> {
        if self.is_cancelled() {
            Err(Error::Cancelled)
        } else {
            Ok(())
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
#[repr(u8)]
pub enum Stage {
    Idle = 0,
    Scanning = 1,
    Planning = 2,
    Processing = 3,
    Finalizing = 4,
    Verifying = 5,
    Done = 6,
}

impl Stage {
    fn from_u8(v: u8) -> Stage {
        match v {
            1 => Stage::Scanning,
            2 => Stage::Planning,
            3 => Stage::Processing,
            4 => Stage::Finalizing,
            5 => Stage::Verifying,
            6 => Stage::Done,
            _ => Stage::Idle,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum LogLevel {
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Serialize)]
pub struct LogEntry {
    pub level: LogLevel,
    pub msg: String,
    pub t_ms: u64,
}

#[derive(Debug, Clone, Serialize)]
pub struct Snapshot {
    pub stage: Stage,

    pub done: u64,

    pub total: u64,

    pub out_bytes: u64,
    pub files_done: u64,
    pub files_total: u64,
    pub current: String,
    pub elapsed_ms: u64,

    pub speed_bps: f64,
    pub eta_secs: Option<f64>,

    pub ratio: Option<f64>,
}

struct SpeedState {
    last_t: f64,
    last_done: u64,
    ewma: f64,
}

struct Inner {
    stage: AtomicU8,
    done: AtomicU64,
    total: AtomicU64,
    out_bytes: AtomicU64,
    files_done: AtomicU64,
    files_total: AtomicU64,
    current: Mutex<String>,
    logs: Mutex<Vec<LogEntry>>,
    speed: Mutex<SpeedState>,
    started: Instant,
}

#[derive(Clone)]
pub struct Progress(Arc<Inner>);

impl Default for Progress {
    fn default() -> Self {
        Self::new()
    }
}

impl Progress {
    pub fn new() -> Self {
        Progress(Arc::new(Inner {
            stage: AtomicU8::new(Stage::Idle as u8),
            done: AtomicU64::new(0),
            total: AtomicU64::new(0),
            out_bytes: AtomicU64::new(0),
            files_done: AtomicU64::new(0),
            files_total: AtomicU64::new(0),
            current: Mutex::new(String::new()),
            logs: Mutex::new(Vec::new()),
            speed: Mutex::new(SpeedState {
                last_t: 0.0,
                last_done: 0,
                ewma: 0.0,
            }),
            started: Instant::now(),
        }))
    }

    pub fn set_stage(&self, s: Stage) {
        self.0.stage.store(s as u8, Ordering::Relaxed);
    }
    pub fn stage(&self) -> Stage {
        Stage::from_u8(self.0.stage.load(Ordering::Relaxed))
    }

    pub fn begin_phase(&self, stage: Stage, total: u64) {
        self.set_stage(stage);
        self.0.total.store(total, Ordering::Relaxed);
        self.0.done.store(0, Ordering::Relaxed);
        self.0.out_bytes.store(0, Ordering::Relaxed);
        let mut sp = self.0.speed.lock().unwrap();
        sp.last_t = self.0.started.elapsed().as_secs_f64();
        sp.last_done = 0;
        sp.ewma = 0.0;
    }
    pub fn set_total(&self, total: u64) {
        self.0.total.store(total, Ordering::Relaxed);
    }
    pub fn add_done(&self, n: u64) {
        self.0.done.fetch_add(n, Ordering::Relaxed);
    }
    pub fn add_out(&self, n: u64) {
        self.0.out_bytes.fetch_add(n, Ordering::Relaxed);
    }

    pub fn sub_done(&self, n: u64) {
        let _ = self
            .0
            .done
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |v| Some(v.saturating_sub(n)));
    }
    pub fn sub_out(&self, n: u64) {
        let _ = self
            .0
            .out_bytes
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |v| Some(v.saturating_sub(n)));
    }
    pub fn set_files_total(&self, n: u64) {
        self.0.files_total.store(n, Ordering::Relaxed);
    }
    pub fn add_files_done(&self, n: u64) {
        self.0.files_done.fetch_add(n, Ordering::Relaxed);
    }
    pub fn set_current(&self, s: &str) {
        if let Ok(mut g) = self.0.current.try_lock() {
            g.clear();
            g.push_str(s);
        }
    }

    pub fn log(&self, level: LogLevel, msg: impl Into<String>) {
        let mut l = self.0.logs.lock().unwrap();
        if l.len() < 5000 {
            l.push(LogEntry {
                level,
                msg: msg.into(),
                t_ms: self.0.started.elapsed().as_millis() as u64,
            });
        }
    }
    pub fn info(&self, msg: impl Into<String>) {
        self.log(LogLevel::Info, msg)
    }
    pub fn warn(&self, msg: impl Into<String>) {
        self.log(LogLevel::Warn, msg)
    }

    pub fn drain_logs(&self) -> Vec<LogEntry> {
        std::mem::take(&mut *self.0.logs.lock().unwrap())
    }

    pub fn snapshot(&self) -> Snapshot {
        let now = self.0.started.elapsed().as_secs_f64();
        let done = self.0.done.load(Ordering::Relaxed);
        let total = self.0.total.load(Ordering::Relaxed);
        let out = self.0.out_bytes.load(Ordering::Relaxed);
        let (speed, eta) = {
            let mut sp = self.0.speed.lock().unwrap();
            let dt = now - sp.last_t;
            if dt >= 0.25 {
                let inst = (done.saturating_sub(sp.last_done)) as f64 / dt;
                sp.ewma = if sp.ewma <= 0.0 {
                    inst
                } else {
                    0.8 * sp.ewma + 0.2 * inst
                };
                sp.last_t = now;
                sp.last_done = done;
            }
            let eta = if sp.ewma > 1.0 && total > done {
                Some((total - done) as f64 / sp.ewma)
            } else {
                None
            };
            (sp.ewma, eta)
        };
        Snapshot {
            stage: self.stage(),
            done,
            total,
            out_bytes: out,
            files_done: self.0.files_done.load(Ordering::Relaxed),
            files_total: self.0.files_total.load(Ordering::Relaxed),
            current: self.0.current.lock().map(|g| g.clone()).unwrap_or_default(),
            elapsed_ms: (now * 1000.0) as u64,
            speed_bps: speed,
            eta_secs: eta,
            ratio: if done > 0 && out > 0 {
                Some(out as f64 / done as f64)
            } else {
                None
            },
        }
    }
}

#[derive(Clone, Default)]
pub struct Ctx {
    pub cancel: Cancel,
    pub progress: Progress,
}

impl Ctx {
    pub fn new() -> Self {
        Self::default()
    }
}
