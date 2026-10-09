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

export function Checkbox({
  checked,
  onChange,
  label,
  className,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  className?: string;
}) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={cn("group inline-flex items-center gap-2 rounded-md py-0.5 text-[12px] font-medium text-muted hover:text-fg", className)}
    >
      <span
        className={cn(
          "grid h-4 w-4 place-items-center rounded-[5px] border",
          checked ? "border-ember bg-ember" : "border-line-strong bg-transparent group-hover:border-muted",
        )}
      >
        <svg viewBox="0 0 12 12" className="h-3 w-3" fill="none" stroke="var(--on-ember)" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
          <path
            d="M2.5 6.4 5 8.9 9.6 3.6"
            style={{ strokeDasharray: 14, strokeDashoffset: checked ? 0 : 14, transition: "stroke-dashoffset 150ms var(--ease)" }}
          />
        </svg>
      </span>
      {label}
    </button>
  );
}
