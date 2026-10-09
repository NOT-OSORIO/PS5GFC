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

import { ChevronRight, FolderSearch, Terminal, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { Cover } from "@/components/Cover";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { ProgressBar } from "@/components/ui/Progress";
import { Tip } from "@/components/ui/Tip";
import { api } from "@/lib/api";
import { px } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { displayName, metaOf } from "@/lib/formats";
import type { Key } from "@/lib/i18n";
import { useStore, useT } from "@/lib/store";
import type { JobView, Route, Stage } from "@/lib/types";
import { baseName, bytes, cleanPath, cn, duration, num, pct, pctText, rate, sizeDelta } from "@/lib/utils";
import { LogConsole } from "./LogConsole";

const STAGE_KEY: Record<Stage, Key> = {
  idle: "stage.idle",
  scanning: "stage.scanning",
  planning: "stage.planning",
  processing: "stage.processing",
  finalizing: "stage.finalizing",
  verifying: "stage.verifying",
  done: "stage.done",
};
const ROUTE_KEY: Record<Route, Key> = { extract: "route.extract", reuse_image: "route.direct", build: "route.rebuild" };

function Flow({ job }: { job: JobView }) {
  const t = useT();
  const m = metaOf(job.target);
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-muted">
      <Tip content={cleanPath(job.source)} side="top" className="mono break-all">
        <span className="mono max-w-[300px] truncate">{baseName(job.source)}</span>
      </Tip>
      <span className="text-dim">→</span>
      <Chip color={m.color}>
        {displayName(job.target, t("fmt.folder"))} {m.ext && <span className="mono opacity-80">{m.ext}</span>}
      </Chip>
      {job.console && (
        <Tip content={<span className="mono break-all">{`${job.console.host}:${job.console.port} ${job.dest}`}</span>} side="top">
          <span>
            <Chip color="var(--ember)">→ PS5</Chip>
          </span>
        </Tip>
      )}
    </div>
  );
}

function Metric({ label, children, tone }: { label: string; children: React.ReactNode; tone?: string }) {
  return (
    <div className="px-4 py-2.5 first:pl-0">
      <div className="eyebrow">{label}</div>
      <div className={cn("mono mt-0.5 text-[13px] font-medium tabular-nums", tone)}>{children}</div>
    </div>
  );
}

export function RunningCard({ job }: { job: JobView }) {
  const t = useT();
  const snap = useStore((s) => s.progress[job.id]) ?? job.snapshot;
  const logs = useStore((s) => s.logs[job.id]) ?? [];
  const loadLogs = useStore((s) => s.loadLogs);
  const [showLog, setShowLog] = useState(false);
  const [cancelling, setCancelling] = useState(false);

  useEffect(() => {
    void loadLogs(job.id);
  }, [job.id, loadLogs]);

  const total = snap?.total ?? 0;
  const done = snap?.done ?? 0;
  const p = pct(done, total);
  const stage = snap?.stage ?? "idle";
  const saving = snap?.ratio != null && job.target.kind === "ffpfsc" ? 1 - snap.ratio : null;
  const indeterminate = !total || stage === "scanning" || stage === "planning";

  return (
    <section className="card p-5 [animation:fade-in_.2s_var(--ease)]">
      <div className="flex items-start gap-3.5">
        <Cover icon={job.icon} size={48} radius={10} />
        <div className="min-w-0 flex-1">
          <h3 className="truncate text-[15px] font-semibold">{job.label ?? baseName(job.source)}</h3>
          <Flow job={job} />
        </div>
        <Button
          variant="danger"
          size="sm"
          icon={<X className="h-3.5 w-3.5" />}
          disabled={cancelling}
          onClick={() => {
            setCancelling(true);
            void api.cancel(job.id);
          }}
        >
          {cancelling ? t("q.cancelling") : t("q.cancel")}
        </Button>
      </div>

      <div className="mt-5">
        <div className="mb-2 flex items-baseline justify-between gap-4">
          <span className="flex items-center gap-2 text-[12px] font-medium text-muted">
            <span className="pulse-dot h-1.5 w-1.5 rounded-full bg-ember" />
            {t(STAGE_KEY[stage])}
          </span>
          <span className="mono text-[12px] text-muted">
            {!indeterminate && <b className="mr-3 text-[15px] font-semibold text-fg">{pctText(p)}%</b>}
            {bytes(done)} {t("q.of")} {bytes(total)}
          </span>
        </div>
        <ProgressBar thick value={p} indeterminate={indeterminate} />
        <Tip content={cleanPath(snap?.current)} side="top" className="mono break-all">
          <div className="mono mt-1.5 h-4 truncate text-[11px] text-dim">{cleanPath(snap?.current)}</div>
        </Tip>
      </div>

      <div className="mt-3 grid grid-cols-3 divide-x divide-line border-y border-line sm:grid-cols-6">
        <Metric label={t("q.speed")}>{rate(snap?.speed_bps)}</Metric>
        <Metric label={t("q.elapsed")}>{duration((snap?.elapsed_ms ?? 0) / 1000)}</Metric>
        <Metric label={t("q.remaining")}>{duration(snap?.eta_secs)}</Metric>
        <Metric label={t("q.files")}>{num(snap?.files_done ?? 0)}</Metric>
        <Metric label={t("q.output")}>{bytes(snap?.out_bytes ?? 0)}</Metric>
        {saving != null ? (
          <Metric label={t("q.saved")} tone="text-ok">
            {(saving * 100).toFixed(1)}%
          </Metric>
        ) : (
          <Metric label={t("q.threads")}>{job.options.threads}</Metric>
        )}
      </div>

      <div className="mt-4">
        <button type="button" onClick={() => setShowLog((v) => !v)} className="no-press flex items-center gap-1.5 rounded-md py-1 text-[12px] font-medium text-muted hover:text-fg">
          <ChevronRight className={cn("h-3.5 w-3.5 transition-transform duration-[var(--d-med)]", showLog && "rotate-90")} />
          {t("q.log")}
        </button>
        {showLog && (
          <div className="pt-2 [animation:fade-in_.16s_var(--ease)]">
            <LogConsole entries={logs} />
          </div>
        )}
      </div>
    </section>
  );
}

