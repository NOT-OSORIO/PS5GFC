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

import { toast } from "sonner";
import { create } from "zustand";
import { api } from "./api";
import {
  asApiError,
  isHidden,
  px,
  targetOf,
  type ConnectTarget,
  type ConsoleInfo,
  type ConsoleJob,
  type FoundConsole,
  type Policy,
  type RemoteEntry,
  type SortKey,
  type SortSpec,
  type TransferView,
} from "./console";
import { tr } from "./i18n";
import { useStore } from "./store";
import type { Snapshot } from "./types";
import { baseName } from "./utils";

export type Status = "disconnected" | "connecting" | "connected" | "lost";

export interface Clip {
  mode: "copy" | "cut";

  paths: string[];
}

export type Dialog =
  | { kind: "conflict"; names: string[]; what: "paste" | "upload" | "download"; resolve: (p: Policy | null) => void }
  | { kind: "confirm_delete"; paths: string[]; dirs: number; resolve: (ok: boolean) => void }
  | { kind: "name"; title: string; label: string; initial: string; confirm: string; taken: string[]; select: "stem" | "all"; resolve: (name: string | null) => void }
  | { kind: "properties"; entries: RemoteEntry[]; dir: string }
  | { kind: "zip"; names: string[] }
  | { kind: "unzip"; name: string }
  | { kind: "picker"; title: string; start: string; confirm: string; resolve: (path: string | null) => void }
  | { kind: "text"; path: string };

interface ConsoleState {

  status: Status;
  info: ConsoleInfo | null;
  connError: string | null;
  found: FoundConsole[];
  scanning: boolean;

  connect: (target?: ConnectTarget, silent?: boolean) => Promise<boolean>;
  disconnect: () => void;
  scan: () => Promise<void>;

  markLost: (message: string) => void;
  ping: () => Promise<void>;

  path: string;
  back: string[];
  fwd: string[];
  entries: RemoteEntry[];
  loading: boolean;
  listError: string | null;
  syncedAt: number;
  go: (path: string, how?: "push" | "replace" | "history") => Promise<boolean>;
  goUp: () => void;
  goBack: () => void;
  goForward: () => void;
  refresh: (silent?: boolean) => Promise<void>;

  openFolder: (path: string) => void;

  selected: string[];
  anchor: string | null;
  setSelected: (names: string[], anchor?: string | null) => void;
  sort: SortSpec;
  setSort: (key: SortKey) => void;
  filter: string;
  setFilter: (f: string) => void;

  clip: Clip | null;
  copy: (names?: string[]) => void;
  cut: (names?: string[]) => void;
  paste: () => Promise<void>;

  jobs: ConsoleJob[];
  pollJobs: () => Promise<void>;
  cancelJob: (id: number) => Promise<void>;
  clearJobs: () => Promise<void>;

  transfers: TransferView[];
  tProgress: Record<number, Snapshot>;
  tSpeed: Record<number, number[]>;
  upload: (paths: string[]) => Promise<void>;
  uploadPick: (kind: "files" | "folders") => Promise<void>;
  download: (names?: string[]) => Promise<void>;
  initEvents: () => Promise<void>;

  busy: number;
  newFolder: () => Promise<void>;
  rename: (name?: string) => Promise<void>;
  remove: (names?: string[]) => Promise<void>;
  chmod: (names: string[], mode: number, recursive?: boolean) => Promise<void>;
  zip: (names: string[], zipName: string, compression: string) => Promise<void>;
  unzip: (name: string, dest: string | null, policy: Policy, deleteSource: boolean, password: string | null) => Promise<void>;

  act: <T>(fn: () => Promise<T>) => Promise<T | undefined>;

  dialog: Dialog | null;
  openDialog: (d: Dialog) => void;
  closeDialog: () => void;
  ask: <T>(make: (resolve: (v: T) => void) => Dialog) => Promise<T>;
}

let listSeq = 0;
let initDone = false;
const notified = new Set<string>();

let jobBaseline: Set<number> | null = null;
const isFinished = (j: ConsoleJob) => ["done", "attention", "canceled", "error"].includes(j.state);

function connectionLost(kind: string): boolean {
  return kind === "unreachable" || kind === "not_connected";
}

