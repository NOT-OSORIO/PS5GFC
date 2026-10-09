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

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use ps5gfc_core::ctl::{LogEntry, Stage};
use ps5gfc_core::inspect::{inspect, SourceInfo};
use ps5gfc_core::util::default_threads;
use ps5gfc_core::Ctx;
use serde::Serialize;
use serde_json::Value;
use tauri::{AppHandle, Emitter, State};

use crate::console::ConsoleState;
use crate::jobs::{JobManager, JobSpec, JobView};
use crate::store::Store;

pub struct AppState {
    pub jobs: Arc<JobManager>,
    pub console: ConsoleState,
    pub store: Arc<Store>,

    pub initial_path: Option<String>,

    pub inspect: Mutex<Option<(u64, Ctx)>>,
    pub inspect_seq: AtomicU64,

    pub closing: AtomicBool,
}

#[derive(Serialize)]
pub struct AppInfo {
    pub version: String,
    pub logical_cpus: usize,
    pub default_threads: usize,
    pub ram_bytes: Option<u64>,
    pub data_dir: String,
}

#[derive(Serialize)]
pub struct PathInfo {
    pub exists: bool,
    pub is_dir: bool,
    pub name: String,
}

#[derive(Serialize, Clone)]
struct InspectProgress {
    stage: Stage,
    files: u64,
    total: u64,
    current: String,
    elapsed_ms: u64,
}

#[tauri::command]
pub fn app_info(state: State<'_, AppState>) -> AppInfo {
    AppInfo {
        version: env!("CARGO_PKG_VERSION").to_string(),
        logical_cpus: ps5gfc_core::sys::logical_cpus(),
        default_threads: default_threads(),
        ram_bytes: ps5gfc_core::sys::total_memory(),
        data_dir: state.store.dir().to_string_lossy().into_owned(),
    }
}

#[tauri::command]
pub fn initial_path(state: State<'_, AppState>) -> Option<String> {
    state.initial_path.clone()
}

#[tauri::command]
pub async fn path_info(path: String) -> PathInfo {
    tauri::async_runtime::spawn_blocking(move || {
        let p = Path::new(&path);
        PathInfo {
            exists: p.exists(),
            is_dir: p.is_dir(),
            name: p
                .file_name()
                .map(|s| s.to_string_lossy().into_owned())
                .unwrap_or_default(),
        }
    })
    .await
    .unwrap_or(PathInfo {
        exists: false,
        is_dir: false,
        name: String::new(),
    })
}

#[tauri::command]
pub fn set_language(lang: String) {
    ps5gfc_core::i18n::set_lang(&lang);
}

#[tauri::command]
pub async fn inspect_source(app: AppHandle, state: State<'_, AppState>, path: String) -> Result<SourceInfo, String> {
    let ctx = Ctx::new();
    let seq = state.inspect_seq.fetch_add(1, Ordering::SeqCst) + 1;
    {
        let mut g = state.inspect.lock().unwrap();
        if let Some((_, old)) = g.replace((seq, ctx.clone())) {
            old.cancel.cancel();
        }
    }
    ctx.progress.begin_phase(Stage::Scanning, 0);

    let done = Arc::new(AtomicBool::new(false));
    let ticker = {
        let (ctx, done, app) = (ctx.clone(), done.clone(), app.clone());
        std::thread::spawn(move || {
            while !done.load(Ordering::Relaxed) {
                let s = ctx.progress.snapshot();
                let _ = app.emit(
                    "inspect-progress",
                    InspectProgress {
                        stage: s.stage,
                        files: s.files_done,
                        total: s.files_total,
                        current: s.current,
                        elapsed_ms: s.elapsed_ms,
                    },
                );
                std::thread::sleep(Duration::from_millis(100));
            }
        })
    };

    let p = PathBuf::from(path);
    let c2 = ctx.clone();
    let res = tauri::async_runtime::spawn_blocking(move || inspect(&p, &c2).map(|(_src, info)| info)).await;
    done.store(true, Ordering::Relaxed);
    let _ = ticker.join();
    {
        let mut g = state.inspect.lock().unwrap();

        if matches!(g.as_ref(), Some((s, _)) if *s == seq) {
            *g = None;
        }
    }
    match res {
        Ok(Ok(info)) => Ok(info),
        Ok(Err(e)) if e.is_cancelled() => Err("cancelled".into()),
        Ok(Err(e)) => Err(e.localized()),
        Err(e) => Err(e.to_string()),
    }
}

#[tauri::command]
pub fn cancel_inspect(state: State<'_, AppState>) {
    if let Some((_, c)) = state.inspect.lock().unwrap().as_ref() {
        c.cancel.cancel();
    }
}

#[tauri::command]
pub async fn enqueue_job(state: State<'_, AppState>, spec: JobSpec) -> Result<u64, String> {
    Ok(state.jobs.enqueue(spec))
}

#[tauri::command]
pub async fn start_queue(state: State<'_, AppState>) -> Result<(), String> {
    state.jobs.start_queue();
    Ok(())
}

#[tauri::command]
pub async fn pause_queue(state: State<'_, AppState>) -> Result<(), String> {
    state.jobs.pause_queue();
    Ok(())
}

#[tauri::command]
pub async fn queue_running(state: State<'_, AppState>) -> Result<bool, String> {
    Ok(state.jobs.running())
}

#[tauri::command]
pub async fn cancel_job(state: State<'_, AppState>, id: u64) -> Result<(), String> {
    state.jobs.cancel(id);
    Ok(())
}

#[tauri::command]
pub async fn remove_job(state: State<'_, AppState>, id: u64) -> Result<(), String> {
    state.jobs.remove(id);
    Ok(())
}

#[tauri::command]
pub async fn clear_finished(state: State<'_, AppState>) -> Result<(), String> {
    state.jobs.clear_finished();
    Ok(())
}

#[tauri::command]
pub async fn list_jobs(state: State<'_, AppState>) -> Result<Vec<JobView>, String> {
    Ok(state.jobs.list())
}

#[tauri::command]
pub async fn job_logs(state: State<'_, AppState>, id: u64) -> Result<Vec<LogEntry>, String> {
    Ok(state.jobs.logs(id))
}

#[tauri::command]
pub async fn settings_get(state: State<'_, AppState>) -> Result<Value, String> {
    let store = state.store.clone();
    tauri::async_runtime::spawn_blocking(move || store.read("settings").unwrap_or(Value::Null))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn settings_set(state: State<'_, AppState>, value: Value) -> Result<(), String> {
    let store = state.store.clone();
    tauri::async_runtime::spawn_blocking(move || store.write("settings", &value).map_err(|e| e.to_string()))
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub fn default_output_dir() -> String {
    let base = std::env::var("USERPROFILE")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."));
    base.join("Documents").join("PS5GFC").to_string_lossy().into_owned()
}

#[tauri::command]
pub async fn free_space(path: String) -> Option<u64> {
    tauri::async_runtime::spawn_blocking(move || ps5gfc_core::sys::free_space(Path::new(&path)))
        .await
        .ok()
        .flatten()
}
