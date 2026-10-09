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

import { HardDrive, Star, Usb, X, type LucideIcon } from "lucide-react";
import { Tip } from "@/components/ui/Tip";
import { px, usedPct, type StorageVolume } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { bytes, cn } from "@/lib/utils";

function volumeIcon(v: StorageVolume): LucideIcon {
  return v.kind === "usb" ? Usb : HardDrive;
}

function Item({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "true" : undefined}
      className={cn("flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-[12.5px]", active ? "bg-surface-3 text-fg" : "text-muted hover:bg-surface-2 hover:text-fg")}
    >
      {children}
    </button>
  );
}

export function Places() {
  const t = useT();
  const info = useConsole((s) => s.info);
  const path = useConsole((s) => s.path);
  const go = useConsole((s) => s.go);
  const favorites = useStore((s) => s.settings.console.favorites);
  const patch = useStore((s) => s.patchConsole);
  const volumes = info?.volumes ?? [];
  const current = volumes.filter((v) => px.isWithin(path, v.path)).sort((a, b) => b.path.length - a.path.length)[0];

  return (
    <aside className="flex w-[208px] shrink-0 flex-col gap-5 overflow-y-auto border-r border-line bg-surface px-3 py-4 xl:w-[232px]">
      {volumes.length > 0 && (
        <section>
          <h3 className="eyebrow mb-2 px-2">{t("con.disks")}</h3>
          <div className="space-y-1">
            {volumes.map((v) => {
              const Icon = volumeIcon(v);
              const p = usedPct(v);
              return (
                <Tip key={v.path} content={<span className="mono">{v.path}</span>} side="right">
                  <div>
                    <Item active={current?.path === v.path && path === v.path} onClick={() => void go(v.path)}>
                      <Icon className="h-4 w-4 shrink-0" strokeWidth={1.8} />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate font-medium">{v.label}</span>
                        <span className="mono block truncate text-[10.5px] text-dim">
                          {bytes(v.free)} {t("con.free_of")} {bytes(v.total)}
                        </span>
                        <span className="bar mt-1.5 block !h-[4px]">
                          <i style={{ width: `${p}%`, background: p > 92 ? "var(--bad)" : p > 80 ? "var(--warn)" : undefined }} />
                        </span>
                      </span>
                    </Item>
                  </div>
                </Tip>
              );
            })}
          </div>
        </section>
      )}

      <section>
        <h3 className="eyebrow mb-2 px-2">{t("con.favorites")}</h3>
        {favorites.length === 0 ? (
          <p className="px-2 text-[11.5px] leading-snug text-dim">{t("con.favorites_empty")}</p>
        ) : (
          <div className="space-y-0.5">
            {favorites.map((f) => (
              <div key={f} className="group relative">
                <Tip content={<span className="mono break-all">{f}</span>} side="right">
                  <div>
                    <Item active={path === f} onClick={() => void go(f)}>
                      <Star className="h-4 w-4 shrink-0 text-ember" fill="currentColor" strokeWidth={1.6} />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate">{px.base(f) || "/"}</span>
                        <span className="mono block truncate text-[10.5px] text-dim">{px.parent(f)}</span>
                      </span>
                    </Item>
                  </div>
                </Tip>
                <button
                  type="button"
                  onClick={() => patch({ favorites: favorites.filter((x) => x !== f) })}
                  aria-label={t("con.unfavorite")}
                  className="absolute right-1.5 top-1/2 hidden h-6 w-6 -translate-y-1/2 place-items-center rounded-md text-dim hover:bg-surface-4 hover:text-fg group-hover:grid"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
            ))}
          </div>
        )}
      </section>
    </aside>
  );
}
