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

import { useCallback } from "react";
import { create } from "zustand";
import { toast } from "sonner";
import { api } from "./api";
import { DEFAULT_CONSOLE_SETTINGS, px, targetOf, type ConsoleSettings } from "./console";
import { displayName, sameFormat, targetKey } from "./formats";
import { outputBase, outputFileName } from "./naming";
import { isLang, setCurrentLang, tr, translate, type Key, type Lang } from "./i18n";
import { effective, emptyProfile, profileOf, resolveTarget } from "./options";
import type {
  AppInfo,
  Format,
  JobView,
  LogEntry,
  NameMode,
  Notice,
  NotifyPrefs,
  Profile,
  SectionId,
  Settings,
  Snapshot,
  SourceInfo,
  Stage,
} from "./types";
import { baseName, bytes, duration, sizeDelta } from "./utils";

export type Page = "convert" | "queue" | "console" | "about";

export interface Reading {
  path: string;
  stage: Stage;
  files: number;
  total: number;
  current: string;
  elapsedMs: number;
}

const DEFAULT_LANG: Lang = "en";

const NAME_MODES: NameMode[] = ["ppsa", "ppsa_title", "ppsa_title_version"];
function isNameMode(v: unknown): v is NameMode {
  return typeof v === "string" && (NAME_MODES as string[]).includes(v);
}

const DEFAULT_NOTIFY: NotifyPrefs = { system: true, toast: true, openFolder: false };

export const ZOOM_MIN = 0.7;
export const ZOOM_MAX = 1.6;
export const ZOOM_STEP = 0.1;
export const ZOOM_DEFAULT = 1;
export function clampZoom(v: number): number {
  const snapped = Math.round(v / ZOOM_STEP) * ZOOM_STEP;
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Number(snapped.toFixed(2))));
}

function normalizeSettings(raw: unknown): Settings {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const notify = { ...DEFAULT_NOTIFY, ...((r.notify as Partial<NotifyPrefs>) ?? {}) };
  const uiZoom = typeof r.uiZoom === "number" && Number.isFinite(r.uiZoom) ? clampZoom(r.uiZoom) : ZOOM_DEFAULT;
  const profiles: Settings["profiles"] = {};
  if (r.profiles && typeof r.profiles === "object") {
    for (const [k, v] of Object.entries(r.profiles as Record<string, unknown>)) {
      const p = v as Partial<{ std: Record<SectionId, boolean>; custom: object }>;

      const key = k.startsWith("ffpfsc-") ? "ffpfsc" : k;
      if (key !== k && profiles[key]) continue;
      profiles[key] = { std: { ...emptyProfile().std, ...(p.std ?? {}) }, custom: (p.custom as never) ?? {} };
    }
  }
  return {
    theme: r.theme === "light" ? "light" : "dark",
    lang: isLang(r.lang) ? r.lang : DEFAULT_LANG,
    notify,
    uiZoom,
    nameMode: isNameMode(r.nameMode) ? r.nameMode : "ppsa_title",
    profiles,
    console: normalizeConsole(r.console),
  };
}

function normalizeConsole(raw: unknown): ConsoleSettings {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const d = DEFAULT_CONSOLE_SETTINGS;
  const str = (v: unknown, def: string) => (typeof v === "string" && v.trim() ? v : def);
  const okPort = (v: unknown, def: number) => (typeof v === "number" && v > 0 && v < 65536 ? Math.floor(v) : def);
  return {
    host: typeof r.host === "string" ? r.host.trim() : d.host,
    port: okPort(r.port, d.port),
    protocol: r.protocol === "ftp" ? "ftp" : "prospero",
    ftpPort: okPort(r.ftpPort, d.ftpPort),
    auto: typeof r.auto === "boolean" ? r.auto : d.auto,
    live: typeof r.live === "boolean" ? r.live : d.live,
    lastDir: px.norm(str(r.lastDir, d.lastDir)),

    destDir: px.norm(r.destDir === "/data/etaHEN/games" ? d.destDir : str(r.destDir, d.destDir)),
    favorites: Array.isArray(r.favorites) ? r.favorites.filter((x): x is string => typeof x === "string").map(px.norm).slice(0, 30) : d.favorites,
    hidden: typeof r.hidden === "boolean" ? r.hidden : d.hidden,
  };
}

const NOTICE_KEY = "ps5gfc.notices";

function loadNotices(): Notice[] {
  try {
    const v = JSON.parse(localStorage.getItem(NOTICE_KEY) ?? "[]");
    return Array.isArray(v) ? (v as Notice[]).slice(0, 50) : [];
  } catch {
    return [];
  }
}
function saveNotices(list: Notice[]) {
  try {
    localStorage.setItem(NOTICE_KEY, JSON.stringify(list.slice(0, 50)));
  } catch {

  }
}

interface State {
  page: Page;
  setPage: (p: Page) => void;

  app: AppInfo | null;
  settings: Settings;
  defaultOutDir: string | null;
  hydrated: boolean;

