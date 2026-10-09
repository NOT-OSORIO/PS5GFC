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

import { ArrowLeft, Bell, Check, CheckCircle2, FolderSearch, Globe, OctagonAlert, Settings2, Trash2, XCircle } from "lucide-react";
import { useCallback, useRef, useState } from "react";
import { api } from "@/lib/api";
import { px } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { LANGS } from "@/lib/i18n";
import { useStore, useT } from "@/lib/store";
import type { Notice } from "@/lib/types";
import { cn, timeOfDay } from "@/lib/utils";
import { Popover } from "./ui/Popover";
import { Switch } from "./ui/Switch";
import { Tip } from "./ui/Tip";

function LanguageMenu() {
  const t = useT();
  const lang = useStore((s) => s.settings.lang);
  const setLang = useStore((s) => s.setLang);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const close = useCallback(() => setOpen(false), []);
  const cur = LANGS.find((l) => l.code === lang) ?? LANGS[1];

  return (
    <div ref={ref} className="relative">
      <Tip content={t("top.language")} side="bottom">
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          className={cn("flex h-9 items-center gap-1.5 rounded-lg border border-line bg-surface px-2.5 text-[12px] font-semibold text-muted hover:border-line-strong hover:text-fg", open && "border-line-strong text-fg")}
        >
          <Globe className="h-4 w-4" />
          {cur.short}
        </button>
      </Tip>
      <Popover open={open} onClose={close} anchor={ref} className="w-52 p-1">
        {LANGS.map((l) => (
          <button
            key={l.code}
            type="button"
            role="menuitem"
            onClick={() => {
              setLang(l.code);
              close();
            }}
            className={cn("flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-[13px] hover:bg-surface-3", l.code === lang && "text-fg")}
          >
            <span className={l.code === lang ? "font-semibold" : "text-muted"}>{l.native}</span>
            {l.code === lang ? <Check className="h-4 w-4 text-ember" /> : <span className="mono text-[11px] text-dim">{l.short}</span>}
          </button>
        ))}
      </Popover>
    </div>
  );
}

function NoticeRow({ n }: { n: Notice }) {
  const t = useT();
  const Icon = n.kind === "done" ? CheckCircle2 : n.kind === "failed" ? OctagonAlert : XCircle;
  const color = n.kind === "done" ? "text-ok" : n.kind === "failed" ? "text-bad" : "text-warn";
  const title = n.kind === "done" ? t("nt.done", { name: n.name }) : n.kind === "failed" ? t("nt.failed", { name: n.name }) : t("nt.cancelled", { name: n.name });
  return (
    <div className="flex items-start gap-3 px-4 py-3">
      <Icon className={cn("mt-0.5 h-4 w-4 shrink-0", color)} />
      <div className="min-w-0 flex-1">
        <div className="flex items-baseline justify-between gap-3">
          <div className={cn("truncate text-[12.5px]", n.read ? "font-medium text-muted" : "font-semibold text-fg")}>{title}</div>
          <span className="mono shrink-0 text-[10.5px] text-dim">{timeOfDay(n.ts)}</span>
        </div>
        {n.detail && <div className="mt-0.5 line-clamp-2 break-words text-[11.5px] leading-snug text-dim">{n.detail}</div>}
      </div>
      {n.path && (
        <Tip content={n.console ? t("con.show_on_console") : t("q.show")} side="left">
          <button
            type="button"
            onClick={() => (n.console ? useConsole.getState().openFolder(n.kind === "done" && n.path ? px.parent(n.path) : "/") : void api.reveal(n.path!))}
            className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-dim hover:bg-surface-3 hover:text-fg"
            aria-label={n.console ? t("con.show_on_console") : t("q.show")}
          >
            <FolderSearch className="h-4 w-4" />
          </button>
        </Tip>
      )}
    </div>
  );
}

