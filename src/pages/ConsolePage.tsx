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

import { useEffect, useMemo, useState } from "react";
import { toast } from "sonner";
import { px, usedPct } from "@/lib/console";
import { useConsole, visibleEntries } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { bytes } from "@/lib/utils";
import { ConnectPanel } from "@/features/console/ConnectPanel";
import { ConnectionBar } from "@/features/console/ConnectionBar";
import { ConsoleDialogs } from "@/features/console/Dialogs";
import { FileList } from "@/features/console/FileList";
import { Places } from "@/features/console/Places";
import { Toolbar } from "@/features/console/Toolbar";
import { TransferDock } from "@/features/console/TransferDock";

function StatusBar() {
  const t = useT();
  const entries = useConsole((s) => s.entries);
  const filter = useConsole((s) => s.filter);
  const selected = useConsole((s) => s.selected);
  const clip = useConsole((s) => s.clip);
  const info = useConsole((s) => s.info);
  const path = useConsole((s) => s.path);
  const hidden = useStore((s) => s.settings.console.hidden);
  const rows = useMemo(() => visibleEntries(entries, filter, hidden), [entries, filter, hidden]);
  const selBytes = useMemo(() => entries.filter((e) => selected.includes(e.name) && !e.is_dir).reduce((a, e) => a + e.size, 0), [entries, selected]);
  const vol = info?.volumes.filter((v) => px.isWithin(path, v.path)).sort((a, b) => b.path.length - a.path.length)[0];
  const pct = vol ? usedPct(vol) : 0;

  return (
    <div className="flex shrink-0 items-center gap-4 border-t border-line bg-surface px-5 py-1.5 text-[11.5px] text-muted">
      <span>
        {t("con.n_items", { n: rows.length })}
        {selected.length > 0 && (
          <>
            {" · "}
            <b className="font-semibold text-fg">{t("con.n_selected", { n: selected.length })}</b>
            {selBytes > 0 && <span className="mono"> ({bytes(selBytes)})</span>}
          </>
        )}
      </span>
      {clip && (
        <span className="text-ember">
          {t(clip.mode === "cut" ? "con.clip_cut" : "con.clip_copy", { n: clip.paths.length })}
        </span>
      )}
      {vol && (
        <span className="ml-auto flex items-center gap-2">
          <span className="mono">
            {bytes(vol.free)} {t("con.free_of")} {bytes(vol.total)}
          </span>
          <span className="bar !h-[4px] w-24">
            <i style={{ width: `${pct}%`, background: pct > 92 ? "var(--bad)" : pct > 80 ? "var(--warn)" : undefined }} />
          </span>
        </span>
      )}
    </div>
  );
}

function useLive() {
  const status = useConsole((s) => s.status);
  const live = useStore((s) => s.settings.console.live);
  const hasActiveJob = useConsole((s) => s.jobs.some((j) => !["done", "attention", "canceled", "error"].includes(j.state)));

  useEffect(() => {
    if (status !== "connected" || !live) return;
    const h = window.setInterval(() => {
      const st = useConsole.getState();
      if (document.hidden || st.busy > 0 || st.dialog) return;
      void st.refresh(true);
    }, 2500);
    return () => window.clearInterval(h);
  }, [status, live]);

  useEffect(() => {
    if (status !== "connected") return;
    const h = window.setInterval(() => {
      if (!document.hidden) void useConsole.getState().ping();
    }, 6000);
    return () => window.clearInterval(h);
  }, [status]);

  useEffect(() => {
    if (status !== "connected") return;
    const h = window.setInterval(() => {
      if (!document.hidden) void useConsole.getState().pollJobs();
    }, hasActiveJob ? 700 : 4000);
    return () => window.clearInterval(h);
  }, [status, hasActiveJob]);

  useEffect(() => {
    if (status !== "lost") return;
    const h = window.setInterval(() => void useConsole.getState().connect(undefined, true), 3000);
    return () => window.clearInterval(h);
  }, [status]);
}

export function ConsolePage() {
  const t = useT();
  const status = useConsole((s) => s.status);
  const info = useConsole((s) => s.info);
  const setDropHandler = useStore((s) => s.setDropHandler);
  const [pathFocus, setPathFocus] = useState(0);
  useLive();

  useEffect(() => {
    setDropHandler((paths) => {
      if (useConsole.getState().status === "connected") void useConsole.getState().upload(paths);
      else toast.error(t("con.drop_offline"));
    });
    return () => setDropHandler(null);
  }, [setDropHandler, t]);

  const connected = (status === "connected" || status === "lost" || (status === "connecting" && info)) && info;

  return (
    <div className="flex h-full min-h-0 flex-col">
      {!connected ? (
        <ConnectPanel />
      ) : (
        <>
          <ConnectionBar />
          <div className="flex min-h-0 flex-1">
            <Places />
            <div className="flex min-w-0 flex-1 flex-col">
              <Toolbar pathFocus={pathFocus} />
              <FileList onFocusPath={() => setPathFocus((n) => n + 1)} />
              <StatusBar />
            </div>
          </div>
          <TransferDock />
        </>
      )}
      <ConsoleDialogs />
    </div>
  );
}