  ready: boolean;
  hydrate: () => Promise<void>;
  setLang: (l: Lang) => void;
  setTheme: (t: "dark" | "light") => void;
  setUiZoom: (z: number) => void;
  setNameMode: (m: NameMode) => void;
  patchNotify: (p: Partial<NotifyPrefs>) => void;
  patchConsole: (p: Partial<ConsoleSettings>) => void;

  dropHandler: ((paths: string[]) => void) | null;
  setDropHandler: (h: ((paths: string[]) => void) | null) => void;
  addNotice: (n: Omit<Notice, "id" | "ts" | "read">) => void;

  dragging: boolean;
  source: SourceInfo | null;
  reading: Reading | null;
  loadSource: (path: string) => Promise<void>;
  cancelReading: () => void;
  clearSource: () => void;

  target: Format | null;
  setTarget: (f: Format | null) => void;
  setStd: (sec: SectionId, on: boolean) => void;
  patchCustom: (sec: SectionId, p: Profile["custom"]) => void;

  queueRunning: boolean;
  startQueue: () => Promise<void>;
  pauseQueue: () => Promise<void>;
  jobs: JobView[];
  progress: Record<number, Snapshot>;
  logs: Record<number, LogEntry[]>;
  loadLogs: (id: number) => Promise<void>;
  startJob: (runNow: boolean) => Promise<void>;

  notices: Notice[];
  markAllRead: () => void;
  clearNotices: () => void;
}

let saveTimer: number | undefined;
let noticeSeq = Date.now();

