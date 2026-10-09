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

import {
  Archive,
  ArrowLeft,
  ArrowRight,
  ArrowUp,
  ChevronRight,
  ClipboardPaste,
  Copy,
  Download,
  Ellipsis,
  Eye,
  EyeOff,
  FolderPlus,
  Info,
  PackageOpen,
  Pencil,
  RefreshCw,
  Scissors,
  Search,
  Star,
  Trash2,
  Upload,
  X,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { Popover } from "@/components/ui/Popover";
import { Tip } from "@/components/ui/Tip";
import { isZip, px } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { cn } from "@/lib/utils";

function ToolButton({ icon, label, hint, onClick, disabled, active }: { icon: ReactNode; label: string; hint?: string; onClick: () => void; disabled?: boolean; active?: boolean }) {
  return (
    <Tip content={hint ? `${label} · ${hint}` : label} side="bottom">
      <span>
        <Button variant="ghost" size="sm" iconOnly icon={icon} onClick={onClick} disabled={disabled} aria-label={label} className={cn(active && "bg-surface-3 text-fg")} />
      </span>
    </Tip>
  );
}

function PathBar({ focusSignal }: { focusSignal: number }) {
  const t = useT();
  const path = useConsole((s) => s.path);
  const go = useConsole((s) => s.go);
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(path);
  const scroller = useRef<HTMLDivElement>(null);
  const parts = px.parts(path);

  useEffect(() => {
    if (focusSignal > 0) {
      setText(path);
      setEditing(true);
    }

  }, [focusSignal]);

  useEffect(() => {
    const el = scroller.current;
    if (el) el.scrollLeft = el.scrollWidth;
  }, [path, editing]);

  function submit() {
    setEditing(false);
    const next = px.norm(text.trim() || "/");
    if (next !== path) void go(next);
  }

  return (
    <div className="flex h-9 min-w-0 flex-1 items-center rounded-lg border border-line bg-surface hover:border-line-strong">
      {editing ? (
        <Input
          autoFocus
          value={text}
          onChange={(e) => setText(e.target.value)}
          onFocus={(e) => e.currentTarget.select()}
          onBlur={() => setEditing(false)}
          onKeyDown={(e) => {
            if (e.key === "Enter") submit();
            if (e.key === "Escape") setEditing(false);
          }}
          className="mono h-[34px] border-0 bg-transparent"
          aria-label={t("con.path")}
        />
      ) : (
        <div
          ref={scroller}
          className="flex min-w-0 flex-1 cursor-text items-center overflow-hidden whitespace-nowrap px-1.5 [scrollbar-width:none]"
          onClick={() => {
            setText(path);
            setEditing(true);
          }}
        >
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              void go("/");
            }}
            className="mono shrink-0 rounded-md px-1.5 py-1 text-[12.5px] text-muted hover:bg-surface-3 hover:text-fg"
          >
            /
          </button>
          {parts.map((p, i) => {
            const target = "/" + parts.slice(0, i + 1).join("/");
            const last = i === parts.length - 1;
            return (
              <span key={target} className="flex shrink-0 items-center">
                {i > 0 && <ChevronRight className="h-3 w-3 text-dim" />}
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    void go(target);
                  }}
                  className={cn("mono rounded-md px-1.5 py-1 text-[12.5px] hover:bg-surface-3", last ? "font-semibold text-fg" : "text-muted hover:text-fg")}
                >
                  {p}
                </button>
              </span>
            );
          })}
        </div>
      )}
    </div>
  );
}

