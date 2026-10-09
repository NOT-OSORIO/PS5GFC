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

import { px, type ConnectTarget, type ConsoleInfo, type ConsoleJob, type FoundConsole, type Policy, type RemoteEntry, type StorageVolume, type TransferStatus, type TransferView } from "./console";
import type { Snapshot } from "./types";

const GIB = 1024 ** 3;
const MIB = 1024 ** 2;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

interface Node {
  dir: boolean;
  size: number;
  mode: number;
  modified: string;
  text?: string;
}

const fs = new Map<string, Node>();
let connected = false;

function stamp(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function put(path: string, n: Partial<Node> & { dir?: boolean } = {}) {
  const parts = px.parts(path);
  for (let i = 1; i < parts.length; i++) {
    const p = "/" + parts.slice(0, i).join("/");
    if (!fs.has(p)) fs.set(p, { dir: true, size: 0, mode: 0o777, modified: "20260716212914" });
  }
  fs.set(px.norm(path), { dir: n.dir ?? false, size: n.size ?? 0, mode: n.mode ?? 0o777, modified: n.modified ?? "20261004120000", text: n.text });
}

function seed() {
  if (fs.size) return;
  for (const d of ["/data/homebrew", "/data/pldmgr", "/data/ProsperoMgr", "/data/shadowmount", "/data/etaHEN/plugins", "/user/home", "/user/app", "/mnt/usb0/homebrew", "/mnt/usb0/games", "/system", "/dev"]) put(d, { dir: true });
  put("/data/.kstuff_noautomount", { size: 0, mode: 0o666 });
  put("/data/etaHEN/config.ini", { size: 212, text: "[general]\nauto_load=1\nfan_threshold=70\n\n[ftp]\nenabled=0\n" });
  put("/data/ProsperoMgr/ProsperoMgr.elf", { size: 6.2 * MIB, mode: 0o755 });
  put("/data/homebrew/etaHEN.bin", { size: 3.1 * MIB });
  put("/data/etaHEN/games", { dir: true });
  put("/data/etaHEN/games/PPSA01411 Astro Forge", { dir: true });
  put("/data/etaHEN/games/PPSA01411 Astro Forge/eboot.bin", { size: 38 * MIB });
  put("/data/etaHEN/games/PPSA01411 Astro Forge/sce_sys/param.json", { size: 2200, text: '{\n  "titleId": "PPSA01411",\n  "contentVersion": "01.004.000"\n}\n' });
  put("/data/etaHEN/games/PPSA01411 Astro Forge/data/pack_00.pak", { size: 4.4 * GIB });
  put("/data/etaHEN/games/PPSA04567 Neon Drift.exfat", { size: 31.2 * GIB });
  put("/data/etaHEN/games/PPSA08111 Last Lantern.ffpfsc", { size: 18.4 * GIB });
  put("/data/etaHEN/games/PPSA09922 Hollow Court.ffpkg", { size: 52.9 * GIB });
  put("/data/etaHEN/games/PPSA09922 Hollow Court.ffpkg.pmgr-part-g18db8", { size: 1.2 * GIB });
  put("/data/etaHEN/games/backup.zip", { size: 812 * MIB });
  put("/data/etaHEN/games/leia-me.txt", { size: 420, text: "Jogos do PS5 ficam aqui.\n" });
  for (let i = 0; i < 14; i++) put(`/data/shadowmount/titulo_${String(i).padStart(2, "0")}.cfg`, { size: 300 + i * 17, text: `id=${i}\n` });
  put("/mnt/usb0/games/PPSA07777.exfat", { size: 44 * GIB });
}

function volumes(): StorageVolume[] {
  return [
    { label: "Internal data", kind: "internal", index: -1, path: "/data", total: 670692474880, free: 656524705792 - delta },
    { label: "User storage", kind: "user", index: -1, path: "/user", total: 670692474880, free: 656524705792 - delta },
    { label: "USB 0", kind: "usb", index: 0, path: "/mnt/usb0", total: 480078987264, free: 264609595392 },
  ];
}
let delta = 0;

const info = (req: ConnectTarget): ConsoleInfo => {
  const { host } = req;
  if (req.protocol === "ftp") {
    return {
      host,
      port: req.port,
      protocol: "ftp",
      caps: { zip: false, chmod: true, storage: false, server_copy: false },
      name: "FTP",
      version: "ftpsrv 0.9 ready",
      privileged: true,
      uptime_seconds: 0,
      instance_id: "",
      system: { platform: "", firmware: "", model: "", hostname: "", ip_address: host, user_name: "" },
      volumes: [],
    };
  }
  return {
    host,
    port: req.port,
    protocol: "prospero",
    caps: { zip: true, chmod: true, storage: true, server_copy: true },
    name: "Prospero Manager",
    version: "1.1",
    privileged: true,
    uptime_seconds: 3088,
    instance_id: "mock",
    system: { platform: "PlayStation 5", firmware: "13.60", model: "PS5 (Prospero)", hostname: "", ip_address: host, user_name: "user" },
    volumes: volumes(),
  };
};
let lastTarget: ConnectTarget = { protocol: "prospero", host: "192.168.1.50", port: 7070, user: "", pass: "" };

function netFail(): never {
  throw { kind: "unreachable", message: "Could not reach the console: connection refused" };
}
function must(): void {
  if (!connected) throw { kind: "not_connected", message: "No console connected." };
}

export async function connect(req: ConnectTarget): Promise<ConsoleInfo> {
  seed();
  await sleep(500);
  if (/fail|0\.0\.0\.0/.test(req.host)) netFail();
  connected = true;
  lastTarget = req;
  return info(req);
}
export async function disconnect() {
  connected = false;
}
export async function ping(): Promise<ConsoleInfo> {
  must();
  await sleep(40);
  return info(lastTarget);
}
export async function scan(): Promise<FoundConsole[]> {
  await sleep(900);
  return [
    { host: "192.168.1.50", port: 7070, protocol: "prospero", name: "Prospero Manager", version: "1.1", model: "PS5 (Prospero)" },
    { host: "192.168.1.50", port: 2121, protocol: "ftp", name: "FTP", version: "ftpsrv 0.9 ready", model: "" },
  ];
}
export async function storage() {
  must();
  return lastTarget.protocol === "ftp" ? [] : volumes();
}
export async function free(path: string): Promise<number | null> {
  must();
  const v = (lastTarget.protocol === "ftp" ? [] : volumes()).filter((x) => px.isWithin(path, x.path)).sort((a, b) => b.path.length - a.path.length)[0];
  return v ? v.free : null;
}

export async function list(path: string): Promise<RemoteEntry[]> {
  must();
  await sleep(60);
  const dir = px.norm(path);
  if (dir !== "/" && !fs.get(dir)?.dir) throw { kind: "not_found", message: "Not found on the console: could not open directory: No such file or directory" };
  const out: RemoteEntry[] = [];
  const prefix = dir === "/" ? "/" : dir + "/";
  for (const [p, n] of fs) {
    if (!p.startsWith(prefix) || p.length === prefix.length) continue;
    const rest = p.slice(prefix.length);
    if (rest.includes("/")) continue;
    out.push({ name: rest, is_dir: n.dir, size: n.size, mode: n.mode, modified: n.modified });
  }
  if (dir === "/") for (const d of ["data", "user", "mnt", "system", "dev"]) if (!out.some((e) => e.name === d)) out.push({ name: d, is_dir: true, size: 0, mode: 0o777, modified: "20260716212914" });
  return out;
}

async function busyCheck() {
  must();
  if (transfers.some((t) => t.status === "running")) throw { kind: "busy", message: "The console is busy with another file operation. Try again in a moment." };
}

export async function mkdir(path: string) {
  await busyCheck();
  await sleep(80);
  put(path, { dir: true, modified: stamp() });
}
export async function rename(from: string, to: string) {
  await busyCheck();
  await sleep(80);
  const [f, t] = [px.norm(from), px.norm(to)];
  if (fs.has(t)) throw { kind: "exists", message: "Already exists on the console: " + t };
  for (const [p, n] of [...fs]) {
    if (p === f || p.startsWith(f + "/")) {
      fs.delete(p);
      fs.set(t + p.slice(f.length), n);
    }
  }
}
export async function chmod(paths: string[], mode: number, recursive = false, dirs: string[] = []): Promise<number> {
  await busyCheck();
  let count = 0;
  for (const p of paths) {
    const norm = px.norm(p);
    const n = fs.get(norm);
    if (n) {
      n.mode = mode;
      count++;
    }
    if (recursive && dirs.includes(p)) {
      const prefix = norm === "/" ? "/" : `${norm}/`;
      for (const [path, entry] of fs.entries()) {
        if (path !== norm && path.startsWith(prefix)) {
          entry.mode = mode;
          count++;
        }
      }
    }
  }
  return count;
}

const jobList: ConsoleJob[] = [];
let jobSeq = 1;

function startJob(type: string, source: string, destination: string, work: () => void, size: number): number {
  const id = jobSeq++;
  const j: ConsoleJob = { id, type, state: "running", source, destination, current: "", current_index: 0, total_items: 1, completed: 0, total: size, error: "", error_code: "", conflict_policy: "cancel" };
  jobList.push(j);
  const steps = 6;
  let k = 0;
  const h = setInterval(() => {
    if (j.state === "canceled") return clearInterval(h);
    k++;
    j.completed = Math.round((size * k) / steps);
    if (k >= steps) {
      clearInterval(h);
      work();
      j.state = "done";
    }
  }, 260);
  return id;
}

function sizeOf(path: string): number {
  let t = 0;
  const p = px.norm(path);
  for (const [k, n] of fs) if ((k === p || k.startsWith(p + "/")) && !n.dir) t += n.size;
  return t;
}

export async function remove(paths: string[]): Promise<number[]> {
  await busyCheck();
  return paths.map((p) =>
    startJob("delete", p, p, () => {
      const n = px.norm(p);
      for (const k of [...fs.keys()]) if (k === n || k.startsWith(n + "/")) fs.delete(k);
    }, sizeOf(p)),
  );
}

export async function paste(sources: string[], destDir: string, mode: "copy" | "move", policy: Policy): Promise<number[]> {
  await busyCheck();
  return sources.map((s) => {
    let target = px.join(destDir, px.base(s));
    if (fs.has(target)) {
      if (policy === "keep_both") {
        const [stem, ext] = px.splitExt(px.base(s));
        for (let i = 1; fs.has(target); i++) target = px.join(destDir, `${stem} (${i})${ext}`);
      } else if (policy === "cancel") throw { kind: "exists", message: "Already exists on the console: " + target };
    }
    const src = px.norm(s);
    return startJob(mode, s, target, () => {
      for (const [k, n] of [...fs]) {
        if (k === src || k.startsWith(src + "/")) {
          fs.set(target + k.slice(src.length), { ...n, modified: stamp() });
          if (mode === "move") fs.delete(k);
        }
      }
    }, sizeOf(s));
  });
}

export async function conflicts(paths: string[]) {
  must();
  return paths.filter((p) => fs.has(px.norm(p)));
}
export async function jobs() {
  must();
  return structuredClone(jobList);
}
export async function cancelJob(id: number) {
  const j = jobList.find((x) => x.id === id);
  if (j && j.state === "running") j.state = "canceled";
}
export async function clearJobs() {
  for (let i = jobList.length - 1; i >= 0; i--) if (!["running", "queued"].includes(jobList[i].state)) jobList.splice(i, 1);
}
export async function dirSize(path: string) {
  must();
  await sleep(150);
  return sizeOf(path);
}
export async function zip(paths: string[], dir: string, name: string) {
  await busyCheck();
  const n = name.endsWith(".zip") ? name : name + ".zip";
  return startJob("zip", paths[0], px.join(dir, n), () => put(px.join(dir, n), { size: paths.reduce((a, p) => a + sizeOf(p), 0) * 0.6, modified: stamp() }), 50 * MIB);
}
export async function unzip(path: string, dest: string | null) {
  await busyCheck();
  const d = dest ?? px.join(px.parent(path), px.splitExt(px.base(path))[0]);
  return startJob("unzip", path, d, () => put(px.join(d, "conteudo.bin"), { size: 12 * MIB, modified: stamp() }), 30 * MIB);
}
export async function readText(path: string) {
  must();
  const n = fs.get(px.norm(path));
  if (!n || n.dir) throw { kind: "not_found", message: "Not found" };
  return { text: n.text ?? "", version: String(n.modified), newline: "lf", bom: false };
}
export async function writeText(path: string, text: string) {
  await busyCheck();
  const n = fs.get(px.norm(path));
  if (n) {
    n.text = text;
    n.size = text.length;
    n.modified = stamp();
  }
  return String(n?.modified);
}

const transfers: TransferView[] = [];
let tSeq = 1;
const lt = new Set<(l: TransferView[]) => void>();
const lp = new Set<(p: { id: number; snapshot: Snapshot }) => void>();
const emit = () => lt.forEach((cb) => cb(structuredClone(transfers)));

function runTransfer(t: TransferView, total: number, onDone: () => void) {
  const timers: { h?: number } = {};
  const tick = () => {
    if (t.status === "cancelled") return;
    const running = transfers.find((x) => x.status === "running");
    if (!running && t.status === "queued") {
      t.status = "running";
      t.started_ms = Date.now();
      emit();
    }
    if (t.status !== "running") {
      timers.h = window.setTimeout(tick, 200);
      return;
    }
    const s: Snapshot = t.snapshot ?? { stage: "processing", done: 0, total, out_bytes: 0, files_done: 0, files_total: Math.max(1, t.items.length * 12), current: "", elapsed_ms: 0, speed_bps: 0, eta_secs: null, ratio: null };
    const speed = (24 + Math.random() * 6) * MIB;
    s.done = Math.min(total, s.done + speed * 0.25);
    s.out_bytes = s.done;
    s.elapsed_ms += 250;
    s.speed_bps = speed;
    s.eta_secs = (total - s.done) / speed;
    s.files_done = Math.floor((s.done / total) * s.files_total);
    s.current = t.items[Math.min(t.items.length - 1, Math.floor((s.done / total) * t.items.length))] ?? "";
    t.snapshot = s;
    lp.forEach((cb) => cb({ id: t.id, snapshot: structuredClone(s) }));
    if (s.done >= total) {
      t.status = "done";
      t.finished_ms = Date.now();
      t.result = { files: s.files_total, dirs: 3, bytes: total, skipped: 0 };
      onDone();
      emit();
      return;
    }
    timers.h = window.setTimeout(tick, 250);
  };
  timers.h = window.setTimeout(tick, 200);
}

export async function transferUpload(paths: string[], destDir: string) {
  must();
  const total = Math.max(40 * MIB, paths.length * 60 * MIB);
  const t: TransferView = { id: tSeq++, kind: "upload", items: paths, dest: destDir, status: "queued" as TransferStatus, error: null, created_ms: Date.now(), started_ms: null, finished_ms: null, snapshot: null, result: null };
  transfers.push(t);
  runTransfer(t, total, () => {
    for (const p of paths) put(px.join(destDir, p.split(/[\\/]/).pop() ?? "arquivo"), { size: total / paths.length, modified: stamp() });
    delta += total;
  });
  emit();
  return t.id;
}
export async function transferDownload(paths: string[], localDir: string) {
  must();
  const total = Math.max(20 * MIB, paths.reduce((a, p) => a + Math.min(sizeOf(p), 300 * MIB), 0));
  const t: TransferView = { id: tSeq++, kind: "download", items: paths, dest: localDir, status: "queued", error: null, created_ms: Date.now(), started_ms: null, finished_ms: null, snapshot: null, result: null };
  transfers.push(t);
  runTransfer(t, total, () => {});
  emit();
  return t.id;
}
export async function transferCancel(id: number) {
  const t = transfers.find((x) => x.id === id);
  if (t && (t.status === "queued" || t.status === "running")) {
    t.status = "cancelled";
    t.error = "Cancelled by the user. The partial file was removed.";
    t.finished_ms = Date.now();
    emit();
  }
}
export async function transferRemove(id: number) {
  const i = transfers.findIndex((x) => x.id === id && x.status !== "running");
  if (i >= 0) transfers.splice(i, 1);
  emit();
}
export async function transferClear() {
  for (let i = transfers.length - 1; i >= 0; i--) if (!["queued", "running"].includes(transfers[i].status)) transfers.splice(i, 1);
  emit();
}
export async function transferList() {
  return structuredClone(transfers);
}
export async function onTransfers(cb: (l: TransferView[]) => void) {
  lt.add(cb);
  return () => void lt.delete(cb);
}
export async function onTransferProgress(cb: (p: { id: number; snapshot: Snapshot }) => void) {
  lp.add(cb);
  return () => void lp.delete(cb);
}

export async function pickFiles(): Promise<string[]> {
  return ["D:\\Jogos\\PPSA02002.ffpfsc", "D:\\Jogos\\leia-me.txt"];
}
export async function pickFolders(): Promise<string[]> {
  return ["D:\\Jogos\\PPSA01411-app"];
}
