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

import { AlertTriangle, Info, RefreshCw, ScanSearch, X } from "lucide-react";
import { useCallback, useRef, useState } from "react";
import { Cover } from "@/components/Cover";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { Popover } from "@/components/ui/Popover";
import { Tip } from "@/components/ui/Tip";
import { api } from "@/lib/api";
import { metaOf } from "@/lib/formats";
import { hasKey, type Key } from "@/lib/i18n";
import { useStore, useT } from "@/lib/store";
import type { Issue, SourceInfo } from "@/lib/types";
import { bytes, num } from "@/lib/utils";

export function issueText(i: Issue, t: (k: Key, v?: Record<string, string | number>) => string): string {
  const key = `iss.${i.code}`;
  return hasKey(key) ? t(key, i.args) : i.msg;
}

function Stat({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="panel px-3 py-2">
      <div className="eyebrow">{label}</div>
      <div className="mono mt-0.5 text-[14px] font-semibold tabular-nums">{children}</div>
    </div>
  );
}

export function SourceCard({ info }: { info: SourceInfo }) {
  const t = useT();
  const clear = useStore((s) => s.clearSource);
  const loadSource = useStore((s) => s.loadSource);
  const [details, setDetails] = useState(false);
  const anchor = useRef<HTMLDivElement>(null);
  const closeDetails = useCallback(() => setDetails(false), []);
  const meta = metaOf(info.format);
  const title = info.title;
  const sub = info.format.kind === "folder" ? t("fmt.folder") : meta.sub ? `${meta.title} · ${meta.sub}` : meta.title;

  const rows: [string, string][] = [
    ...info.details.map(([k, v]): [string, string] => {
      const lk = `detail.${k}`;
      const val = k === "case" ? (hasKey(`detail.${v}`) ? t(`detail.${v}` as Key) : v) : v;
      return [hasKey(lk) ? t(lk as Key) : k, val];
    }),
  ];
  if (info.wrapper) {
    rows.unshift([t("detail.inner"), `${info.wrapper.inner_name} · ${bytes(info.wrapper.inner_size)}`]);
    rows.unshift([t("detail.container"), bytes(info.wrapper.file_size)]);
  }

  return (
    <section className="card p-5">
      <div className="flex gap-4">
        <Cover icon={info.icon} size={84} radius={14} />
        <div className="min-w-0 flex-1">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 className="truncate text-[19px] font-semibold leading-tight">{title.title_name ?? t("src.unnamed")}</h2>
              <div className="mt-2 flex flex-wrap items-center gap-1.5">
                {title.title_id && <Chip className="mono">{title.title_id}</Chip>}
                {title.content_version && <Chip className="mono">v{title.content_version}</Chip>}
                <Chip color={meta.color}>{sub}</Chip>
                {title.ampr && (
                  <Tip content={t("src.ampr_tip")}>
                    <span>
                      <Chip color="var(--info)">AMPR</Chip>
                    </span>
                  </Tip>
                )}
                {title.ampr_packs && (
                  <Tip content={t("src.packs_tip")}>
                    <span>
                      <Chip color="var(--ok)">LZ4</Chip>
                    </span>
                  </Tip>
                )}
                {title.playgo && (
                  <Tip content={t("src.playgo_tip")}>
                    <span>
                      <Chip color="var(--info)">PlayGo</Chip>
                    </span>
                  </Tip>
                )}
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-0.5">
              {rows.length > 0 && (
                <div ref={anchor} className="relative">
                  <Tip content={t("src.details")} side="bottom">
                    <Button variant="ghost" size="sm" iconOnly onClick={() => setDetails((v) => !v)} aria-label={t("src.details")} aria-expanded={details}>
                      <ScanSearch className="h-4 w-4" />
                    </Button>
                  </Tip>
                  <Popover open={details} onClose={closeDetails} anchor={anchor} className="w-[300px] p-3">
                    <dl className="space-y-1.5 text-[12px]">
                      {rows.map(([k, v]) => (
                        <div key={k} className="flex justify-between gap-6">
                          <dt className="text-muted">{k}</dt>
                          <Tip content={v} side="left" className="mono break-all">
                            <dd className="mono truncate text-right">{v}</dd>
                          </Tip>
                        </div>
                      ))}
                    </dl>
                  </Popover>
                </div>
              )}
              <Tip content={t("src.reload")} side="bottom">
                <Button variant="ghost" size="sm" iconOnly onClick={() => void loadSource(info.path)} aria-label={t("src.reload")}>
                  <RefreshCw className="h-4 w-4" />
                </Button>
              </Tip>
              <Tip content={t("src.remove")} side="bottom">
                <Button variant="ghost" size="sm" iconOnly onClick={clear} aria-label={t("src.remove")}>
                  <X className="h-4 w-4" />
                </Button>
              </Tip>
            </div>
          </div>
          <Tip content={t("src.reveal")} side="bottom" align="start">
            <button type="button" onClick={() => void api.reveal(info.path)} className="mono mt-2 block max-w-full truncate text-left text-[11.5px] text-dim hover:text-muted">
              {info.path}
            </button>
          </Tip>
        </div>
      </div>

      <div className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
        <Stat label={t("src.files")}>{num(info.files)}</Stat>
        <Stat label={t("src.folders")}>{num(info.dirs)}</Stat>
        <Stat label={t("src.content")}>{bytes(info.bytes)}</Stat>
        {info.wrapper ? (
          <Stat label={t("src.on_disk")}>
            {bytes(info.wrapper.file_size)}{" "}
            <span className="text-[11px] font-medium text-ok">−{(100 - (info.wrapper.file_size / Math.max(1, info.wrapper.inner_size)) * 100).toFixed(0)}%</span>
          </Stat>
        ) : (
          <Stat label={t("src.eboot")}>{title.has_eboot ? <span className="text-ok">{t("src.ok")}</span> : <span className="text-warn">{t("src.missing")}</span>}</Stat>
        )}
      </div>

      {info.issues.length > 0 && (
        <ul className="mt-3 space-y-1">
          {info.issues.map((i, k) => (
            <li
              key={k}
              className="flex items-start gap-2 text-[12px] leading-snug"
              style={{ color: i.severity === "error" ? "var(--bad)" : i.severity === "warn" ? "var(--warn)" : "var(--fg-muted)" }}
            >
              {i.severity === "info" ? <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" /> : <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />}
              {issueText(i, t)}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
