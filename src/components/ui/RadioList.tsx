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

import { cn } from "@/lib/utils";
import { Tip } from "./Tip";

export interface RadioOption<T extends string> {
  value: T;
  label: string;

  example?: string;
}

export function RadioList<T extends string>({ value, options, onChange }: { value: T; options: RadioOption<T>[]; onChange: (v: T) => void }) {
  return (
    <div role="radiogroup" className="space-y-1">
      {options.map((o) => {
        const checked = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={checked}
            onClick={() => onChange(o.value)}
            className={cn(
              "flex w-full items-center gap-3 rounded-lg border px-3 py-2 text-left",
              checked ? "border-ember bg-[var(--ember-soft)]" : "border-line bg-surface-2 hover:border-line-strong hover:bg-surface-3",
            )}
          >
            <span className={cn("grid h-4 w-4 shrink-0 place-items-center rounded-full border", checked ? "border-ember" : "border-line-strong")}>
              <span className={cn("h-2 w-2 rounded-full bg-ember transition-transform duration-[var(--d-fast)]", checked ? "scale-100" : "scale-0")} />
            </span>
            <span className="min-w-0 flex-1">
              <span className="block text-[12.5px] font-medium">{o.label}</span>
              {o.example && (
                <Tip content={o.example} side="right" className="mono break-all">
                  <span className="mono mt-0.5 block truncate text-[11px] text-dim">{o.example}</span>
                </Tip>
              )}
            </span>
          </button>
        );
      })}
    </div>
  );
}
