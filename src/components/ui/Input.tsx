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

import { forwardRef, type InputHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement> & { invalid?: boolean }>(function Input({ className, invalid, ...rest }, ref) {
  return (
    <input
      ref={ref}
      spellCheck={false}
      autoComplete="off"
      {...rest}
      className={cn(
        "h-9 w-full rounded-lg border bg-surface px-3 text-[13px] text-fg outline-none placeholder:text-dim",
        "transition-colors duration-[var(--d-fast)] hover:border-line-strong focus:border-ember",
        invalid ? "border-bad/60 focus:border-bad" : "border-line",
        className,
      )}
    />
  );
});