export function RowCard({ job }: { job: JobView }) {
  const t = useT();
  const r = job.report;
  const st = job.status;
  const delta = r ? sizeDelta(r.in_bytes, r.out_bytes) : null;
  const [open, setOpen] = useState(false);
  const logs = useStore((s) => s.logs[job.id]) ?? [];
  const loadLogs = useStore((s) => s.loadLogs);

  const statusChip =
    st === "queued" ? (
      <Chip>{t("st.queued")}</Chip>
    ) : st === "done" ? (
      <Chip color="var(--ok)">{t("st.done")}</Chip>
    ) : st === "failed" ? (
      <Chip color="var(--bad)">{t("st.failed")}</Chip>
    ) : (
      <Chip color="var(--warn)">{t("st.cancelled")}</Chip>
    );

  return (
    <div className="card px-4 py-3 [animation:fade-in_.18s_var(--ease)]">
      <div className="flex items-center gap-3.5">
        <Cover icon={job.icon} size={40} radius={9} />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2.5">
            <h4 className="truncate text-[13.5px] font-semibold">{job.label ?? baseName(job.source)}</h4>
            {statusChip}
          </div>
          <div className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1">
            <Flow job={job} />
            {r && (
              <span className="mono text-[11.5px] text-muted">
                {bytes(r.in_bytes)} → <b className="font-medium text-fg">{bytes(r.out_bytes)}</b>
                {delta && (
                  <Tip content={t(delta.bigger ? "size.bigger" : "size.smaller")}>
                    <span className={cn("ml-1.5 font-semibold", delta.bigger ? "text-bad" : "text-ok")}>{delta.text}</span>
                  </Tip>
                )}{" "}
                · {duration(r.elapsed_ms / 1000)} · {t(ROUTE_KEY[r.route])}
              </span>
            )}
          </div>
          {st === "failed" && job.error && <p className="mt-1.5 text-[12px] leading-snug text-bad">{job.error}</p>}
          {st === "cancelled" && job.error && <p className="mt-1.5 text-[12px] text-muted">{job.error}</p>}
          {r && r.warnings.length > 0 && <p className="mt-1.5 text-[12px] text-warn">{r.warnings.join(" · ")}</p>}
        </div>
        <div className="flex shrink-0 items-center gap-0.5">
          {st === "done" && r && (
            <Tip content={r.console ? t("con.show_on_console") : t("q.show")} side="bottom">
              <Button
                variant="ghost"
                size="sm"
                iconOnly
                icon={<FolderSearch className="h-4 w-4" />}
                onClick={() => (r.console ? useConsole.getState().openFolder(px.parent(r.output)) : void api.reveal(r.output))}
                aria-label={r.console ? t("con.show_on_console") : t("q.show")}
              />
            </Tip>
          )}
          {st === "queued" && (
            <Button variant="ghost" size="sm" onClick={() => void api.cancel(job.id)}>
              {t("q.cancel")}
            </Button>
          )}
          {st !== "queued" && (
            <>
              <Tip content={t("q.log")} side="bottom">
                <Button
                  variant="ghost"
                  size="sm"
                  iconOnly
                  icon={<Terminal className="h-4 w-4" />}
                  onClick={() => {
                    setOpen((v) => !v);
                    void loadLogs(job.id);
                  }}
                  aria-label={t("q.log")}
                />
              </Tip>
              <Tip content={t("q.remove")} side="bottom">
                <Button variant="ghost" size="sm" iconOnly icon={<Trash2 className="h-4 w-4" />} onClick={() => void api.remove(job.id)} aria-label={t("q.remove")} />
              </Tip>
            </>
          )}
        </div>
      </div>
      {open && (
        <div className="pt-3 [animation:fade-in_.16s_var(--ease)]">
          <LogConsole entries={logs} />
        </div>
      )}
    </div>
  );
}
