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

export function ProgressBar({ value, indeterminate, thick, className }: { value?: number; indeterminate?: boolean; thick?: boolean; className?: string }) {
  return (
    <div
      role="progressbar"
      aria-valuenow={indeterminate ? undefined : Math.round(value ?? 0)}
      aria-valuemin={0}
      aria-valuemax={100}
      className={cn("bar", thick && "thick", indeterminate && "indet", className)}
    >
      <i style={indeterminate ? undefined : { width: `${Math.max(0, Math.min(100, value ?? 0))}%` }} />
    </div>
  );
}
