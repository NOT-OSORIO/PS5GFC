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

import { ChevronDown, Copy, Download, FolderInput, ListChecks, Loader2, Repeat2, Scissors, Trash2, Upload, X, Archive, PackageOpen, type LucideIcon } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { ProgressBar } from "@/components/ui/Progress";
import { Tip } from "@/components/ui/Tip";
import { api } from "@/lib/api";
import { px, type ConsoleJob, type TransferView } from "@/lib/console";
import { jobKey, useConsole } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { baseName, bytes, cn, duration, num, pct, pctText, rate } from "@/lib/utils";

const FINISHED = ["done", "attention", "canceled", "error"];

function Row({ icon: Icon, title, sub, children, actions, tone }: { icon: LucideIcon; title: string; sub?: string; children?: React.ReactNode; actions?: React.ReactNode; tone?: string }) {
  return (
    <div className="flex items-center gap-3 border-b border-line px-5 py-2.5 last:border-b-0">
      <span className={cn("grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-surface-3 text-muted", tone)}>
        <Icon className="h-4 w-4" strokeWidth={1.8} />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-2">
          <Tip content={title} side="top" className="break-all">
            <span className="truncate text-[12.5px] font-medium">{title}</span>
          </Tip>
        </div>
        {sub && (
          <Tip content={<span className="mono break-all">{sub}</span>} side="bottom" align="start">
            <div className="mono truncate text-[11px] text-dim">{sub}</div>
          </Tip>
        )}
        {children}
      </div>
      <div className="flex shrink-0 items-center gap-1">{actions}</div>
    </div>
  );
}

function TransferRow({ tr: x }: { tr: TransferView }) {
  const t = useT();
  const snap = useConsole((s) => s.tProgress[x.id]) ?? x.snapshot;
  const remove = (id: number) => void api.transferRemove(id);
  const cancel = (id: number) => void api.transferCancel(id);
  const first = x.kind === "upload" ? baseName(x.items[0] ?? "") : px.base(x.items[0] ?? "");
  const title = x.items.length > 1 ? `${first} +${x.items.length - 1}` : first;
  const running = x.status === "running";
  const p = snap ? pct(snap.done, snap.total) : 0;
  const scanning = !snap || snap.stage === "scanning" || !snap.total;
  const dest = x.kind === "upload" ? `→ ${x.dest}` : `→ ${x.dest}`;

  return (
    <Row
      icon={x.kind === "upload" ? Upload : Download}
      title={title}
      sub={dest}
      tone={running ? "text-ember" : undefined}
      actions={
        x.status === "queued" || running ? (
          <Button size="sm" variant="ghost" onClick={() => cancel(x.id)}>
            {t("q.cancel")}
          </Button>
        ) : (
          <Tip content={t("q.remove")} side="left">
            <Button size="sm" variant="ghost" iconOnly icon={<X className="h-3.5 w-3.5" />} onClick={() => remove(x.id)} aria-label={t("q.remove")} />
          </Tip>
        )
      }
    >
      {running && (
        <div className="mt-1.5">
          <ProgressBar value={p} indeterminate={scanning} />
          <div className="mono mt-1 flex flex-wrap items-center gap-x-3 text-[11px] text-muted">
            {!scanning && <b className="font-semibold text-fg">{pctText(p)}%</b>}
            <span>
              {bytes(snap?.done ?? 0)} {t("q.of")} {bytes(snap?.total ?? 0)}
            </span>
            <span>{rate(snap?.speed_bps)}</span>
            <span>{duration(snap?.eta_secs)}</span>
            {!!snap?.files_total && (
              <span>
                {num(snap.files_done)}/{num(snap.files_total)} {t("q.files").toLowerCase()}
              </span>
            )}
          </div>
        </div>
      )}
      {x.status === "queued" && <div className="mt-0.5 text-[11.5px] text-dim">{t("st.queued")}</div>}
      {x.status === "done" && x.result && (
        <div className="mt-0.5 flex items-center gap-2 text-[11.5px] text-muted">
          <Chip color="var(--ok)">{t("st.done")}</Chip>
          <span className="mono">
            {t("con.t_files", { n: x.result.files })} · {bytes(x.result.bytes)}
            {x.result.skipped ? ` · ${t("con.t_skipped", { n: x.result.skipped })}` : ""}
          </span>
        </div>
      )}
      {x.status === "failed" && <div className="mt-0.5 text-[11.5px] leading-snug text-bad">{x.error}</div>}
      {x.status === "cancelled" && <div className="mt-0.5 text-[11.5px] text-muted">{x.error ?? t("st.cancelled")}</div>}
    </Row>
  );
}

const JOB_ICON: Record<string, LucideIcon> = { copy: Copy, move: Scissors, delete: Trash2, zip: Archive, unzip: PackageOpen };

