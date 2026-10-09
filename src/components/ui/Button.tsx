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

import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils";

type Variant = "primary" | "secondary" | "ghost" | "danger";

export function Button({
  variant = "secondary",
  size = "md",
  icon,
  iconOnly,
  className,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: "sm" | "md" | "lg"; icon?: ReactNode; iconOnly?: boolean }) {
  return (
    <button
      type="button"
      {...rest}
      className={cn(
        "inline-flex select-none items-center justify-center gap-2 rounded-lg font-medium disabled:opacity-40",
        size === "sm" && (iconOnly ? "h-8 w-8" : "h-8 px-3 text-[12px]"),
        size === "md" && (iconOnly ? "h-9 w-9" : "h-9 px-4 text-[13px]"),
        size === "lg" && (iconOnly ? "h-10 w-10" : "h-10 px-5 text-[13px]"),
        variant === "primary" && "bg-ember text-[var(--on-ember)] hover:bg-[var(--ember-hover)] disabled:hover:bg-ember",
        variant === "secondary" && "border border-line-strong bg-surface-2 text-fg hover:bg-surface-3 disabled:hover:bg-surface-2",
        variant === "ghost" && "text-muted hover:bg-surface-3 hover:text-fg disabled:hover:bg-transparent",
        variant === "danger" && "border border-bad/40 text-bad hover:bg-bad/10 disabled:hover:bg-transparent",
        className,
      )}
    >
      {icon}
      {children}
    </button>
  );
}