function NotificationMenu() {
  const t = useT();
  const notices = useStore((s) => s.notices);
  const markAllRead = useStore((s) => s.markAllRead);
  const clearNotices = useStore((s) => s.clearNotices);
  const notify = useStore((s) => s.settings.notify);
  const patchNotify = useStore((s) => s.patchNotify);
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<"list" | "settings">("list");
  const ref = useRef<HTMLDivElement>(null);
  const unread = notices.filter((n) => !n.read).length;

  const close = useCallback(() => {
    setOpen(false);
    markAllRead();
    window.setTimeout(() => setView("list"), 150);
  }, [markAllRead]);

  const rows: { key: "system" | "toast" | "openFolder"; label: string }[] = [
    { key: "system", label: t("nt.system") },
    { key: "toast", label: t("nt.toast") },
    { key: "openFolder", label: t("nt.open") },
  ];

  return (
    <div ref={ref} className="relative">
      <Tip content={t("top.notifications")} side="bottom">
        <button
          type="button"
          onClick={() => (open ? close() : setOpen(true))}
          aria-expanded={open}
          className={cn("relative grid h-9 w-9 place-items-center rounded-lg border border-line bg-surface text-muted hover:border-line-strong hover:text-fg", open && "border-line-strong text-fg")}
        >
          <Bell className="h-4 w-4" />
          {unread > 0 && (
            <span className="absolute -right-1 -top-1 grid h-[16px] min-w-[16px] place-items-center rounded-full bg-ember px-1 text-[9.5px] font-bold text-[var(--on-ember)]">{unread > 9 ? "9+" : unread}</span>
          )}
        </button>
      </Tip>
      <Popover open={open} onClose={close} anchor={ref} className="w-[360px]">
        <div className="flex items-center justify-between border-b border-line px-4 py-2.5">
          {view === "settings" ? (
            <button type="button" onClick={() => setView("list")} className="-ml-1.5 flex items-center gap-1.5 rounded-md px-1.5 py-1 text-[13px] font-semibold hover:bg-surface-3">
              <ArrowLeft className="h-4 w-4" />
              {t("nt.settings")}
            </button>
          ) : (
            <h3 className="text-[13px] font-semibold">{t("nt.title")}</h3>
          )}
          {view === "list" && (
            <div className="flex items-center gap-0.5">
              {notices.length > 0 && (
                <Tip content={t("nt.clear")} side="bottom">
                  <button type="button" onClick={clearNotices} className="grid h-7 w-7 place-items-center rounded-md text-dim hover:bg-surface-3 hover:text-fg" aria-label={t("nt.clear")}>
                    <Trash2 className="h-4 w-4" />
                  </button>
                </Tip>
              )}
              <Tip content={t("nt.settings")} side="bottom">
                <button type="button" onClick={() => setView("settings")} className="grid h-7 w-7 place-items-center rounded-md text-dim hover:bg-surface-3 hover:text-fg" aria-label={t("nt.settings")}>
                  <Settings2 className="h-4 w-4" />
                </button>
              </Tip>
            </div>
          )}
        </div>
        {view === "list" ? (
          <div className="max-h-[360px] divide-y divide-line overflow-y-auto">
            {notices.length === 0 ? <div className="px-4 py-10 text-center text-[12.5px] text-dim">{t("nt.empty")}</div> : notices.map((n) => <NoticeRow key={n.id} n={n} />)}
          </div>
        ) : (
          <div className="space-y-3.5 px-4 py-4">
            {rows.map((r) => (
              <div key={r.key} className="flex items-center justify-between gap-4 text-[13px]">
                <span>{r.label}</span>
                <Switch checked={notify[r.key]} onCheckedChange={(v) => patchNotify({ [r.key]: v })} />
              </div>
            ))}
          </div>
        )}
      </Popover>
    </div>
  );
}

export function TopActions() {
  return (
    <div className="flex items-center gap-2">
      <NotificationMenu />
      <LanguageMenu />
    </div>
  );
}
