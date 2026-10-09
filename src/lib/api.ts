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

import type { AppInfo, JobSpec, JobView, LogEntry, PathInfo, ReadProgress, Snapshot, SourceInfo } from "./types";
import type { ConnectTarget, ConsoleInfo, ConsoleJob, FoundConsole, Policy, RemoteEntry, StorageVolume, TransferView } from "./console";
import * as mock from "./mock";
import * as mc from "./mockConsole";

export const isTauri = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

async function call<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<T>(cmd, args);
}

type Unlisten = () => void;

async function on<T>(event: string, cb: (payload: T) => void): Promise<Unlisten> {
  const { listen } = await import("@tauri-apps/api/event");
  return listen<T>(event, (e) => cb(e.payload));
}

export const api = {
  appInfo: (): Promise<AppInfo> => (isTauri ? call("app_info") : mock.appInfo()),
  initialPath: (): Promise<string | null> => (isTauri ? call("initial_path") : Promise.resolve(null)),
  pathInfo: (path: string): Promise<PathInfo> => (isTauri ? call("path_info", { path }) : mock.pathInfo(path)),
  setLanguage: (lang: string): Promise<void> => (isTauri ? call("set_language", { lang }) : Promise.resolve()),
  inspect: (path: string): Promise<SourceInfo> => (isTauri ? call("inspect_source", { path }) : mock.inspect(path)),
  cancelInspect: (): Promise<void> => (isTauri ? call("cancel_inspect") : Promise.resolve(mock.cancelInspect())),
  enqueue: (spec: JobSpec): Promise<number> => (isTauri ? call("enqueue_job", { spec }) : mock.enqueue(spec)),
  startQueue: (): Promise<void> => (isTauri ? call("start_queue") : Promise.resolve(mock.startQueue())),
  pauseQueue: (): Promise<void> => (isTauri ? call("pause_queue") : Promise.resolve(mock.pauseQueue())),
  queueRunning: (): Promise<boolean> => (isTauri ? call("queue_running") : Promise.resolve(mock.queueRunning())),
  cancel: (id: number): Promise<void> => (isTauri ? call("cancel_job", { id }) : mock.cancel(id)),
  remove: (id: number): Promise<void> => (isTauri ? call("remove_job", { id }) : mock.remove(id)),
  clearFinished: (): Promise<void> => (isTauri ? call("clear_finished") : mock.clearFinished()),
  listJobs: (): Promise<JobView[]> => (isTauri ? call("list_jobs") : mock.listJobs()),
  jobLogs: (id: number): Promise<LogEntry[]> => (isTauri ? call("job_logs", { id }) : Promise.resolve([])),
  settingsGet: (): Promise<unknown> => (isTauri ? call("settings_get") : Promise.resolve(null)),
  settingsSet: (value: unknown): Promise<void> => (isTauri ? call("settings_set", { value }) : Promise.resolve()),
  defaultOutputDir: (): Promise<string> => (isTauri ? call("default_output_dir") : Promise.resolve("C:\\Users\\user\\Documents\\PS5GFC")),
  async setZoom(factor: number): Promise<void> {
    if (!isTauri) return;
    const { getCurrentWebview } = await import("@tauri-apps/api/webview");
    await getCurrentWebview().setZoom(factor);
  },
  freeSpace: (path: string): Promise<number | null> => (isTauri ? call("free_space", { path }) : Promise.resolve(412 * 1024 ** 3)),

  onJobsChanged: (cb: (jobs: JobView[]) => void): Promise<Unlisten> => (isTauri ? on("jobs-changed", cb) : mock.onJobsChanged(cb)),
  onProgress: (cb: (p: { id: number; snapshot: Snapshot }) => void): Promise<Unlisten> => (isTauri ? on("job-progress", cb) : mock.onProgress(cb)),
  onLog: (cb: (p: { id: number; entries: LogEntry[] }) => void): Promise<Unlisten> => (isTauri ? on("job-log", cb) : mock.onLog(cb)),
  onQueueState: (cb: (running: boolean) => void): Promise<Unlisten> => (isTauri ? on("queue-state", cb) : Promise.resolve(mock.onQueueState(cb))),
  onInspectProgress: (cb: (p: ReadProgress) => void): Promise<Unlisten> =>
    isTauri ? on("inspect-progress", cb) : Promise.resolve(mock.onInspectProgress(cb)),

  consoleConnect: (req: ConnectTarget): Promise<ConsoleInfo> => (isTauri ? call("console_connect", { req }) : mc.connect(req)),
  consoleDisconnect: (): Promise<void> => (isTauri ? call("console_disconnect") : mc.disconnect()),
  consolePing: (): Promise<ConsoleInfo> => (isTauri ? call("console_ping") : mc.ping()),
  consoleScan: (): Promise<FoundConsole[]> => (isTauri ? call("console_scan") : mc.scan()),
  consoleList: (path: string): Promise<RemoteEntry[]> => (isTauri ? call("console_list", { path }) : mc.list(path)),
  consoleStorage: (): Promise<StorageVolume[]> => (isTauri ? call("console_storage") : mc.storage()),
  consoleFree: (path: string): Promise<number | null> => (isTauri ? call("console_free", { path }) : mc.free(path)),
  consoleMkdir: (path: string): Promise<void> => (isTauri ? call("console_mkdir", { path }) : mc.mkdir(path)),
  consoleRename: (from: string, to: string): Promise<void> => (isTauri ? call("console_rename", { from, to }) : mc.rename(from, to)),
  consoleChmod: (paths: string[], mode: number, recursive = false, dirs: string[] = []): Promise<number> =>
    (isTauri ? call("console_chmod", { paths, mode, recursive, dirs }) : mc.chmod(paths, mode)),
  consoleDelete: (paths: string[]): Promise<number[]> => (isTauri ? call("console_delete", { paths }) : mc.remove(paths)),
  consolePaste: (sources: string[], destDir: string, mode: "copy" | "move", policy: Policy): Promise<number[]> =>
    isTauri ? call("console_paste", { sources, destDir, mode, policy }) : mc.paste(sources, destDir, mode, policy),
  consoleConflicts: (paths: string[]): Promise<string[]> => (isTauri ? call("console_conflicts", { paths }) : mc.conflicts(paths)),
  consoleJobs: (): Promise<ConsoleJob[]> => (isTauri ? call("console_jobs") : mc.jobs()),
  consoleCancelJob: (id: number): Promise<void> => (isTauri ? call("console_cancel_job", { id }) : mc.cancelJob(id)),
  consoleClearJobs: (): Promise<void> => (isTauri ? call("console_clear_jobs") : mc.clearJobs()),
  consoleDirSize: (path: string): Promise<number> => (isTauri ? call("console_dir_size", { path }) : mc.dirSize(path)),
  consoleZip: (paths: string[], dir: string, name: string, compression: string): Promise<number> =>
    isTauri ? call("console_zip", { paths, dir, name, compression }) : mc.zip(paths, dir, name),
  consoleUnzip: (path: string, dest: string | null, policy: Policy, deleteSource: boolean, password: string | null): Promise<number> =>
    isTauri ? call("console_unzip", { path, dest, policy, deleteSource, password }) : mc.unzip(path, dest),
  consoleReadText: (path: string): Promise<{ text: string; version: string; newline: string; bom: boolean }> =>
    isTauri ? call("console_read_text", { path }) : mc.readText(path),
  consoleWriteText: (path: string, text: string, version: string, newline: string, bom: boolean): Promise<string> =>
    isTauri ? call("console_write_text", { path, text, version, newline, bom }) : mc.writeText(path, text),
  transferUpload: (paths: string[], destDir: string, policy: Policy): Promise<number> =>
    isTauri ? call("transfer_upload", { req: { paths, dest_dir: destDir, policy } }) : mc.transferUpload(paths, destDir),
  transferDownload: (paths: string[], localDir: string, policy: Policy): Promise<number> =>
    isTauri ? call("transfer_download", { req: { paths, local_dir: localDir, policy } }) : mc.transferDownload(paths, localDir),
  transferCancel: (id: number): Promise<void> => (isTauri ? call("transfer_cancel", { id }) : mc.transferCancel(id)),
  transferRemove: (id: number): Promise<void> => (isTauri ? call("transfer_remove", { id }) : mc.transferRemove(id)),
  transferClear: (): Promise<void> => (isTauri ? call("transfer_clear") : mc.transferClear()),
  transferList: (): Promise<TransferView[]> => (isTauri ? call("transfer_list") : mc.transferList()),
  onTransfers: (cb: (list: TransferView[]) => void): Promise<Unlisten> => (isTauri ? on("transfers-changed", cb) : mc.onTransfers(cb)),
  onTransferProgress: (cb: (p: { id: number; snapshot: Snapshot }) => void): Promise<Unlisten> =>
    isTauri ? on("transfer-progress", cb) : mc.onTransferProgress(cb),

  pickFiles: async (): Promise<string[]> => {
    if (!isTauri) return mc.pickFiles();
    const { open } = await import("@tauri-apps/plugin-dialog");
    const r = await open({ directory: false, multiple: true });
    return Array.isArray(r) ? r : typeof r === "string" ? [r] : [];
  },

  pickFolders: async (): Promise<string[]> => {
    if (!isTauri) return mc.pickFolders();
    const { open } = await import("@tauri-apps/plugin-dialog");
    const r = await open({ directory: true, multiple: true });
    return Array.isArray(r) ? r : typeof r === "string" ? [r] : [];
  },

  pickFolder: async (title: string): Promise<string | null> => {
    if (!isTauri) return mock.pick(true);
    const { open } = await import("@tauri-apps/plugin-dialog");
    const r = await open({ directory: true, multiple: false, title });
    return typeof r === "string" ? r : null;
  },
  pickImage: async (): Promise<string | null> => {
    if (!isTauri) return mock.pick(false);
    const { open } = await import("@tauri-apps/plugin-dialog");
    const r = await open({
      directory: false,
      multiple: false,
      filters: [{ name: "PS5", extensions: ["exfat", "ffpkg", "ffpfs", "ffpfsc", "pkg"] }],
    });
    return typeof r === "string" ? r : null;
  },
  reveal: async (path: string): Promise<void> => {
    if (!isTauri) return;
    const { revealItemInDir } = await import("@tauri-apps/plugin-opener");
    await revealItemInDir(path);
  },

  openUrl: async (url: string): Promise<void> => {
    if (!isTauri) {
      window.open(url, "_blank", "noopener");
      return;
    }
    const { openUrl } = await import("@tauri-apps/plugin-opener");
    await openUrl(url);
  },
  onDrop: async (cb: (paths: string[], state: "over" | "leave" | "drop") => void): Promise<Unlisten> => {
    if (!isTauri) return () => {};
    const { getCurrentWebview } = await import("@tauri-apps/api/webview");
    return getCurrentWebview().onDragDropEvent((e) => {
      const p = e.payload;
      if (p.type === "over" || p.type === "enter") cb([], "over");
      else if (p.type === "leave") cb([], "leave");
      else if (p.type === "drop") cb(p.paths, "drop");
    });
  },
};

export const windowControls = {
  async minimize() {
    if (!isTauri) return;
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    await getCurrentWindow().minimize();
  },
  async toggleMaximize() {
    if (!isTauri) return;
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    await getCurrentWindow().toggleMaximize();
  },
  async close() {
    if (!isTauri) return;
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    await getCurrentWindow().close();
  },
  async isMaximized(): Promise<boolean> {
    if (!isTauri) return false;
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    return getCurrentWindow().isMaximized();
  },
  async onResized(cb: () => void): Promise<Unlisten> {
    if (!isTauri) return () => {};
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    return getCurrentWindow().onResized(cb);
  },
};
