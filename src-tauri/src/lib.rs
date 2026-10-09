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

mod commands;
mod console;
mod jobs;
mod store;

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tauri::{Manager, WindowEvent};

// The window is sized in logical (DPI-independent) pixels: WebView2 already scales its content to the
// monitor's own Windows scaling setting, so no extra compensation is applied here. A separate, user-chosen
// zoom factor (see commands::set_ui_zoom) is layered on top of that by the frontend, independent of DPI.
const BASE_WIDTH: f64 = 1280.0;
const BASE_HEIGHT: f64 = 820.0;
const MIN_WIDTH: f64 = 980.0;
const MIN_HEIGHT: f64 = 620.0;

fn fit_window_to_monitor(app: &tauri::App) {
    let Some(win) = app.get_webview_window("main") else {
        return;
    };
    let Ok(Some(m)) = win.current_monitor() else { return };
    let scale = m.scale_factor().max(0.5);
    let logical_w = m.size().width as f64 / scale;
    let logical_h = m.size().height as f64 / scale;
    let w = (logical_w * 0.92).clamp(MIN_WIDTH, BASE_WIDTH);
    let h = (logical_h * 0.90).clamp(MIN_HEIGHT, BASE_HEIGHT);
    let _ = win.set_min_size(Some(tauri::LogicalSize::new(MIN_WIDTH, MIN_HEIGHT)));
    let _ = win.set_size(tauri::LogicalSize::new(w, h));
    let _ = win.center();
}

fn on_close_requested(window: &tauri::Window, api: &tauri::CloseRequestApi) {
    let Some(state) = window.try_state::<commands::AppState>() else {
        return;
    };
    api.prevent_close();
    if state.closing.swap(true, Ordering::SeqCst) {
        return;
    }
    let _ = window.hide();
    if let Some((_, c)) = state.inspect.lock().unwrap().as_ref() {
        c.cancel.cancel();
    }
    let jobs = state.jobs.clone();
    let transfers = state.console.transfers.clone();
    let app = window.app_handle().clone();
    std::thread::spawn(move || {
        let t = std::thread::spawn(move || transfers.shutdown(Duration::from_secs(10)));
        jobs.shutdown(Duration::from_secs(10));
        let _ = t.join();
        app.exit(0);
    });
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let dir = app
                .path()
                .app_data_dir()
                .unwrap_or_else(|_| std::env::temp_dir().join("ps5gfc"));
            let store = Arc::new(store::Store::new(dir));
            let jobs = jobs::JobManager::start(app.handle().clone());
            app.manage(commands::AppState {
                jobs,
                console: console::ConsoleState::new(app.handle().clone()),
                store,
                initial_path: std::env::args()
                    .skip(1)
                    .find(|a| !a.starts_with('-') && std::path::Path::new(a).exists()),
                inspect: Mutex::new(None),
                inspect_seq: AtomicU64::new(0),
                closing: AtomicBool::new(false),
            });
            fit_window_to_monitor(app);
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                on_close_requested(window, api);
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::app_info,
            commands::initial_path,
            commands::path_info,
            commands::set_language,
            commands::inspect_source,
            commands::cancel_inspect,
            commands::enqueue_job,
            commands::start_queue,
            commands::pause_queue,
            commands::queue_running,
            commands::cancel_job,
            commands::remove_job,
            commands::clear_finished,
            commands::list_jobs,
            commands::job_logs,
            commands::settings_get,
            commands::settings_set,
            commands::default_output_dir,
            commands::free_space,
            console::console_connect,
            console::console_disconnect,
            console::console_ping,
            console::console_list,
            console::console_storage,
            console::console_free,
            console::console_mkdir,
            console::console_rename,
            console::console_chmod,
            console::console_delete,
            console::console_paste,
            console::console_conflicts,
            console::console_jobs,
            console::console_cancel_job,
            console::console_clear_jobs,
            console::console_dir_size,
            console::console_zip,
            console::console_unzip,
            console::console_read_text,
            console::console_write_text,
            console::console_scan,
            console::transfer_upload,
            console::transfer_download,
            console::transfer_cancel,
            console::transfer_remove,
            console::transfer_clear,
            console::transfer_list,
        ])
        .run(tauri::generate_context!())
        .expect("failed to start PS5GFC");
}