function uniqueName(base: string, taken: string[]): string {
  if (!taken.includes(base)) return base;
  const [stem, ext] = px.splitExt(base);
  for (let i = 2; i < 1000; i++) {
    const n = `${stem} (${i})${ext}`;
    if (!taken.includes(n)) return n;
  }
  return base;
}

export const useConsole = create<ConsoleState>((set, get) => {
  const cfg = () => useStore.getState().settings.console;
  const patchCfg = (p: Parameters<ReturnType<typeof useStore.getState>["patchConsole"]>[0]) => useStore.getState().patchConsole(p);

  function fail(e: unknown, silent = false): string {
    const err = asApiError(e);
    if (err.kind === "cancelled") return err.message;
    if (connectionLost(err.kind) && get().status === "connected") get().markLost(err.message);
    else if (!silent) toast.error(err.message);
    return err.message;
  }

  return {
    status: "disconnected",
    info: null,
    connError: null,
    found: [],
    scanning: false,

    async connect(target, silent = false) {
      const c = cfg();
      const want = target ?? targetOf(c);
      if (!want.host.trim()) return false;
      if (get().status === "connecting") return false;
      const wasLost = get().status === "lost";
      if (!silent) set({ status: "connecting", connError: null });
      try {
        const info = await api.consoleConnect(want);
        jobBaseline = null;
        set({ status: "connected", info, connError: null });

        const remembered = want.protocol === "ftp" ? { host: info.host, protocol: "ftp" as const, ftpPort: info.port } : { host: info.host, protocol: "prospero" as const, port: info.port };
        if (Object.entries(remembered).some(([k, v]) => c[k as keyof typeof c] !== v)) patchCfg(remembered);
        if (!wasLost || get().entries.length === 0) {
          const start = get().path === "/" && c.lastDir ? c.lastDir : get().path;

          if (!(await get().go(start, "replace")) && get().status === "connected" && start !== "/") await get().go("/", "replace");
        } else void get().refresh(true);
        void get().pollJobs();
        return true;
      } catch (e) {
        const err = asApiError(e);
        if (get().status !== "lost") set({ status: "disconnected", connError: err.message });
        else set({ connError: err.message });
        return false;
      }
    },

    disconnect() {
      void api.consoleDisconnect();
      jobBaseline = null;
      set({ status: "disconnected", info: null, entries: [], selected: [], jobs: [], listError: null, back: [], fwd: [] });
    },

    async scan() {
      if (get().scanning) return;
      set({ scanning: true, found: [] });
      try {
        set({ found: await api.consoleScan() });
      } catch {
        set({ found: [] });
      } finally {
        set({ scanning: false });
      }
    },

    markLost(message) {
      if (get().status !== "connected") return;
      set({ status: "lost", connError: message });
    },

    async ping() {
      if (get().status !== "connected") return;
      try {
        set({ info: await api.consolePing() });
      } catch (e) {
        fail(e, true);
      }
    },

    path: "/",
    back: [],
    fwd: [],
    entries: [],
    loading: false,
    listError: null,
    syncedAt: 0,

    async go(path, how = "push") {
      const target = px.norm(path);
      const mine = ++listSeq;
      const same = target === get().path;
      if (!same) set({ loading: true, listError: null });
      try {
        const entries = await api.consoleList(target);
        if (mine !== listSeq) return false;
        const prev = get().path;
        set((s) => ({
          path: target,
          entries,
          loading: false,
          listError: null,
          syncedAt: Date.now(),
          selected: same ? s.selected.filter((n) => entries.some((e) => e.name === n)) : [],
          anchor: same ? s.anchor : null,
          filter: same ? s.filter : "",
          back: how === "push" && !same ? [...s.back, prev].slice(-60) : s.back,
          fwd: how === "push" && !same ? [] : s.fwd,
        }));
        if (!same && cfg().lastDir !== target) patchCfg({ lastDir: target });
        return true;
      } catch (e) {
        if (mine !== listSeq) return false;
        const err = asApiError(e);
        set({ loading: false, listError: err.message });
        if (connectionLost(err.kind)) get().markLost(err.message);
        return false;
      }
    },
    goUp() {
      const p = get().path;
      if (p !== "/") void get().go(px.parent(p));
    },
    goBack() {
      const { back, path } = get();
      if (!back.length) return;
      const to = back[back.length - 1];
      set((s) => ({ back: s.back.slice(0, -1), fwd: [path, ...s.fwd] }));
      void get().go(to, "history");
    },
    goForward() {
      const { fwd, path } = get();
      if (!fwd.length) return;
      const to = fwd[0];
      set((s) => ({ fwd: s.fwd.slice(1), back: [...s.back, path] }));
      void get().go(to, "history");
    },
    async refresh(silent = false) {
      if (get().status !== "connected") return;
      const path = get().path;
      const mine = ++listSeq;
      try {
        const entries = await api.consoleList(path);
        if (mine !== listSeq || path !== get().path) return;
        const old = get().entries;

        const same = old.length === entries.length && JSON.stringify(old) === JSON.stringify(entries);
        set((s) => ({
          entries: same ? old : entries,
          syncedAt: Date.now(),
          listError: null,
          loading: false,
          selected: same ? s.selected : s.selected.filter((n) => entries.some((e) => e.name === n)),
        }));
      } catch (e) {
        if (mine !== listSeq) return;
        const err = asApiError(e);
        if (connectionLost(err.kind)) get().markLost(err.message);
        else if (!silent) set({ listError: err.message });
      }
    },
    openFolder(path) {
      useStore.getState().setPage("console");
      const go = () => void get().go(path);
      if (get().status === "connected") go();
      else void get().connect().then((ok) => ok && go());
    },

    selected: [],
    anchor: null,
    setSelected: (names, anchor) => set((s) => ({ selected: names, anchor: anchor === undefined ? s.anchor : anchor })),
    sort: { key: "name", dir: 1 },
    setSort: (key) => set((s) => ({ sort: s.sort.key === key ? { key, dir: s.sort.dir === 1 ? -1 : 1 } : { key, dir: key === "name" ? 1 : -1 } })),
    filter: "",
    setFilter: (filter) => set({ filter }),

    clip: null,
    copy(names) {
      const sel = names ?? get().selected;
      if (!sel.length) return;
      set({ clip: { mode: "copy", paths: sel.map((n) => px.join(get().path, n)) } });
    },
    cut(names) {
      const sel = names ?? get().selected;
      if (!sel.length) return;
      set({ clip: { mode: "cut", paths: sel.map((n) => px.join(get().path, n)) } });
    },
    async paste() {
      const { clip, path } = get();
      if (!clip || !clip.paths.length) return;
      const sources = clip.paths.filter((p) => !(clip.mode === "cut" && px.parent(p) === path));
      if (!sources.length) {
        set({ clip: null });
        return;
      }

      if (sources.some((s) => px.isWithin(path, s))) {
        toast.error(tr("con.err_into_self"));
        return;
      }
      const targets = sources.map((s) => px.join(path, px.base(s)));
      let policy: Policy = "cancel";
      try {
        const existing = await api.consoleConflicts(targets);
        if (existing.length) {

          const selfCopy = clip.mode === "copy" && sources.every((s) => px.parent(s) === path);
          if (selfCopy) policy = "keep_both";
          else {
            const p = await get().ask<Policy | null>((resolve) => ({ kind: "conflict", names: existing.map(px.base), what: "paste", resolve }));
            if (!p) return;
            policy = p;
          }
        }
      } catch (e) {
        fail(e);
        return;
      }
      const ids = await get().act(() => api.consolePaste(sources, path, clip.mode === "cut" ? "move" : "copy", policy));
      if (ids) {
        if (clip.mode === "cut") set({ clip: null });
        toast(tr(clip.mode === "cut" ? "con.moving" : "con.copying", { n: sources.length }));
        void get().pollJobs();
      }
    },

    jobs: [],
    async pollJobs() {
      if (get().status !== "connected") return;
      try {
        const all = await api.consoleJobs();
        if (jobBaseline === null) jobBaseline = new Set(all.filter(isFinished).map((j) => j.id));
        const list = all.filter((j) => !jobBaseline!.has(j.id));
        const prev = get().jobs;
        set({ jobs: list });

        let changed = false;
        for (const j of list) {
          const before = prev.find((p) => p.id === j.id);
          const finished = isFinished(j);
          const wasActive = !before || !isFinished(before);
          const key = `job:${j.id}:${j.state}`;
          if (finished && wasActive && !notified.has(key)) {
            notified.add(key);
            changed = true;
            if (j.state === "error" || j.state === "attention") toast.error(tr("con.job_failed", { op: tr(jobKey(j.type)) }), { description: j.error || j.error_code || undefined });
          }
        }
        if (changed) void get().refresh(true);
      } catch (e) {
        fail(e, true);
      }
    },
    async cancelJob(id) {
      await get().act(() => api.consoleCancelJob(id));
      void get().pollJobs();
    },
    async clearJobs() {
      await get().act(() => api.consoleClearJobs());
      void get().pollJobs();
    },

    transfers: [],
    tProgress: {},
    tSpeed: {},

    async upload(paths) {
      if (!paths.length || get().status !== "connected") return;
      const dest = get().path;
      const names = paths.map(baseName);
      let policy: Policy = "cancel";
      try {
        const existing = await api.consoleConflicts(names.map((n) => px.join(dest, n)));
        if (existing.length) {
          const p = await get().ask<Policy | null>((resolve) => ({ kind: "conflict", names: existing.map(px.base), what: "upload", resolve }));
          if (!p) return;
          policy = p;
        }
        await api.transferUpload(paths, dest, policy);
        toast(tr("con.upload_queued", { n: paths.length }));
      } catch (e) {
        fail(e);
      }
    },
    async uploadPick(kind) {
      const paths = kind === "files" ? await api.pickFiles() : await api.pickFolders();
      if (paths.length) await get().upload(paths);
    },
    async download(names) {
      const sel = names ?? get().selected;
      if (!sel.length) return;
      const dir = await api.pickFolder(tr("con.download_to"));
      if (!dir) return;
      let policy: Policy = "cancel";
      try {
        const exists: string[] = [];
        for (const n of sel) {
          const info = await api.pathInfo(`${dir.replace(/[\\/]+$/, "")}\\${n}`);
          if (info.exists) exists.push(n);
        }
        if (exists.length) {
          const p = await get().ask<Policy | null>((resolve) => ({ kind: "conflict", names: exists, what: "download", resolve }));
          if (!p) return;
          policy = p;
        }
        await api.transferDownload(
          sel.map((n) => px.join(get().path, n)),
          dir,
          policy,
        );
        toast(tr("con.download_queued", { n: sel.length }));
      } catch (e) {
        fail(e);
      }
    },
    async initEvents() {
      if (initDone) return;
      initDone = true;
      set({ transfers: await api.transferList() });
      await api.onTransfers((list) => {
        const prev = get().transfers;
        set({ transfers: list });
        for (const t of list) {
          const before = prev.find((p) => p.id === t.id);
          if (before && before.status !== t.status && ["done", "failed", "cancelled"].includes(t.status)) onTransferEnd(t);
        }
      });
      await api.onTransferProgress(({ id, snapshot }) => {
        const hist = get().tSpeed[id] ?? [];
        const next = hist.length >= 80 ? hist.slice(hist.length - 79) : hist.slice();
        next.push(snapshot.speed_bps);
        set((s) => ({ tProgress: { ...s.tProgress, [id]: snapshot }, tSpeed: { ...s.tSpeed, [id]: next } }));
      });
    },

    busy: 0,
    async act(fn) {
      set((s) => ({ busy: s.busy + 1 }));
      try {
        return await fn();
      } catch (e) {
        fail(e);
        return undefined;
      } finally {
        set((s) => ({ busy: s.busy - 1 }));
      }
    },
    async newFolder() {
      const taken = get().entries.map((e) => e.name);
      const name = await get().ask<string | null>((resolve) => ({
        kind: "name",
        title: tr("con.new_folder"),
        label: tr("con.name"),
        initial: uniqueName(tr("con.new_folder_name"), taken),
        confirm: tr("con.create"),
        taken,
        select: "all",
        resolve,
      }));
      if (!name) return;
      const ok = await get().act(() => api.consoleMkdir(px.join(get().path, name)).then(() => true));
      if (ok) {
        await get().refresh(true);
        set({ selected: [name], anchor: name });
      }
    },
    async rename(name) {
      const old = name ?? get().selected[0];
      if (!old) return;
      const taken = get().entries.map((e) => e.name).filter((n) => n !== old);
      const next = await get().ask<string | null>((resolve) => ({
        kind: "name",
        title: tr("con.rename"),
        label: tr("con.name"),
        initial: old,
        confirm: tr("con.rename_do"),
        taken,
        select: get().entries.find((e) => e.name === old)?.is_dir ? "all" : "stem",
        resolve,
      }));
      if (!next || next === old) return;
      const ok = await get().act(() => api.consoleRename(px.join(get().path, old), px.join(get().path, next)).then(() => true));
      if (ok) {
        await get().refresh(true);
        set({ selected: [next], anchor: next });
      }
    },
    async remove(names) {
      const sel = names ?? get().selected;
      if (!sel.length) return;
      const paths = sel.map((n) => px.join(get().path, n));
      const dirs = sel.filter((n) => get().entries.find((e) => e.name === n)?.is_dir).length;
      const ok = await get().ask<boolean>((resolve) => ({ kind: "confirm_delete", paths, dirs, resolve }));
      if (!ok) return;
      const ids = await get().act(() => api.consoleDelete(paths));
      if (ids) {
        set({ selected: [] });
        void get().pollJobs();
        window.setTimeout(() => void get().refresh(true), 500);
      }
    },
    async chmod(names, mode, recursive = false) {
      const base = get().path;
      const paths = names.map((n) => px.join(base, n));
      const dirs = recursive ? names.filter((n) => get().entries.find((e) => e.name === n)?.is_dir).map((n) => px.join(base, n)) : [];
      const count = await get().act(() => api.consoleChmod(paths, mode, recursive, dirs));
      if (count !== undefined) {
        toast.success(recursive && count > paths.length ? tr("con.perm_done_n", { n: count }) : tr("con.perm_done"));
        void get().refresh(true);
      }
    },
    async zip(names, zipName, compression) {
      const id = await get().act(() => api.consoleZip(names.map((n) => px.join(get().path, n)), get().path, zipName, compression));
      if (id !== undefined) void get().pollJobs();
    },
    async unzip(name, dest, policy, deleteSource, password) {
      const id = await get().act(() => api.consoleUnzip(px.join(get().path, name), dest, policy, deleteSource, password));
      if (id !== undefined) void get().pollJobs();
    },

    dialog: null,
    openDialog: (d) => set({ dialog: d }),
    closeDialog: () => set({ dialog: null }),
    ask(make) {
      return new Promise((resolve) => {
        set({
          dialog: make((v) => {
            set({ dialog: null });
            resolve(v);
          }),
        });
      });
    },
  };
});

