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

import { useEffect, useRef } from "react";
import { useT } from "@/lib/store";
import { cn } from "@/lib/utils";
import type { LogEntry } from "@/lib/types";

export function LogConsole({ entries }: { entries: LogEntry[] }) {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [entries.length]);
  return (
    <div ref={ref} className="mono max-h-48 select-text overflow-y-auto rounded-lg border border-line bg-bg p-3 text-[11px] leading-relaxed">
      {entries.length === 0 && <div className="text-dim">{t("q.log_empty")}</div>}
      {entries.map((e, i) => (
        <div key={i} className={cn("flex gap-3", e.level === "warn" && "text-warn", e.level === "error" && "text-bad", e.level === "info" && "text-muted")}>
          <span className="w-12 shrink-0 text-right text-dim">{(e.t_ms / 1000).toFixed(1)}s</span>
          <span className="min-w-0 break-words">{e.msg}</span>
        </div>
      ))}
    </div>
  );
}