function JobRow({ job }: { job: ConsoleJob }) {
  const t = useT();
  const cancelJob = useConsole((s) => s.cancelJob);
  const running = !FINISHED.includes(job.state);
  const p = job.total > 0 ? pct(job.completed, job.total) : 0;
  const failed = job.state === "error" || job.state === "attention";
  const name = baseName(job.source.split("\n")[0]) || px.base(job.destination);
  return (
    <Row
      icon={JOB_ICON[job.type] ?? FolderInput}
      title={`${t(jobKey(job.type))} · ${name}`}
      sub={job.type === "delete" ? job.source : `${job.source.split("\n")[0]} → ${job.destination}`}
      tone={running ? "text-ember" : undefined}
      actions={
        running ? (
          <Button size="sm" variant="ghost" onClick={() => void cancelJob(job.id)}>
            {t("q.cancel")}
          </Button>
        ) : null
      }
    >
      {running ? (
        <div className="mt-1.5">
          <ProgressBar value={p} indeterminate={job.total === 0} />
          <div className="mono mt-1 flex gap-3 text-[11px] text-muted">
            {job.total > 0 && <b className="font-semibold text-fg">{pctText(p)}%</b>}
            <span>{job.total > 0 ? `${bytes(job.completed)} ${t("q.of")} ${bytes(job.total)}` : t("con.job_working")}</span>
            <span className="text-dim">{t("con.on_console")}</span>
          </div>
        </div>
      ) : failed ? (
        <div className="mt-0.5 text-[11.5px] leading-snug text-bad">{job.error || job.error_code}</div>
      ) : (
        <div className="mt-0.5">
          <Chip color={job.state === "canceled" ? "var(--warn)" : "var(--ok)"}>{job.state === "canceled" ? t("st.cancelled") : t("st.done")}</Chip>
        </div>
      )}
    </Row>
  );
}

export function TransferDock() {
  const t = useT();
  const transfers = useConsole((s) => s.transfers);
  const jobs = useConsole((s) => s.jobs);
  const clearJobs = useConsole((s) => s.clearJobs);
  const convs = useStore((s) => s.jobs).filter((j) => j.console && (j.status === "running" || j.status === "queued"));
  const progress = useStore((s) => s.progress);
  const setPage = useStore((s) => s.setPage);
  const [open, setOpen] = useState(false);

  const active = transfers.filter((x) => x.status === "running" || x.status === "queued").length + jobs.filter((j) => !FINISHED.includes(j.state)).length + convs.length;
  const prev = useRef(active);
  useEffect(() => {
    if (active > prev.current) setOpen(true);
    prev.current = active;
  }, [active]);

  const finished = transfers.filter((x) => !["queued", "running"].includes(x.status)).length + jobs.filter((j) => FINISHED.includes(j.state)).length;
  if (!transfers.length && !jobs.length && !convs.length) return null;

  return (
    <section className="shrink-0 border-t border-line bg-surface">
      <div className="flex items-center gap-2 px-5 py-2">
        <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} className="no-press flex min-w-0 flex-1 items-center gap-2 text-left">
          <ChevronDown className={cn("h-4 w-4 text-dim transition-transform duration-[var(--d-med)]", !open && "-rotate-90")} />
          <span className="eyebrow">{t("con.transfers")}</span>
          {active > 0 ? (
            <span className="flex items-center gap-1.5 text-[11.5px] text-ember">
              <Loader2 className="spin h-3 w-3" />
              {t("con.n_active", { n: active })}
            </span>
          ) : (
            <span className="text-[11.5px] text-dim">{t("con.all_done")}</span>
          )}
        </button>
        {finished > 0 && (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              void api.transferClear();
              void clearJobs();
            }}
          >
            {t("q.clear")}
          </Button>
        )}
      </div>
      {open && (
        <div className="max-h-[188px] overflow-y-auto border-t border-line">
          {convs.map((j) => {
            const snap = progress[j.id] ?? j.snapshot;
            const p = snap ? pct(snap.done, snap.total) : 0;
            return (
              <Row
                key={`c${j.id}`}
                icon={Repeat2}
                tone="text-ember"
                title={`${t("nav.convert")} → PS5 · ${j.label ?? baseName(j.source)}`}
                sub={j.dest}
                actions={
                  <Button size="sm" variant="ghost" icon={<ListChecks className="h-3.5 w-3.5" />} onClick={() => setPage("queue")}>
                    {t("nav.queue")}
                  </Button>
                }
              >
                <div className="mt-1.5">
                  <ProgressBar value={p} indeterminate={!snap?.total || snap.stage === "scanning"} />
                  <div className="mono mt-1 flex flex-wrap gap-x-3 text-[11px] text-muted">
                    {j.status === "queued" ? t("st.queued") : <b className="font-semibold text-fg">{pctText(p)}%</b>}
                    {snap && j.status === "running" && (
                      <>
                        <span>{rate(snap.speed_bps)}</span>
                        <span>{duration(snap.eta_secs)}</span>
                      </>
                    )}
                  </div>
                </div>
              </Row>
            );
          })}
          {transfers.map((x) => (
            <TransferRow key={x.id} tr={x} />
          ))}
          {jobs.map((j) => (
            <JobRow key={`j${j.id}`} job={j} />
          ))}
        </div>
      )}
    </section>
  );
}
