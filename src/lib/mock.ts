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

import type { AppInfo, Format, JobSpec, JobView, LogEntry, PathInfo, ReadProgress, Snapshot, SourceInfo, TargetInfo } from "./types";

const GIB = 1024 ** 3;

const icon =
  "data:image/svg+xml;utf8," +
  encodeURIComponent(
    `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 200'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#3b82f6'/><stop offset='1' stop-color='#9333ea'/></linearGradient></defs><rect width='200' height='200' fill='url(#g)'/><circle cx='100' cy='92' r='46' fill='none' stroke='white' stroke-width='10' opacity='.9'/><path d='M62 150 L100 112 L138 150' stroke='white' stroke-width='10' fill='none' opacity='.9'/></svg>`,
  );

export async function appInfo(): Promise<AppInfo> {
  return { version: "0.1.0", logical_cpus: 16, default_threads: 15, ram_bytes: 15.7 * GIB, data_dir: "C:\\Users\\user\\AppData\\Roaming\\com.ps5gfc.converter" };
}

export async function pathInfo(p: string): Promise<PathInfo> {
  const name = p.split(/[\\/]/).pop() ?? p;
  return { exists: true, is_dir: !/\.[a-z0-9]+$/i.test(name), name };
}

const FORMATS: Format[] = [
  { kind: "folder" },
  { kind: "image", inner: "exfat" },
  { kind: "image", inner: "ufs2" },
  { kind: "ffpfsc", inner: "exfat" },
  { kind: "ffpfsc", inner: "ufs2" },
  { kind: "ffpfsc", inner: "pfs" },
  { kind: "pkg" },
];

const readListeners = new Set<(p: ReadProgress) => void>();
let readCancelled = false;

export async function inspect(path: string): Promise<SourceInfo> {
  readCancelled = false;
  const total = 18432;
  for (let i = 1; i <= 20; i++) {
    await new Promise((r) => setTimeout(r, 120));
    if (readCancelled) throw "cancelled";
    readListeners.forEach((cb) => cb({ stage: i < 17 ? "scanning" : "planning", files: Math.floor((total * i) / 20), total: 0, current: `data/paks/pack_${i}.pak`, elapsed_ms: i * 120 }));
  }
  const isFolder = !/\.[a-z0-9]+$/i.test(path);
  const format: Format = isFolder ? { kind: "folder" } : { kind: "ffpfsc", inner: "exfat" };
  const targets: TargetInfo[] = FORMATS.map((f, i) => ({
    format: f,
    route: i === 0 ? "extract" : !isFolder && "inner" in f && f.inner === "exfat" ? "reuse_image" : "build",

    issues: f.kind === format.kind && (f.kind === "folder" || f.kind === "ffpfsc") ? [{ severity: "error", code: "same_format", args: {}, msg: "" }] : [],
  }));
  return {
    path,
    format,
    files: total,
    dirs: 1207,
    bytes: 61.4 * GIB,
    title: { title_id: "PPSA01411", content_id: "IV0000-PPSA01411_00-ASTROFORGE00000", title_name: "Astro Forge: Ember Trials", content_version: "01.004.000", has_eboot: true, ampr: true, ampr_packs: false, playgo: true },
    icon,
    wrapper: isFolder ? null : { file_size: 33.8 * GIB, inner_name: "PPSA01411.exfat", inner_size: 63.1 * GIB },
    details: isFolder ? [] : [["cluster", "64 KiB"], ["sector", "512 B"], ["clusters", "1032419"], ["volume", "63,1 GiB"]],
    issues: [],
    targets,
  };
}
export function onInspectProgress(cb: (p: ReadProgress) => void) {
  readListeners.add(cb);
  return () => void readListeners.delete(cb);
}
export function cancelInspect() {
  readCancelled = true;
}

let nextId = 1;
const jobs: JobView[] = [];
const listenersJobs = new Set<(j: JobView[]) => void>();
const listenersProg = new Set<(p: { id: number; snapshot: Snapshot }) => void>();
const listenersLog = new Set<(p: { id: number; entries: LogEntry[] }) => void>();
let timer: number | null = null;
let started = false;
const queueListeners = new Set<(r: boolean) => void>();
const setStarted = (v: boolean) => {
  started = v;
  queueListeners.forEach((cb) => cb(v));
};
export const startQueue = () => setStarted(true);
export const pauseQueue = () => setStarted(false);
export const queueRunning = () => started;
export function onQueueState(cb: (r: boolean) => void) {
  queueListeners.add(cb);
  return () => void queueListeners.delete(cb);
}

