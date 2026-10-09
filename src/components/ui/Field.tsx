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

import { Info } from "lucide-react";
import type { ReactNode } from "react";
import { Tip } from "./Tip";
import { cn } from "@/lib/utils";

export function Field({
  label,
  tip,
  children,
  stack,
}: {
  label: string;
  tip?: ReactNode;
  children: ReactNode;
  stack?: boolean;
}) {
  return (
    <div className={cn("flex gap-4", stack ? "flex-col gap-2" : "items-center justify-between")}>
      <div className="flex min-w-0 items-center gap-1.5 text-[13px] font-medium">
        <span className="truncate">{label}</span>
        {tip && (
          <Tip content={tip} side="right">
            <button type="button" className="text-dim hover:text-fg" aria-label={label}>
              <Info className="h-3.5 w-3.5" />
            </button>
          </Tip>
        )}
      </div>
      <div className={cn(!stack && "shrink-0")}>{children}</div>
    </div>
  );
}