function jobKey(type: string) {
  switch (type) {
    case "copy":
      return "con.op_copy" as const;
    case "move":
      return "con.op_move" as const;
    case "delete":
      return "con.op_delete" as const;
    case "zip":
      return "con.op_zip" as const;
    case "unzip":
      return "con.op_unzip" as const;
    default:
      return "con.op_other" as const;
  }
}
export { jobKey };

function onTransferEnd(t: TransferView) {
  const st = useStore.getState();
  const label =
    t.items.length === 1 ? baseName(t.items[0]) : tr("con.n_items", { n: t.items.length });
  const name = t.kind === "upload" ? tr("con.t_upload", { name: label }) : tr("con.t_download", { name: label });
  if (t.status === "done") {
    const r = t.result;
    const detail = r ? `${tr("con.t_files", { n: r.files })}${r.skipped ? ` · ${tr("con.t_skipped", { n: r.skipped })}` : ""}` : "";
    if (st.settings.notify.toast) toast.success(tr("nt.done", { name }), { description: detail });
    st.addNotice({ kind: "done", name, detail, path: t.kind === "upload" ? px.join(t.dest, baseName(t.items[0] ?? "")) : t.dest, console: t.kind === "upload" });
  } else if (t.status === "failed") {
    toast.error(tr("nt.failed", { name }), { description: t.error ?? undefined });
    st.addNotice({ kind: "failed", name, detail: t.error ?? "" });
  } else {
    st.addNotice({ kind: "cancelled", name, detail: "" });
  }
  void useConsole.getState().refresh(true);
}

export function visibleEntries(entries: RemoteEntry[], filter: string, hidden: boolean): RemoteEntry[] {
  const f = filter.trim().toLowerCase();
  return entries.filter((e) => (hidden || !isHidden(e.name)) && (!f || e.name.toLowerCase().includes(f)));
}
