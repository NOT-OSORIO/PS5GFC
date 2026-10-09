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

import { ChevronUp, ListPlus, Play } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { Popover } from "@/components/ui/Popover";
import { RadioList } from "@/components/ui/RadioList";
import { Tip } from "@/components/ui/Tip";
import { api } from "@/lib/api";
import { useConsole } from "@/lib/consoleStore";
import { estimateOutput } from "@/lib/estimate";
import { innerFileName, sameFormat } from "@/lib/formats";
import type { Key } from "@/lib/i18n";
import { outputFileName } from "@/lib/naming";
import { effective } from "@/lib/options";
import { useStore, useT } from "@/lib/store";
import type { Format, NameMode, SourceInfo } from "@/lib/types";
import { bytes, cn } from "@/lib/utils";
import { issueText } from "./SourceCard";

const NAME_MODES: { value: NameMode; label: Key }[] = [
  { value: "ppsa", label: "f.name_ppsa" },
  { value: "ppsa_title", label: "f.name_ppsa_title" },
  { value: "ppsa_title_version", label: "f.name_ppsa_title_version" },
];

function NamePicker({ info, target }: { info: SourceInfo; target: Format }) {
  const t = useT();
  const nameMode = useStore((s) => s.settings.nameMode);
  const setNameMode = useStore((s) => s.setNameMode);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const close = useCallback(() => setOpen(false), []);

  return (
    <div ref={ref} className="relative min-w-0 flex-1">
      <Tip content={t("act.name_change")} side="top">
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          className={cn(
            "-mx-1.5 flex w-fit max-w-full items-center gap-1.5 rounded-md px-1.5 py-0.5 text-left hover:bg-surface-3",
            open && "bg-surface-3",
          )}
        >
          <span className="mono truncate text-[12.5px] font-semibold">{outputFileName(info, nameMode, target)}</span>
          <ChevronUp className={cn("h-3.5 w-3.5 shrink-0 text-dim transition-transform duration-[var(--d-fast)]", !open && "rotate-180")} />
        </button>
      </Tip>
      <Popover open={open} onClose={close} anchor={ref} align="left" side="top" className="w-[400px] max-w-[calc(100vw-140px)] p-3">
        <div className="mb-2 text-[13px] font-semibold">{t("f.name")}</div>
        <p className="mb-2.5 text-[11.5px] leading-snug text-dim">{t("f.name_tip")}</p>
        <RadioList
          value={nameMode}
          options={NAME_MODES.map((m) => ({ value: m.value, label: t(m.label), example: outputFileName(info, m.value, target) }))}
          onChange={(v) => {
            setNameMode(v);
            close();
          }}
        />
      </Popover>
    </div>
  );
}

export function ActionBar({ info }: { info: SourceInfo }) {
  const t = useT();
  const target = useStore((s) => s.target);
  const settings = useStore((s) => s.settings);
  const defaultOutDir = useStore((s) => s.defaultOutDir);
  const startJob = useStore((s) => s.startJob);
  const [free, setFree] = useState<number | null>(null);
  const consoleStatus = useConsole((s) => s.status);

  const eff = target ? effective(settings, target, defaultOutDir) : null;
  const outputDir = eff?.outputDir ?? null;
  const toConsole = eff?.toConsole ?? false;
  const online = consoleStatus === "connected";
  useEffect(() => {
    if (!outputDir) return;
    if (!toConsole) void api.freeSpace(outputDir).then(setFree);
    else if (online) void api.consoleFree(outputDir).then(setFree).catch(() => setFree(null));
    else setFree(null);
  }, [outputDir, toConsole, online]);

  const tinfo = target ? info.targets.find((x) => sameFormat(x.format, target)) : undefined;
  const blocked = tinfo?.issues.find((i) => i.severity === "error");
  const [lo, hi] = target ? estimateOutput(info, target) : [0, 0];
  const low = free != null && target != null && free < lo;
  const offline = toConsole && !online;
  const disabled = !target || !!blocked || !outputDir || low || offline;
  const why = !target ? t("act.pick_format") : blocked ? issueText(blocked, t) : offline ? t("act.console_offline") : low ? t("act.no_space") : "";

  return (
    <div className="sticky bottom-0 z-20 -mx-6 border-t border-line bg-bg px-6 py-3">
      <div className="mx-auto flex max-w-[1040px] flex-wrap items-center gap-x-6 gap-y-2">
        <div className="min-w-0 flex-1">
          {target ? (
            <>
              <div className="flex items-center gap-2 text-[12.5px]">
                <span className="shrink-0 text-dim">{t("act.output")}</span>
                <NamePicker info={info} target={target} />
                {toConsole && (
                  <Tip content={<span className="mono break-all">{outputDir}</span>}>
                    <span>
                      <Chip color="var(--ember)">→ PS5</Chip>
                    </span>
                  </Tip>
                )}
              </div>
              <div className="mt-0.5 flex flex-wrap items-center gap-x-4 text-[11.5px] text-muted">
                <span>
                  {t("act.estimate")}: <b className="mono font-medium text-fg">{lo === hi ? bytes(lo) : `${bytes(lo)} – ${bytes(hi)}`}</b>
                </span>
                <span className={low ? "text-bad" : ""}>
                  {t("act.free")}: <b className="mono font-medium">{free != null ? bytes(free) : "—"}</b>
                </span>
                {target.kind === "ffpfsc" && (
                  <span>
                    {t("act.inside")}: <b className="mono font-medium text-fg">{innerFileName(info.title.title_id, target.inner)}</b>
                  </span>
                )}
              </div>
            </>
          ) : (
            <span className="text-[12.5px] text-dim">{t("act.pick_format")}</span>
          )}
        </div>
        <div className="flex items-center gap-2">
          <Tip content={why || t("act.queue_tip")}>
            <span>
              <Button variant="secondary" size="lg" disabled={disabled} icon={<ListPlus className="h-4 w-4" />} onClick={() => void startJob(false)}>
                {t("act.queue")}
              </Button>
            </span>
          </Tip>
          <Tip content={why || t("act.convert_tip")}>
            <span>
              <Button variant="primary" size="lg" disabled={disabled} icon={<Play className="h-4 w-4" />} onClick={() => void startJob(true)}>
                {t("act.convert")}
              </Button>
            </span>
          </Tip>
        </div>
      </div>
    </div>
  );
}
