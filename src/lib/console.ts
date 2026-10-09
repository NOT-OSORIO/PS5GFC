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

import { currentLang, locale } from "./i18n";
import type { Snapshot } from "./types";

export interface RemoteEntry {
  name: string;
  is_dir: boolean;
  size: number;

  mode: number;

  modified: string;
}

export interface StorageVolume {
  label: string;
  kind: string;
  index: number;
  path: string;
  total: number;
  free: number;
}

export interface ConsoleSystem {
  platform: string;
  firmware: string;
  model: string;
  hostname: string;
  ip_address: string;
  user_name: string;
}

export type Protocol = "prospero" | "ftp";

export const DEFAULT_PORT: Record<Protocol, number> = { prospero: 7070, ftp: 2121 };

export interface ConsoleCaps {

  zip: boolean;
  chmod: boolean;

  storage: boolean;

  server_copy: boolean;
}

export interface ConnectTarget {
  protocol: Protocol;
  host: string;
  port: number;
  user: string;
  pass: string;
}

export interface ConsoleInfo {
  host: string;
  port: number;
  protocol: Protocol;
  caps: ConsoleCaps;
  name: string;
  version: string;
  privileged: boolean;
  uptime_seconds: number;
  instance_id: string;
  system: ConsoleSystem;
  volumes: StorageVolume[];
}

export interface FoundConsole {
  host: string;
  port: number;
  protocol: Protocol;
  name: string;
  version: string;
  model: string;
}

export interface ConsoleJob {
  id: number;
  type: string;
  state: string;
  source: string;
  destination: string;
  current: string;
  current_index: number;
  total_items: number;
  completed: number;
  total: number;
  error: string;
  error_code: string;
  conflict_policy: string;
}

export type TransferKind = "upload" | "download";
export type TransferStatus = "queued" | "running" | "done" | "failed" | "cancelled";

export interface TransferView {
  id: number;
  kind: TransferKind;
  items: string[];
  dest: string;
  status: TransferStatus;
  error: string | null;
  created_ms: number;
  started_ms: number | null;
  finished_ms: number | null;
  snapshot: Snapshot | null;
  result: { files: number; dirs: number; bytes: number; skipped: number } | null;
}

export type Policy = "cancel" | "skip" | "replace" | "keep_both";

export interface ApiError {

  kind: string;
  message: string;
}

export function asApiError(e: unknown): ApiError {
  if (e && typeof e === "object" && "message" in e) {
    const o = e as { kind?: unknown; message?: unknown };
    return { kind: typeof o.kind === "string" ? o.kind : "other", message: String(o.message) };
  }
  return { kind: "other", message: String(e) };
}

export interface ConsoleSettings {
  host: string;

  port: number;

  protocol: Protocol;

  ftpPort: number;

  auto: boolean;

  live: boolean;

  lastDir: string;

  destDir: string;

  favorites: string[];

  hidden: boolean;
}

export const DEFAULT_CONSOLE_DIR = "/data/homebrew";

export const DEFAULT_CONSOLE_SETTINGS: ConsoleSettings = {
  host: "",
  port: 7070,
  protocol: "prospero",
  ftpPort: 2121,
  auto: true,
  live: true,
  lastDir: "/data",
  destDir: DEFAULT_CONSOLE_DIR,
  favorites: [],
  hidden: true,
};

export const px = {
  norm(p: string): string {
    const parts: string[] = [];
    for (const c of p.split("/")) {
      if (c === "" || c === ".") continue;
      if (c === "..") parts.pop();
      else parts.push(c);
    }
    return "/" + parts.join("/");
  },
  join(base: string, ...rest: string[]): string {
    return px.norm([base, ...rest].join("/"));
  },
  parent(p: string): string {
    const n = px.norm(p);
    const i = n.lastIndexOf("/");
    return i <= 0 ? "/" : n.slice(0, i);
  },
  base(p: string): string {
    const n = px.norm(p);
    return n === "/" ? "" : n.slice(n.lastIndexOf("/") + 1);
  },
  parts(p: string): string[] {
    return px.norm(p).split("/").filter(Boolean);
  },
  isWithin(p: string, root: string): boolean {
    const [a, b] = [px.norm(p), px.norm(root)];
    return b === "/" || a === b || a.startsWith(b + "/");
  },

  validName(n: string): boolean {
    return n.length > 0 && n.length <= 255 && n !== "." && n !== ".." && !n.includes("/") && !n.includes("\0");
  },

  splitExt(name: string): [string, string] {
    const i = name.lastIndexOf(".");
    return i > 0 ? [name.slice(0, i), name.slice(i)] : [name, ""];
  },
};

