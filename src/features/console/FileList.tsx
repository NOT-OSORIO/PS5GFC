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

import { AlertTriangle, Archive, ArrowDown, ArrowUp, ClipboardPaste, Copy, Download, FolderOpen, FolderPlus, FolderSearch, Inbox, Info, PackageOpen, Pencil, RefreshCw, Scissors, SquarePen, Star, Trash2, Upload } from "lucide-react";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { Chip } from "@/components/ui/Chip";
import { ContextMenu, type MenuItem } from "@/components/ui/ContextMenu";
import { Button } from "@/components/ui/Button";
import { Tip } from "@/components/ui/Tip";
import { formatModified, isEditable, isLeftover, isZip, modeString, octal, px, sortEntries, type RemoteEntry, type SortKey } from "@/lib/console";
import { useConsole, visibleEntries } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { bytes, cn } from "@/lib/utils";
import { FileIcon } from "./FileIcon";

const ROW = 34;

function SortHead({ id, label, className }: { id: SortKey; label: string; className?: string }) {
  const sort = useConsole((s) => s.sort);
  const setSort = useConsole((s) => s.setSort);
  const on = sort.key === id;
  return (
    <button
      type="button"
      onClick={() => setSort(id)}
      className={cn("flex items-center gap-1 text-[10.5px] font-semibold uppercase tracking-[0.07em] hover:text-fg", on ? "text-fg" : "text-dim", className)}
    >
      {label}
      {on && (sort.dir === 1 ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
    </button>
  );
}

export function FileList({ onFocusPath }: { onFocusPath: () => void }) {
  const t = useT();
  const entries = useConsole((s) => s.entries);
  const filter = useConsole((s) => s.filter);
  const sort = useConsole((s) => s.sort);
  const selected = useConsole((s) => s.selected);
  const anchor = useConsole((s) => s.anchor);
  const path = useConsole((s) => s.path);
  const loading = useConsole((s) => s.loading);
  const listError = useConsole((s) => s.listError);
  const clip = useConsole((s) => s.clip);
  const dialogOpen = useConsole((s) => s.dialog !== null);

  const zipOk = useConsole((s) => s.info?.caps.zip ?? true);
  const hidden = useStore((s) => s.settings.console.hidden);
  const favorites = useStore((s) => s.settings.console.favorites);
  const patchConsole = useStore((s) => s.patchConsole);
  const dragging = useStore((s) => s.dragging);
  const c = useConsole.getState;

  const rows = useMemo(() => sortEntries(visibleEntries(entries, filter, hidden), sort), [entries, filter, hidden, sort]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number; row: RemoteEntry | null } | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ top: 0, height: 600 });
  const typed = useRef({ text: "", at: 0 });
  const cutSet = useMemo(() => new Set(clip?.mode === "cut" ? clip.paths : []), [clip]);

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const measure = () => setView({ top: el.scrollTop, height: el.clientHeight });
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const first = Math.max(0, Math.floor(view.top / ROW) - 8);
  const last = Math.min(rows.length, Math.ceil((view.top + view.height) / ROW) + 8);

  useEffect(() => {
    scroller.current?.scrollTo({ top: 0 });
    setCursor(null);
  }, [path]);

  const indexOf = useCallback((name: string | null) => (name == null ? -1 : rows.findIndex((r) => r.name === name)), [rows]);

  function reveal(i: number) {
    const el = scroller.current;
    if (!el || i < 0) return;
    const top = i * ROW;
    if (top < el.scrollTop) el.scrollTop = top;
    else if (top + ROW > el.scrollTop + el.clientHeight) el.scrollTop = top + ROW - el.clientHeight;
  }

  function select(names: string[], a?: string | null) {
    c().setSelected(names, a);
  }

  function open(e: RemoteEntry) {
    if (e.is_dir) void c().go(px.join(path, e.name));
    else if (isEditable(e)) c().openDialog({ kind: "text", path: px.join(path, e.name) });
    else if (zipOk && isZip(e.name)) c().openDialog({ kind: "unzip", name: e.name });
  }

  function onRowDown(ev: React.MouseEvent, e: RemoteEntry, i: number) {
    scroller.current?.focus({ preventScroll: true });
    setCursor(e.name);
    if (ev.button === 2) {
      if (!selected.includes(e.name)) select([e.name], e.name);
      return;
    }
    if (ev.button !== 0) return;
    if (ev.shiftKey && anchor) {
      const a = indexOf(anchor);
      const [lo, hi] = a < 0 ? [i, i] : [Math.min(a, i), Math.max(a, i)];
      select(rows.slice(lo, hi + 1).map((r) => r.name));
    } else if (ev.ctrlKey || ev.metaKey) {
      select(selected.includes(e.name) ? selected.filter((n) => n !== e.name) : [...selected, e.name], e.name);
    } else {
      select([e.name], e.name);
    }
  }

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      const tgt = ev.target as HTMLElement | null;
      if (dialogOpen || menu) return;
      if (tgt && (tgt.tagName === "INPUT" || tgt.tagName === "TEXTAREA" || tgt.isContentEditable)) return;
      const ctrl = ev.ctrlKey || ev.metaKey;
      const store = c();
      const cur = indexOf(cursor ?? (store.selected.length ? store.selected[store.selected.length - 1] : null));
      const move = (to: number) => {
        if (!rows.length) return;
        const i = Math.max(0, Math.min(rows.length - 1, to));
        const name = rows[i].name;
        setCursor(name);
        reveal(i);
        if (ev.shiftKey && store.anchor) {
          const a = indexOf(store.anchor);
          const [lo, hi] = [Math.min(a < 0 ? i : a, i), Math.max(a < 0 ? i : a, i)];
          select(rows.slice(lo, hi + 1).map((r) => r.name));
        } else select([name], name);
        ev.preventDefault();
      };
      switch (ev.key) {
        case "ArrowDown":
          return move(cur < 0 ? 0 : cur + 1);
        case "ArrowUp":
          return move(cur < 0 ? 0 : cur - 1);
        case "PageDown":
          return move((cur < 0 ? 0 : cur) + Math.floor(view.height / ROW));
        case "PageUp":
          return move((cur < 0 ? 0 : cur) - Math.floor(view.height / ROW));
        case "Home":
          return move(0);
        case "End":
          return move(rows.length - 1);
        case "Enter": {
          const e = rows[cur] ?? rows.find((r) => r.name === store.selected[0]);
          if (e) {
            open(e);
            ev.preventDefault();
          }
          return;
        }
        case "Backspace":
          ev.preventDefault();
          return store.goUp();
        case "ArrowLeft":
          if (ev.altKey) {
            ev.preventDefault();
            store.goBack();
          }
          return;
        case "ArrowRight":
          if (ev.altKey) {
            ev.preventDefault();
            store.goForward();
          }
          return;
        case "Delete":
          ev.preventDefault();
          return void store.remove();
        case "F2":
          ev.preventDefault();
          return void store.rename();
        case "F5":
          ev.preventDefault();
          return void store.refresh();
        case "Escape":
          if (store.selected.length) select([]);
          else if (store.filter) store.setFilter("");
          return;
      }
      if (ctrl && !ev.altKey) {
        const k = ev.key.toLowerCase();
        if (k === "a") {
          ev.preventDefault();
          select(rows.map((r) => r.name), rows[0]?.name ?? null);
        } else if (k === "c") {
          ev.preventDefault();
          store.copy();
        } else if (k === "x") {
          ev.preventDefault();
          store.cut();
        } else if (k === "v") {
          ev.preventDefault();
          void store.paste();
        } else if (k === "d") {
          ev.preventDefault();
          void store.download();
        } else if (k === "l") {
          ev.preventDefault();
          onFocusPath();
        } else if (k === "n" && ev.shiftKey) {
          ev.preventDefault();
          void store.newFolder();
        }
        return;
      }

      if (ev.key.length === 1 && !ev.altKey) {
        const now = Date.now();
        typed.current = { text: now - typed.current.at > 700 ? ev.key.toLowerCase() : typed.current.text + ev.key.toLowerCase(), at: now };
        const i = rows.findIndex((r) => r.name.toLowerCase().startsWith(typed.current.text));
        if (i >= 0) {
          setCursor(rows[i].name);
          reveal(i);
          select([rows[i].name], rows[i].name);
        }
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);

  }, [rows, cursor, dialogOpen, menu, view.height, path]);

  function menuItems(row: RemoteEntry | null): MenuItem[] {
    const st = c();
    const sel = st.selected;
    const n = sel.length;
    const one = n === 1 ? entries.find((e) => e.name === sel[0]) : undefined;
    const props = () => st.openDialog({ kind: "properties", entries: entries.filter((e) => sel.includes(e.name)), dir: path });
    const pasteItem: MenuItem = { kind: "item", label: t("con.paste"), icon: <ClipboardPaste />, hint: "Ctrl+V", disabled: !st.clip, onSelect: () => void st.paste() };
    if (!row || n === 0) {
      return [
        { kind: "item", label: t("con.new_folder"), icon: <FolderPlus />, hint: "Ctrl+Shift+N", onSelect: () => void st.newFolder() },
        pasteItem,
        { kind: "sep" },
        { kind: "item", label: t("con.send_files"), icon: <Upload />, onSelect: () => void st.uploadPick("files") },
        { kind: "item", label: t("con.send_folder"), icon: <FolderSearch />, onSelect: () => void st.uploadPick("folders") },
        { kind: "sep" },
        { kind: "item", label: t("con.refresh"), icon: <RefreshCw />, hint: "F5", onSelect: () => void st.refresh() },
        {
          kind: "item",
          label: t("con.copy_path"),
          icon: <Copy />,
          onSelect: () => void navigator.clipboard?.writeText(path).then(() => toast(t("con.path_copied"))),
        },
      ];
    }
    const items: MenuItem[] = [];
    if (one?.is_dir) items.push({ kind: "item", label: t("con.open"), icon: <FolderOpen />, hint: "Enter", onSelect: () => open(one) });
    if (one && isEditable(one)) items.push({ kind: "item", label: t("con.edit"), icon: <SquarePen />, hint: "Enter", onSelect: () => open(one) });
    if (zipOk && one && isZip(one.name)) items.push({ kind: "item", label: t("con.unzip"), icon: <PackageOpen />, onSelect: () => st.openDialog({ kind: "unzip", name: one.name }) });
    if (items.length) items.push({ kind: "sep" });
    items.push(
      { kind: "item", label: t("con.download"), icon: <Download />, hint: "Ctrl+D", onSelect: () => void st.download() },
      { kind: "sep" },
      { kind: "item", label: t("con.copy"), icon: <Copy />, hint: "Ctrl+C", onSelect: () => st.copy() },
      { kind: "item", label: t("con.cut"), icon: <Scissors />, hint: "Ctrl+X", onSelect: () => st.cut() },
    );
    if (one?.is_dir) items.push({ ...pasteItem, label: t("con.paste_into") , onSelect: () => void st.go(px.join(path, one.name)).then(() => st.paste()) });
    items.push(
      { kind: "item", label: t("con.rename"), icon: <Pencil />, hint: "F2", disabled: n !== 1, onSelect: () => void st.rename() },
    );
    if (zipOk) items.push({ kind: "item", label: t("con.zip"), icon: <Archive />, onSelect: () => st.openDialog({ kind: "zip", names: sel }) });
    if (one?.is_dir) {
      const p = px.join(path, one.name);
      const isFav = favorites.includes(p);
      items.push({
        kind: "item",
        label: isFav ? t("con.unfavorite") : t("con.favorite"),
        icon: <Star />,
        onSelect: () => patchConsole({ favorites: isFav ? favorites.filter((x) => x !== p) : [...favorites, p] }),
      });
    }
    items.push(
      { kind: "item", label: t("con.copy_path"), icon: <Copy />, onSelect: () => void navigator.clipboard?.writeText(sel.map((s) => px.join(path, s)).join("\n")).then(() => toast(t("con.path_copied"))) },
      { kind: "sep" },
      { kind: "item", label: t("con.props"), icon: <Info />, onSelect: props },
      { kind: "item", label: t("con.delete"), icon: <Trash2 />, hint: "Del", danger: true, onSelect: () => void st.remove() },
    );
    return items;
  }

  const empty = !loading && !listError && rows.length === 0;

  return (
    <div className="relative flex min-h-0 min-w-0 flex-1 flex-col @container">

      <div className="flex shrink-0 items-center gap-3 border-b border-line bg-surface px-5 py-2 pr-6">
        <span className="w-[17px] shrink-0" />
        <SortHead id="name" label={t("con.col_name")} className="min-w-0 flex-1" />
        <SortHead id="size" label={t("con.col_size")} className="w-24 justify-end" />
        <SortHead id="modified" label={t("con.col_modified")} className="hidden w-40 @[640px]:flex" />
        <SortHead id="mode" label={t("con.col_perm")} className="hidden w-[84px] @[780px]:flex" />
      </div>

      <div
        ref={scroller}
        tabIndex={0}
        onScroll={(e) => setView({ top: e.currentTarget.scrollTop, height: e.currentTarget.clientHeight })}
        onMouseDown={(e) => {
          if (e.target === e.currentTarget || (e.target as HTMLElement).dataset.empty) {
            select([]);
            setCursor(null);
          }
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          if (e.target === e.currentTarget || (e.target as HTMLElement).dataset.empty) select([]);
          setMenu({ x: e.clientX, y: e.clientY, row: null });
        }}
        style={{ outline: "none" }}
        className="min-h-0 flex-1 overflow-y-auto"
      >
        {listError && (
          <div className="grid h-full place-items-center px-6 text-center" data-empty="1">
            <div className="max-w-[420px]">
              <AlertTriangle className="mx-auto mb-3 h-7 w-7 text-warn" strokeWidth={1.6} />
              <p className="text-[13px] leading-snug text-muted">{listError}</p>
              <div className="mt-4 flex justify-center gap-2">
                <Button size="sm" variant="secondary" onClick={() => void c().refresh()}>
                  {t("con.retry")}
                </Button>
                {path !== "/" && (
                  <Button size="sm" variant="ghost" onClick={() => void c().go(px.parent(path))}>
                    {t("con.up")}
                  </Button>
                )}
              </div>
            </div>
          </div>
        )}

        {loading && rows.length === 0 && !listError && (
          <div className="px-5 py-2">
            {Array.from({ length: 9 }).map((_, i) => (
              <div key={i} className="flex h-[34px] items-center gap-3">
                <span className="h-4 w-4 rounded bg-surface-3" />
                <span className="h-3 rounded bg-surface-3" style={{ width: `${30 + ((i * 37) % 40)}%` }} />
              </div>
            ))}
          </div>
        )}

        {empty && (
          <div className="grid h-full place-items-center px-6 text-center" data-empty="1">
            <div className="pointer-events-none text-dim">
              <Inbox className="mx-auto mb-3 h-8 w-8" strokeWidth={1.4} />
              <p className="text-[13px]">{filter ? t("con.no_match") : t("con.empty")}</p>
              {!filter && <p className="mt-1 text-[11.5px]">{t("con.empty_hint")}</p>}
            </div>
          </div>
        )}

        {rows.length > 0 && (
          <div style={{ height: rows.length * ROW, position: "relative" }}>
            {rows.slice(first, last).map((e, k) => {
              const i = first + k;
              const isSel = selected.includes(e.name);
              const full = px.join(path, e.name);
              const left = isLeftover(e.name);
              return (
                <div
                  key={e.name}
                  role="row"
                  aria-selected={isSel}
                  onMouseDown={(ev) => onRowDown(ev, e, i)}
                  onDoubleClick={() => open(e)}
                  onContextMenu={(ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    setMenu({ x: ev.clientX, y: ev.clientY, row: e });
                  }}
                  style={{ position: "absolute", top: i * ROW, height: ROW, left: 0, right: 0 }}
                  className={cn(
                    "flex cursor-default items-center gap-3 border-l-2 px-[18px] pr-6 text-[12.5px]",
                    isSel ? "border-ember bg-[var(--ember-soft)]" : "border-transparent hover:bg-surface-2",
                    cursor === e.name && !isSel && "bg-surface-2",
                    cutSet.has(full) && "opacity-50",
                  )}
                >
                  <FileIcon entry={e} />
                  <span className="flex min-w-0 flex-1 items-center gap-2">
                    <Tip content={<span className="mono break-all">{full}</span>} side="bottom" align="start">
                      <span className={cn("truncate", e.is_dir && "font-medium")}>{e.name}</span>
                    </Tip>
                    {left && (
                      <Tip content={t("con.leftover_tip")}>
                        <span>
                          <Chip color="var(--warn)" className="!text-[10.5px]">
                            {t("con.leftover")}
                          </Chip>
                        </span>
                      </Tip>
                    )}
                  </span>
                  <span className="mono w-24 shrink-0 text-right text-[12px] tabular-nums text-muted">{e.is_dir ? "—" : bytes(e.size)}</span>
                  <span className="mono hidden w-40 shrink-0 text-[11.5px] tabular-nums text-muted @[640px]:block">{formatModified(e.modified)}</span>
                  <Tip content={`${modeString(e.mode)} (${octal(e.mode)})`} side="left">
                    <span className="mono hidden w-[84px] shrink-0 text-[11.5px] text-dim @[780px]:block">{octal(e.mode)}</span>
                  </Tip>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {dragging && (
        <div className="pointer-events-none absolute inset-2 z-20 grid place-items-center rounded-xl border-2 border-dashed border-ember bg-[var(--ember-soft)] backdrop-blur-[1px] [animation:fade-in_.12s_var(--ease)]">
          <div className="text-center">
            <Upload className="mx-auto mb-2 h-8 w-8 text-ember" strokeWidth={1.7} />
            <div className="text-[14px] font-semibold">{t("con.drop_here")}</div>
            <div className="mono mt-1 text-[12px] text-muted">{path}</div>
          </div>
        </div>
      )}

      {menu && <ContextMenu x={menu.x} y={menu.y} items={menuItems(menu.row)} onClose={() => setMenu(null)} />}
    </div>
  );
}
