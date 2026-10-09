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

import { motion } from "motion/react";
import { useId } from "react";
import { cn } from "@/lib/utils";
import { Tip } from "./Tip";

export interface SegOption<T extends string | number> {
  value: T;
  label: string;
  tip?: string;

  disabled?: boolean;
}

export function Segmented<T extends string | number>({
  value,
  options,
  onChange,
  disabled,
}: {
  value: T;
  options: SegOption<T>[];
  onChange: (v: T) => void;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <div className={cn("inline-flex rounded-lg border border-line bg-surface-2 p-[2px]", disabled && "pointer-events-none")}>
      {options.map((o) => {
        const active = o.value === value;
        return (
          <Tip key={String(o.value)} content={o.tip}>
            <button
              type="button"
              onClick={() => !o.disabled && onChange(o.value)}
              aria-disabled={o.disabled || undefined}
              className={cn(
                "relative rounded-md px-3 py-1 text-[12px] font-medium",
                o.disabled ? "cursor-not-allowed text-dim opacity-50" : active ? "text-fg" : "text-muted hover:text-fg",
              )}
            >
              {active && (
                <motion.span
                  layoutId={`seg-${id}`}
                  className="absolute inset-0 rounded-md bg-surface-4"
                  transition={{ type: "tween", duration: 0.16, ease: [0.2, 0.7, 0.2, 1] }}
                />
              )}
              <span className="relative">{o.label}</span>
            </button>
          </Tip>
        );
      })}
    </div>
  );
}
