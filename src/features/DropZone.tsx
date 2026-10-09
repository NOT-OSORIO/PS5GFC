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

import { FileArchive, FolderInput, FolderOpen } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/Button";
import { ProgressBar } from "@/components/ui/Progress";
import { api } from "@/lib/api";
import { useStore, useT } from "@/lib/store";
import { cleanPath, cn, duration, num } from "@/lib/utils";
import { Tip } from "@/components/ui/Tip";

const EXTS = [".exfat", ".ffpkg", ".ffpfs", ".ffpfsc", ".pkg"];

export function DropZone() {
  const t = useT();
  const reading = useStore((s) => s.reading);
  const dragging = useStore((s) => s.dragging);
  const loadSource = useStore((s) => s.loadSource);
  const cancelReading = useStore((s) => s.cancelReading);
  const [menu, setMenu] = useState(false);

  async function pick(folder: boolean) {
    setMenu(false);
    const p = folder ? await api.pickFolder("") : await api.pickImage();
    if (p) void loadSource(p);
  }

  if (reading) {
    const known = reading.total > 0;
    const p = known ? Math.min(100, (reading.files / reading.total) * 100) : 0;
    const analyzing = reading.stage === "planning";
    return (
      <div className="grid h-full place-items-center px-8">
        <div className="w-full max-w-[560px]">
          <div className="mb-3 flex items-baseline justify-between">
            <h2 className="text-[15px] font-semibold">{t("read.title")}</h2>
            <span className="text-[12px] text-muted">{analyzing ? t("read.analyzing") : ""}</span>
          </div>
          <ProgressBar thick indeterminate={!known || analyzing} value={p} />
          <div className="mt-3 flex items-center justify-between gap-4 text-[12px] text-muted">
            <span className="mono">{t("read.files", { n: num(reading.files) })}</span>
            <span className="mono">{duration(reading.elapsedMs / 1000)}</span>
          </div>
          <Tip content={cleanPath(reading.current) || cleanPath(reading.path)} side="top" className="mono break-all">
            <div className="mono mt-1 h-4 truncate text-[11px] text-dim">{cleanPath(reading.current) || cleanPath(reading.path)}</div>
          </Tip>
          <div className="mt-5 flex justify-end">
            <Button variant="secondary" size="sm" onClick={cancelReading}>
              {t("read.cancel")}
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="h-full p-6">
      <div
        role="button"
        tabIndex={0}
        onClick={() => setMenu((v) => !v)}
        onKeyDown={(e) => (e.key === "Enter" || e.key === " ") && setMenu((v) => !v)}
        className={cn(
          "grid h-full min-h-[320px] cursor-pointer place-items-center rounded-2xl border border-dashed text-center transition-colors duration-[var(--d-med)]",
          dragging ? "border-ember bg-[var(--ember-soft)]" : "border-line-strong hover:border-muted hover:bg-surface",
        )}
      >
        <div className="flex flex-col items-center px-6">
          <FolderInput className={cn("mb-4 h-9 w-9 transition-colors duration-[var(--d-med)]", dragging ? "text-ember" : "text-dim")} strokeWidth={1.6} />
          <h2 className="text-[16px] font-semibold">{dragging ? t("drop.release") : t("drop.title")}</h2>
          <p className="mono mt-2 text-[12px] text-dim">{EXTS.join("   ")}</p>
          {menu && (
            <div className="mt-6 flex gap-2 [animation:fade-in_.14s_var(--ease)]" onClick={(e) => e.stopPropagation()}>
              <Button size="sm" variant="secondary" icon={<FolderOpen className="h-4 w-4" />} onClick={() => void pick(true)}>
                {t("drop.pick_folder")}
              </Button>
              <Button size="sm" variant="secondary" icon={<FileArchive className="h-4 w-4" />} onClick={() => void pick(false)}>
                {t("drop.pick_image")}
              </Button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