const emitJobs = () => listenersJobs.forEach((cb) => cb(structuredClone(jobs)));

function tick() {
  const j = jobs.find((x) => x.status === "running");
  if (!j) {
    const q = started ? jobs.find((x) => x.status === "queued") : undefined;
    if (!q && started) setStarted(false);
    if (q) {
      q.status = "running";
      q.started_ms = Date.now();
      emitJobs();
      listenersLog.forEach((cb) => cb({ id: q.id, entries: [{ level: "info", msg: "Opening source…", t_ms: 0 }, { level: "info", msg: "Planning the exFAT layout…", t_ms: 120 }] }));
    }
    return;
  }
  const total = 61.4 * GIB;
  const s: Snapshot = j.snapshot ?? { stage: "processing", done: 0, total, out_bytes: 0, files_done: 0, files_total: 18432, current: "", elapsed_ms: 0, speed_bps: 0, eta_secs: null, ratio: null };
  const speed = (480 + Math.sin(s.elapsed_ms / 2500) * 140 + Math.random() * 60) * 1024 ** 2;
  s.done = Math.min(total, s.done + speed * 0.25);
  s.out_bytes = s.done * 0.54;
  s.elapsed_ms += 250;
  s.speed_bps = speed;
  s.eta_secs = (total - s.done) / speed;
  s.ratio = 0.54;
  s.files_done = Math.floor((s.done / total) * s.files_total);
  s.current = `data/paks/pack_${String(Math.floor(s.done / GIB)).padStart(2, "0")}.pak`;
  s.stage = s.done >= total ? "done" : "processing";
  j.snapshot = s;
  listenersProg.forEach((cb) => cb({ id: j.id, snapshot: structuredClone(s) }));
  if (s.done >= total) {
    j.status = "done";
    j.finished_ms = Date.now();
    j.report = { output: `${j.dest}\\PPSA01411.ffpfsc`, route: "build", source_format: { kind: "folder" }, target_format: j.target, in_bytes: total, out_bytes: total * 0.54, files: s.files_total, elapsed_ms: s.elapsed_ms, warnings: [] };
    emitJobs();
  }
}

function ensureTimer() {
  if (timer == null) timer = window.setInterval(tick, 250);
}

export async function enqueue(spec: JobSpec): Promise<number> {
  const id = nextId++;
  jobs.push({
    id, source: spec.source, target: spec.target, dest: spec.dest, label: spec.label, icon: spec.icon, status: "queued", report: null, error: null,
    created_ms: Date.now(), started_ms: null, finished_ms: null, snapshot: null,
    options: { threads: spec.options.threads || 15, level: spec.options.level, rebuild: spec.options.rebuild, verify: spec.options.verify },
  });
  ensureTimer();
  emitJobs();
  return id;
}
export async function cancel(id: number) {
  const j = jobs.find((x) => x.id === id);
  if (j && (j.status === "queued" || j.status === "running")) {
    j.status = "cancelled";
    j.error = "Cancelled by the user. The partial file was removed.";
    emitJobs();
  }
}
export async function remove(id: number) {
  const i = jobs.findIndex((x) => x.id === id);
  if (i >= 0) jobs.splice(i, 1);
  emitJobs();
}
export async function clearFinished() {
  for (let i = jobs.length - 1; i >= 0; i--) if (!["queued", "running"].includes(jobs[i].status)) jobs.splice(i, 1);
  emitJobs();
}
export async function listJobs() {
  return structuredClone(jobs);
}
export async function onJobsChanged(cb: (j: JobView[]) => void) {
  listenersJobs.add(cb);
  return () => void listenersJobs.delete(cb);
}
export async function onProgress(cb: (p: { id: number; snapshot: Snapshot }) => void) {
  listenersProg.add(cb);
  return () => void listenersProg.delete(cb);
}
export async function onLog(cb: (p: { id: number; entries: LogEntry[] }) => void) {
  listenersLog.add(cb);
  return () => void listenersLog.delete(cb);
}
export async function pick(folder: boolean): Promise<string | null> {
  return folder ? "D:\\Games\\PPSA01411-app" : "D:\\Games\\PPSA01411.ffpfsc";
}
