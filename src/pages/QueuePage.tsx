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

import { Inbox, Pause, Play } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Tip } from "@/components/ui/Tip";
import { RowCard, RunningCard } from "@/features/JobCard";
import { api } from "@/lib/api";
import { useStore, useT } from "@/lib/store";

export function QueuePage() {
  const t = useT();
  const jobs = useStore((s) => s.jobs);
  const queueRunning = useStore((s) => s.queueRunning);
  const startQueue = useStore((s) => s.startQueue);
  const pauseQueue = useStore((s) => s.pauseQueue);
  const running = jobs.filter((j) => j.status === "running");
  const queued = jobs.filter((j) => j.status === "queued");
  const finished = jobs.filter((j) => !["queued", "running"].includes(j.status)).sort((a, b) => (b.finished_ms ?? 0) - (a.finished_ms ?? 0));

  return (
    <div className="px-6 pb-8 pt-5">
      <div className="mx-auto max-w-[1040px]">
        {(queued.length > 0 || running.length > 0) && (
          <div className="mb-6 flex items-center justify-between gap-4 rounded-xl border border-line bg-surface px-4 py-3">
            <p className="text-[12.5px] text-muted">{queueRunning ? "" : queued.length > 0 ? t("q.start_hint") : ""}</p>
            {queueRunning ? (
              <Tip content={t("q.pause_tip")} side="left">
                <Button variant="secondary" icon={<Pause className="h-4 w-4" />} onClick={() => void pauseQueue()}>
                  {t("q.pause")}
                </Button>
              </Tip>
            ) : (
              <Button variant="primary" icon={<Play className="h-4 w-4" />} disabled={queued.length === 0} onClick={() => void startQueue()}>
                {t("q.start")}
              </Button>
            )}
          </div>
        )}

        {jobs.length === 0 ? (
          <div className="grid place-items-center py-24 text-center text-dim">
            <Inbox className="mb-3 h-8 w-8" strokeWidth={1.5} />
            <p className="text-[13px]">{t("q.empty")}</p>
          </div>
        ) : (
          <div className="space-y-7">
            {running.length > 0 && (
              <section className="space-y-3">
                <h2 className="eyebrow">{t("q.running")}</h2>
                {running.map((j) => (
                  <RunningCard key={j.id} job={j} />
                ))}
              </section>
            )}
            {queued.length > 0 && (
              <section className="space-y-2">
                <h2 className="eyebrow">
                  {t("q.queued")} · {queued.length}
                </h2>
                {queued.map((j) => (
                  <RowCard key={j.id} job={j} />
                ))}
              </section>
            )}
            {finished.length > 0 && (
              <section className="space-y-2">
                <div className="flex items-center justify-between">
                  <h2 className="eyebrow">{t("q.finished")}</h2>
                  <Button variant="ghost" size="sm" onClick={() => void api.clearFinished()}>
                    {t("q.clear")}
                  </Button>
                </div>
                {finished.map((j) => (
                  <RowCard key={j.id} job={j} />
                ))}
              </section>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