export function Toolbar({ pathFocus }: { pathFocus: number }) {
  const t = useT();
  const path = useConsole((s) => s.path);
  const entries = useConsole((s) => s.entries);
  const selected = useConsole((s) => s.selected);
  const clip = useConsole((s) => s.clip);
  const back = useConsole((s) => s.back);
  const fwd = useConsole((s) => s.fwd);
  const filter = useConsole((s) => s.filter);
  const busy = useConsole((s) => s.busy);
  const zipOk = useConsole((s) => s.info?.caps.zip ?? true);
  const c = useConsole.getState;
  const cfg = useStore((s) => s.settings.console);
  const patch = useStore((s) => s.patchConsole);
  const fav = cfg.favorites.includes(path);

  const [upMenu, setUpMenu] = useState(false);
  const [more, setMore] = useState(false);
  const upRef = useRef<HTMLDivElement>(null);
  const moreRef = useRef<HTMLDivElement>(null);
  const closeUp = useCallback(() => setUpMenu(false), []);
  const closeMore = useCallback(() => setMore(false), []);

  const n = selected.length;
  const one = n === 1 ? entries.find((e) => e.name === selected[0]) : undefined;
  const none = n === 0;

  function props() {
    const sel = entries.filter((e) => selected.includes(e.name));
    c().openDialog({ kind: "properties", entries: sel.length ? sel : [], dir: path });
  }

  return (
    <div className="flex flex-col gap-2 border-b border-line bg-bg px-5 py-2.5">
      <div className="flex items-center gap-1.5">
        <ToolButton icon={<ArrowLeft className="h-4 w-4" />} label={t("con.back")} hint="Alt+←" onClick={() => c().goBack()} disabled={!back.length} />
        <ToolButton icon={<ArrowRight className="h-4 w-4" />} label={t("con.forward")} hint="Alt+→" onClick={() => c().goForward()} disabled={!fwd.length} />
        <ToolButton icon={<ArrowUp className="h-4 w-4" />} label={t("con.up")} hint="Backspace" onClick={() => c().goUp()} disabled={path === "/"} />
        <ToolButton icon={<RefreshCw className="h-4 w-4" />} label={t("con.refresh")} hint="F5" onClick={() => void c().refresh()} />
        <PathBar focusSignal={pathFocus} />
        <ToolButton
          icon={<Star className="h-4 w-4" fill={fav ? "currentColor" : "none"} />}
          label={fav ? t("con.unfavorite") : t("con.favorite")}
          onClick={() => patch({ favorites: fav ? cfg.favorites.filter((x) => x !== path) : [...cfg.favorites, path] })}
          active={fav}
        />
        <div className="relative w-[200px] shrink-0">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-dim" />
          <Input value={filter} onChange={(e) => c().setFilter(e.target.value)} placeholder={t("con.filter")} className="h-9 pl-8 pr-8" aria-label={t("con.filter")} />
          {filter && (
            <button type="button" onClick={() => c().setFilter("")} className="absolute right-1.5 top-1/2 grid h-6 w-6 -translate-y-1/2 place-items-center rounded-md text-dim hover:bg-surface-3 hover:text-fg" aria-label="×">
              <X className="h-3.5 w-3.5" />
            </button>
          )}
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-1">
        <div ref={upRef} className="relative">
          <Button variant="primary" size="sm" icon={<Upload className="h-3.5 w-3.5" />} onClick={() => setUpMenu((v) => !v)} aria-expanded={upMenu}>
            {t("con.send")}
          </Button>
          <Popover open={upMenu} onClose={closeUp} anchor={upRef} align="left" className="w-52 p-1">
            {[
              { label: t("con.send_files"), run: () => void c().uploadPick("files") },
              { label: t("con.send_folder"), run: () => void c().uploadPick("folders") },
            ].map((it) => (
              <button
                key={it.label}
                type="button"
                role="menuitem"
                onClick={() => {
                  closeUp();
                  it.run();
                }}
                className="flex w-full items-center rounded-lg px-3 py-2 text-left text-[12.5px] hover:bg-surface-3"
              >
                {it.label}
              </button>
            ))}
          </Popover>
        </div>
        <ToolButton icon={<Download className="h-4 w-4" />} label={t("con.download")} hint="Ctrl+D" onClick={() => void c().download()} disabled={none} />
        <span className="mx-1 h-5 w-px bg-line" />
        <ToolButton icon={<FolderPlus className="h-4 w-4" />} label={t("con.new_folder")} hint="Ctrl+Shift+N" onClick={() => void c().newFolder()} />
        <ToolButton icon={<Copy className="h-4 w-4" />} label={t("con.copy")} hint="Ctrl+C" onClick={() => c().copy()} disabled={none} active={clip?.mode === "copy"} />
        <ToolButton icon={<Scissors className="h-4 w-4" />} label={t("con.cut")} hint="Ctrl+X" onClick={() => c().cut()} disabled={none} active={clip?.mode === "cut"} />
        <ToolButton icon={<ClipboardPaste className="h-4 w-4" />} label={t("con.paste")} hint="Ctrl+V" onClick={() => void c().paste()} disabled={!clip} />
        <ToolButton icon={<Pencil className="h-4 w-4" />} label={t("con.rename")} hint="F2" onClick={() => void c().rename()} disabled={n !== 1} />
        <ToolButton icon={<Trash2 className="h-4 w-4" />} label={t("con.delete")} hint="Del" onClick={() => void c().remove()} disabled={none} />
        <span className="mx-1 h-5 w-px bg-line" />
        <div ref={moreRef} className="relative">
          <Tip content={t("con.more")} side="bottom">
            <span>
              <Button variant="ghost" size="sm" iconOnly icon={<Ellipsis className="h-4 w-4" />} onClick={() => setMore((v) => !v)} aria-label={t("con.more")} aria-expanded={more} />
            </span>
          </Tip>
          <Popover open={more} onClose={closeMore} anchor={moreRef} align="left" className="w-64 p-1">
            {(
              [
                { label: t("con.props"), icon: <Info className="h-4 w-4" />, run: props, off: false },
                { label: t("con.zip"), icon: <Archive className="h-4 w-4" />, run: () => c().openDialog({ kind: "zip", names: selected }), off: none, needsZip: true },
                { label: t("con.unzip"), icon: <PackageOpen className="h-4 w-4" />, run: () => one && c().openDialog({ kind: "unzip", name: one.name }), off: !one || !isZip(one.name), needsZip: true },
                {
                  label: t("con.copy_path"),
                  icon: <Copy className="h-4 w-4" />,
                  run: () => {
                    const text = one ? px.join(path, one.name) : path;
                    void navigator.clipboard?.writeText(text).then(() => toast(t("con.path_copied")));
                  },
                  off: false,
                },
                {
                  label: cfg.hidden ? t("con.hide_hidden") : t("con.show_hidden"),
                  icon: cfg.hidden ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />,
                  run: () => patch({ hidden: !cfg.hidden }),
                  off: false,
                },
              ] as const
            )
              .filter((it) => zipOk || !("needsZip" in it))
              .map((it) => (
              <button
                key={it.label}
                type="button"
                role="menuitem"
                disabled={it.off}
                onClick={() => {
                  closeMore();
                  it.run();
                }}
                className="flex w-full items-center gap-2.5 rounded-lg px-3 py-2 text-left text-[12.5px] hover:bg-surface-3 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent"
              >
                <span className="text-muted">{it.icon}</span>
                {it.label}
              </button>
            ))}
          </Popover>
        </div>
        {busy > 0 && <span className="pulse-dot ml-2 h-1.5 w-1.5 rounded-full bg-ember" />}
      </div>
    </div>
  );
}
