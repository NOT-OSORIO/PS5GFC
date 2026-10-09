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

import { Copy, Minus, Plus, Square, X } from "lucide-react";
import { useEffect, useState } from "react";
import { windowControls } from "@/lib/api";
import { ZOOM_MAX, ZOOM_MIN, ZOOM_STEP } from "@/lib/store";
import { useStore, useT } from "@/lib/store";
import { pct, pctText, rate } from "@/lib/utils";
import { Logo } from "./Logo";
import { Tip } from "./ui/Tip";

function ZoomControl() {
  const t = useT();
  const zoom = useStore((s) => s.settings.uiZoom);
  const setUiZoom = useStore((s) => s.setUiZoom);
  return (
    <Tip content={t("top.zoom")} side="bottom">
      <div
        data-tauri-drag-region="false"
        className="my-1.5 mr-1 flex items-center overflow-hidden rounded-md border border-line bg-surface-2 text-dim"
      >
        <button
          type="button"
          aria-label={t("top.zoom_out")}
          disabled={zoom <= ZOOM_MIN}
          onClick={() => setUiZoom(zoom - ZOOM_STEP)}
          className="flex h-6 w-6 items-center justify-center hover:bg-surface-3 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
        >
          <Minus className="h-3 w-3" />
        </button>
        <button
          type="button"
          aria-label={t("top.zoom_reset")}
          onClick={() => setUiZoom(1)}
          className="mono px-1 text-[10.5px] hover:text-fg"
        >
          {Math.round(zoom * 100)}%
        </button>
        <button
          type="button"
          aria-label={t("top.zoom_in")}
          disabled={zoom >= ZOOM_MAX}
          onClick={() => setUiZoom(zoom + ZOOM_STEP)}
          className="flex h-6 w-6 items-center justify-center hover:bg-surface-3 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
        >
          <Plus className="h-3 w-3" />
        </button>
      </div>
    </Tip>
  );
}

export function TitleBar() {
  const t = useT();
  const [maximized, setMaximized] = useState(false);
  const jobs = useStore((s) => s.jobs);
  const progress = useStore((s) => s.progress);
  const setPage = useStore((s) => s.setPage);
  const running = jobs.find((j) => j.status === "running");
  const snap = running ? (progress[running.id] ?? running.snapshot) : null;
  const p = snap ? pct(snap.done, snap.total) : 0;

  useEffect(() => {
    let un: (() => void) | undefined;
    void windowControls.isMaximized().then(setMaximized);
    void windowControls.onResized(() => void windowControls.isMaximized().then(setMaximized)).then((u) => (un = u));
    return () => un?.();
  }, []);

  return (
    <div className="titlebar flex shrink-0 select-none items-stretch">
      <div className="flex min-w-0 flex-1 items-center gap-2.5 pl-3.5" data-tauri-drag-region onDoubleClick={() => void windowControls.toggleMaximize()}>
        <Logo size={20} />
        <span className="pointer-events-none text-[12.5px] font-bold tracking-[0.04em]">PS5GFC</span>
        <span className="pointer-events-none text-[11px] text-dim">PS5 Game Format Converter</span>
      </div>

      {running && snap && (
        <Tip content={t("nav.queue")} side="bottom">
          <button
            type="button"
            onClick={() => setPage("queue")}
            aria-label={t("nav.queue")}
            data-tauri-drag-region="false"
            className="my-1.5 mr-2 flex cursor-pointer items-center gap-2 rounded-md border border-line bg-surface-2 px-2.5 text-[11px] text-muted hover:border-line-strong hover:text-fg"
          >
            <span className="pulse-dot h-1.5 w-1.5 rounded-full bg-ember" />
            <span className="mono text-fg">{pctText(p)}%</span>
            <span className="mono hidden sm:inline">{rate(snap.speed_bps)}</span>
          </button>
        </Tip>
      )}

      <ZoomControl />

      <div className="flex">
        <button type="button" className="tb-btn" aria-label="Minimize" onClick={() => void windowControls.minimize()}>
          <Minus className="h-4 w-4" />
        </button>
        <button type="button" className="tb-btn" aria-label={maximized ? "Restore" : "Maximize"} onClick={() => void windowControls.toggleMaximize()}>
          {maximized ? <Copy className="h-3.5 w-3.5" /> : <Square className="h-3.5 w-3.5" />}
        </button>
        <button type="button" className="tb-btn danger" aria-label="Close" onClick={() => void windowControls.close()}>
          <X className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}