export function parseModified(s: string): Date | null {
  const m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(s);
  if (!m) return null;
  const d = new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
  return Number.isNaN(d.getTime()) || +m[1] < 1980 ? null : d;
}

export function formatModified(s: string): string {
  const d = parseModified(s);
  if (!d) return "—";
  return d.toLocaleString(locale(currentLang()), { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

export function octal(mode: number): string {
  return (mode & 0o7777).toString(8).padStart(3, "0");
}

export function modeString(mode: number): string {
  const b = "rwxrwxrwx";
  let out = "";
  for (let i = 0; i < 9; i++) out += mode & (1 << (8 - i)) ? b[i] : "-";
  return out;
}

export type FileKind = "folder" | "exfat" | "ffpkg" | "ffpfsc" | "ffpfs" | "archive" | "exec" | "pkg" | "image" | "audio" | "video" | "text" | "config" | "file";

const EXT_KIND: Record<string, FileKind> = {
  exfat: "exfat",
  ffpkg: "ffpkg",
  ffpfsc: "ffpfsc",
  ffpfs: "ffpfs",
  zip: "archive",
  rar: "archive",
  "7z": "archive",
  tar: "archive",
  gz: "archive",
  elf: "exec",
  self: "exec",
  prx: "exec",
  sprx: "exec",
  bin: "exec",
  pkg: "pkg",
  png: "image",
  jpg: "image",
  jpeg: "image",
  gif: "image",
  bmp: "image",
  webp: "image",
  mp3: "audio",
  ogg: "audio",
  wav: "audio",
  at9: "audio",
  mp4: "video",
  mkv: "video",
  webm: "video",
  txt: "text",
  log: "text",
  md: "text",
  json: "config",
  ini: "config",
  cfg: "config",
  conf: "config",
  xml: "config",
  toml: "config",
  yml: "config",
  yaml: "config",
  lua: "config",
  js: "config",
  sh: "config",
};

export function fileKind(e: Pick<RemoteEntry, "name" | "is_dir">): FileKind {
  if (e.is_dir) return "folder";
  const i = e.name.lastIndexOf(".");
  if (i < 0) return "file";
  return EXT_KIND[e.name.slice(i + 1).toLowerCase()] ?? "file";
}

export function isEditable(e: RemoteEntry): boolean {
  const k = fileKind(e);
  return !e.is_dir && (k === "text" || k === "config" || (k === "file" && !e.name.includes("."))) && e.size <= 4 * 1024 * 1024;
}

export function isZip(name: string): boolean {
  return /\.(zip|rar)$/i.test(name);
}

export function isLeftover(name: string): boolean {
  return /\.(pmgr-(part|upload)|ps5gfc-part)-/.test(name);
}

export function targetOf(c: ConsoleSettings): ConnectTarget {
  // The PS5's FTP server (ftpsrv) and Prospero Manager both accept anonymous connections; the backend
  // falls back to "anonymous" automatically when no user is given (see remote::Client::open).
  return { protocol: c.protocol, host: c.host, port: c.protocol === "ftp" ? c.ftpPort : c.port, user: "", pass: "" };
}

export function isHidden(name: string): boolean {
  return name.startsWith(".");
}

export function isSystemPath(p: string): boolean {
  const parts = px.parts(p);
  if (parts.length === 0) return true;
  if (parts.length === 1) return !["data", "user", "mnt"].includes(parts[0]);
  return ["system", "system_ex", "system_data", "preinst", "preinst2", "dev", "devbin", "boot"].includes(parts[0]);
}

export type SortKey = "name" | "size" | "modified" | "mode";
export interface SortSpec {
  key: SortKey;
  dir: 1 | -1;
}

export function sortEntries(list: RemoteEntry[], s: SortSpec): RemoteEntry[] {
  const coll = new Intl.Collator(locale(currentLang()), { numeric: true, sensitivity: "base" });
  const cmp = (a: RemoteEntry, b: RemoteEntry): number => {
    if (a.is_dir !== b.is_dir) return a.is_dir ? -1 : 1;
    let r = 0;
    switch (s.key) {
      case "name":
        r = coll.compare(a.name, b.name);
        break;
      case "size":
        r = a.size - b.size;
        break;
      case "modified":
        r = a.modified.localeCompare(b.modified);
        break;
      case "mode":
        r = a.mode - b.mode;
        break;
    }
    return r !== 0 ? r * s.dir : coll.compare(a.name, b.name);
  };
  return [...list].sort(cmp);
}

export function usedPct(v: StorageVolume): number {
  return v.total > 0 ? Math.min(100, ((v.total - v.free) / v.total) * 100) : 0;
}
