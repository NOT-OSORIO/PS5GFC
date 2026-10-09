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

import type { ReactNode } from "react";
import { useT } from "@/lib/store";
import { cn } from "@/lib/utils";
import { Checkbox } from "./Checkbox";
import { Tip } from "./Tip";

export function Section({
  title,
  std,
  onStd,
  wide,
  children,
}: {
  title: string;
  std: boolean;
  onStd: (v: boolean) => void;
  wide?: boolean;
  children: ReactNode;
}) {
  const t = useT();
  return (
    <section className="card flex h-full flex-col">
      <header className="flex items-center justify-between border-b border-line px-4 py-3">
        <h3 className="text-[13px] font-semibold">{title}</h3>
        <Tip content={t("opt.default_tip")} side="left">
          <span>
            <Checkbox checked={std} onChange={onStd} label={t("opt.default")} />
          </span>
        </Tip>
      </header>
      <div
        aria-disabled={std}
        className={cn(
          "flex-1 px-4 py-4 transition-opacity duration-[var(--d-med)]",
          wide ? "grid content-center gap-x-10 gap-y-4 lg:grid-cols-2 lg:items-center" : "flex flex-col justify-evenly gap-4",
          std && "pointer-events-none select-none opacity-45",
        )}
      >
        {children}
      </div>
    </section>
  );
}
