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

import { Gamepad2, Info, ListChecks, Moon, Repeat2, Sun, type LucideIcon } from "lucide-react";
import { motion } from "motion/react";
import { useConsole } from "@/lib/consoleStore";
import { useStore, useT, type Page } from "@/lib/store";
import type { Key } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { Tip } from "./ui/Tip";

const ITEMS: { id: Page; label: Key; icon: LucideIcon }[] = [
  { id: "convert", label: "nav.convert", icon: Repeat2 },
  { id: "queue", label: "nav.queue", icon: ListChecks },
  { id: "console", label: "nav.console", icon: Gamepad2 },
];

function NavButton({ icon: Icon, label, active, badge, onClick }: { icon: LucideIcon; label: string; active: boolean; badge?: number; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={cn("relative flex w-[64px] flex-col items-center gap-1 rounded-lg py-2 text-[10.5px] font-medium", active ? "text-fg" : "text-dim hover:bg-surface-2 hover:text-muted")}
    >
      {active && (
        <motion.span layoutId="nav-active" className="absolute inset-0 rounded-lg bg-surface-3" transition={{ type: "tween", duration: 0.18, ease: [0.2, 0.7, 0.2, 1] }} />
      )}
      <span className="relative">
        <Icon className={cn("h-[19px] w-[19px]", active && "text-ember")} strokeWidth={active ? 2.2 : 1.9} />
        {!!badge && (
          <span className="absolute -right-2.5 -top-1.5 grid h-[16px] min-w-[16px] place-items-center rounded-full bg-ember px-1 text-[9.5px] font-bold text-[var(--on-ember)]">{badge}</span>
        )}
      </span>
      <span className="relative">{label}</span>
    </button>
  );
}

export function Sidebar() {
  const t = useT();
  const page = useStore((s) => s.page);
  const setPage = useStore((s) => s.setPage);
  const jobs = useStore((s) => s.jobs);
  const theme = useStore((s) => s.settings.theme);
  const setTheme = useStore((s) => s.setTheme);
  const active = jobs.filter((j) => j.status === "queued" || j.status === "running").length;
  const consoleActive = useConsole((s) => s.transfers.filter((x) => x.status === "queued" || x.status === "running").length);

  return (
    <aside className="flex w-[84px] shrink-0 flex-col items-center justify-between border-r border-line bg-surface py-3">
      <nav className="flex flex-col items-center gap-1">
        {ITEMS.map((it) => (
          <NavButton key={it.id} icon={it.icon} label={t(it.label)} active={page === it.id} badge={it.id === "queue" ? active : it.id === "console" ? consoleActive : undefined} onClick={() => setPage(it.id)} />
        ))}
      </nav>
      <div className="flex flex-col items-center gap-1">
        <Tip content={theme === "dark" ? t("nav.theme_light") : t("nav.theme_dark")} side="right">
          <button
            type="button"
            onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
            className="mb-1 grid h-9 w-9 place-items-center rounded-lg text-muted hover:bg-surface-3 hover:text-fg"
            aria-label={theme === "dark" ? t("nav.theme_light") : t("nav.theme_dark")}
          >
            {theme === "dark" ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}
          </button>
        </Tip>
        <NavButton icon={Info} label={t("nav.about")} active={page === "about"} onClick={() => setPage("about")} />
      </div>
    </aside>
  );
}