export const useStore = create<State>((set, get) => {
  function persist() {
    window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => void api.settingsSet(get().settings), 300);
  }

  function pushNotice(n: Omit<Notice, "id" | "ts" | "read">) {
    const notice: Notice = { ...n, id: ++noticeSeq, ts: Date.now(), read: false };
    const list = [notice, ...get().notices].slice(0, 50);
    saveNotices(list);
    set({ notices: list });
  }

  function onJobTransition(j: JobView) {
    const name = j.label ?? baseName(j.source);
    const prefs = get().settings.notify;
    if (j.status === "done" && j.report) {
      const r = j.report;
      const d = sizeDelta(r.in_bytes, r.out_bytes);
      const detail = `${displayName(j.target, tr("fmt.folder"))} · ${bytes(r.out_bytes)}${d ? ` (${d.text})` : ""} · ${duration(r.elapsed_ms / 1000)}`;
      pushNotice({ kind: "done", name, detail, path: r.output, console: r.console });
      if (prefs.toast) toast.success(tr("nt.done", { name }), { description: detail });
      if (prefs.system) tryNotify(tr("nt.done", { name }));
      if (prefs.openFolder && !r.console) void api.reveal(r.output);
    } else if (j.status === "failed") {
      pushNotice({ kind: "failed", name, detail: j.error ?? "" });
      toast.error(tr("nt.failed", { name }), { description: j.error ?? undefined });
    } else if (j.status === "cancelled") {
      pushNotice({ kind: "cancelled", name, detail: "" });
    }
  }

  return {
    page: "convert",
    setPage: (page) => set({ page }),

    app: null,
    settings: normalizeSettings(null),
    defaultOutDir: null,
    hydrated: false,
    ready: false,

    async hydrate() {
      if (get().hydrated) return;
      set({ hydrated: true });
      const [app, saved, outDir, jobs] = await Promise.all([api.appInfo(), api.settingsGet(), api.defaultOutputDir(), api.listJobs()]);
      const settings = normalizeSettings(saved);
      document.documentElement.dataset.theme = settings.theme;
      document.documentElement.lang = settings.lang;
      setCurrentLang(settings.lang);
      void api.setLanguage(settings.lang);
      void api.setZoom(settings.uiZoom);
      set({ app, settings, jobs, defaultOutDir: outDir, notices: loadNotices(), ready: true });

      set({ queueRunning: await api.queueRunning() });
      await api.onQueueState((running) => set({ queueRunning: running }));
      await api.onJobsChanged((list) => {
        const prev = get().jobs;
        set({ jobs: list });
        for (const j of list) {
          const before = prev.find((p) => p.id === j.id);
          if (before && before.status !== j.status) onJobTransition(j);
        }
      });
      await api.onProgress(({ id, snapshot }) => {
        set((s) => ({ progress: { ...s.progress, [id]: snapshot } }));
      });
      await api.onLog(({ id, entries }) => {
        set((s) => {
          const cur = s.logs[id] ?? [];
          const merged = cur.concat(entries);
          return { logs: { ...s.logs, [id]: merged.length > 2500 ? merged.slice(merged.length - 2500) : merged } };
        });
      });
      await api.onInspectProgress((p) => {
        set((s) =>
          s.reading ? { reading: { ...s.reading, stage: p.stage, files: p.files, total: p.total, current: p.current, elapsedMs: p.elapsed_ms } } : s,
        );
      });
      await api.onDrop((paths, state) => {
        if (state === "over") set({ dragging: true });
        else if (state === "leave") set({ dragging: false });
        else {
          set({ dragging: false });
          if (paths.length) {
            const h = get().dropHandler;
            if (h) h(paths);
            else {
              set({ page: "convert" });
              void get().loadSource(paths[0]);
            }
          }
        }
      });
      const initial = await api.initialPath();
      if (initial) void get().loadSource(initial);
    },

    setLang(lang) {
      setCurrentLang(lang);
      document.documentElement.lang = lang;
      void api.setLanguage(lang);
      set((s) => ({ settings: { ...s.settings, lang } }));
      persist();
    },
    setTheme(theme) {
      document.documentElement.dataset.theme = theme;
      set((s) => ({ settings: { ...s.settings, theme } }));
      persist();
    },
    setUiZoom(z) {
      const uiZoom = clampZoom(z);
      void api.setZoom(uiZoom);
      set((s) => ({ settings: { ...s.settings, uiZoom } }));
      persist();
    },
    setNameMode(nameMode) {
      set((s) => ({ settings: { ...s.settings, nameMode } }));
      persist();
    },
    patchNotify(p) {
      set((s) => ({ settings: { ...s.settings, notify: { ...s.settings.notify, ...p } } }));
      persist();
    },
    patchConsole(p) {
      set((s) => ({ settings: { ...s.settings, console: { ...s.settings.console, ...p } } }));
      persist();
    },
    dropHandler: null,
    setDropHandler: (h) => set({ dropHandler: h }),
    addNotice: (n) => pushNotice(n),

    dragging: false,
    source: null,
    reading: null,

    async loadSource(path) {
      if (get().reading) return;
      set({ reading: { path, stage: "scanning", files: 0, total: 0, current: "", elapsedMs: 0 } });
      try {
        const info = await api.inspect(path);
        const prev = get().target;
        const t = prev ? resolveTarget(prev, info, get().settings) : null;
        const ti = t ? info.targets.find((x) => sameFormat(x.format, t)) : undefined;
        const keep = ti && !ti.issues.some((i) => i.severity === "error");
        set({ source: info, reading: null, target: keep ? t : null });
      } catch (e) {
        set({ reading: null });
        if (String(e) !== "cancelled") toast.error(tr("read.failed"), { description: String(e) });
      }
    },
    cancelReading() {
      void api.cancelInspect();
    },
    clearSource() {
      set({ source: null, target: null });
    },

    target: null,
    setTarget: (f) => set((s) => ({ target: f ? resolveTarget(f, s.source, s.settings) : null })),
    setStd(sec, on) {
      const { target, settings } = get();
      if (!target) return;
      const key = targetKey(target);
      const p = profileOf(settings, target);
      const next = { ...settings, profiles: { ...settings.profiles, [key]: { ...p, std: { ...p.std, [sec]: on } } } };

      set((s) => ({ settings: next, target: resolveTarget(target, s.source, next) }));
      persist();
    },
    patchCustom(sec, patch) {
      const { target, settings } = get();
      if (!target) return;
      const key = targetKey(target);
      const p = profileOf(settings, target);
      const next = { ...settings, profiles: { ...settings.profiles, [key]: { std: { ...p.std, [sec]: false }, custom: { ...p.custom, ...patch } } } };
      set((s) => ({ settings: next, target: resolveTarget(target, s.source, next) }));
      persist();
    },

    queueRunning: false,
    async startQueue() {
      set({ queueRunning: true });
      await api.startQueue();
    },
    async pauseQueue() {
      set({ queueRunning: false });
      await api.pauseQueue();
    },
    jobs: [],
    progress: {},
    logs: {},
    async loadLogs(id) {
      const logs = await api.jobLogs(id);
      set((s) => ({ logs: { ...s.logs, [id]: logs } }));
    },

    async startJob(runNow) {
      const { source, target, settings, defaultOutDir } = get();
      if (!source || !target) return;
      const eff = effective(settings, target, defaultOutDir);
      if (!eff.outputDir) return;
      const label = source.title.title_name ?? source.title.title_id ?? baseName(source.path);
      const outName = outputBase(source, eff.options.name_mode);
      const where = targetOf(settings.console);
      if (eff.toConsole && !where.host) return;
      await api.enqueue({
        source: source.path,
        target,
        dest: eff.outputDir,
        options: { ...eff.options, out_name: outName },
        label,
        icon: source.icon,
        console: eff.toConsole ? where : null,
      });
      toast.success(tr("act.queued"), { description: `${label} → ${outputFileName(source, eff.options.name_mode, target)}` });

      if (runNow) {
        await get().startQueue();
        set({ page: "queue" });
      }
    },

    notices: [],
    markAllRead() {
      const list = get().notices.map((n) => ({ ...n, read: true }));
      saveNotices(list);
      set({ notices: list });
    },
    clearNotices() {
      saveNotices([]);
      set({ notices: [] });
    },
  };
});

function tryNotify(body: string) {
  try {
    if (typeof Notification === "undefined") return;
    if (Notification.permission === "granted") new Notification("PS5GFC", { body });
    else if (Notification.permission !== "denied") void Notification.requestPermission();
  } catch {

  }
}

export function useT() {
  const lang = useStore((s) => s.settings.lang);
  return useCallback((key: Key, vars?: Record<string, string | number>) => translate(lang, key, vars), [lang]);
}
