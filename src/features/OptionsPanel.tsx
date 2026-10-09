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

import { Loader2, PlugZap } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { Button } from "@/components/ui/Button";
import { Field } from "@/components/ui/Field";
import { Section } from "@/components/ui/Section";
import { Segmented } from "@/components/ui/Segmented";
import { Slider } from "@/components/ui/Slider";
import { Switch } from "@/components/ui/Switch";
import { Tip } from "@/components/ui/Tip";
import { FolderPickerModal } from "@/features/console/FolderPicker";
import { api } from "@/lib/api";
import { useConsole } from "@/lib/consoleStore";
import { FFPFSC_INNERS, INNER_LABEL, innerFileName, sameFormat } from "@/lib/formats";
import { effective, profileOf } from "@/lib/options";
import { useStore, useT } from "@/lib/store";
import type { FsKind, SectionId, SourceInfo } from "@/lib/types";
import { bytes, cn } from "@/lib/utils";
import { issueText } from "./SourceCard";

export function OptionsPanel({ info }: { info: SourceInfo }) {
  const t = useT();
  const target = useStore((s) => s.target);
  const settings = useStore((s) => s.settings);
  const defaultOutDir = useStore((s) => s.defaultOutDir);
  const app = useStore((s) => s.app);
  const setStd = useStore((s) => s.setStd);
  const patchCustom = useStore((s) => s.patchCustom);
  const [free, setFree] = useState<number | null>(null);
  const [pickConsole, setPickConsole] = useState(false);
  const consoleStatus = useConsole((s) => s.status);
  const connectConsole = useConsole((s) => s.connect);
  const setPage = useStore((s) => s.setPage);
  const patchConsole = useStore((s) => s.patchConsole);

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

  if (!target || !eff) return null;
  const o = eff.options;
  const prof = profileOf(settings, target);
  const tinfo = info.targets.find((x) => sameFormat(x.format, target));
  const inner = target.kind === "image" || target.kind === "ffpfsc" ? target.inner : null;
  const isPkg = target.kind === "pkg";
  const cpus = app?.logical_cpus ?? 8;
  const threads = o.threads || app?.default_threads || Math.max(1, cpus - 1);

  const presets = [
    { value: 3, label: t("f.fast"), tip: t("f.fast_tip") },
    { value: 7, label: t("f.balanced"), tip: t("f.balanced_tip") },
    { value: 9, label: t("f.max"), tip: t("f.max_tip") },
  ];
  const isPreset = presets.some((p) => p.value === o.level);

  const innerOptions = FFPFSC_INNERS.map((k) => {
    const err = info.targets.find((x) => sameFormat(x.format, { kind: "ffpfsc", inner: k }))?.issues.find((i) => i.severity === "error");
    return {
      value: k as FsKind,
      label: INNER_LABEL[k],
      tip: err ? issueText(err, t) : t(INNER_TIP[k]),
      disabled: !!err,
    };
  });

  async function chooseDir() {
    if (toConsole) {
      if (online) setPickConsole(true);
      return;
    }
    const p = await api.pickFolder("");
    if (p) patchCustom("out", { outputDir: p });
  }

  const rows: SectionId[][] =
    target.kind === "folder" || target.kind === "pkg"
      ? [["perf"], ["out"]]
      : target.kind === "image"
        ? [["perf"], ["fs", "out"]]
        : [
            ["perf", "comp"],
            ["fs", "out"],
          ];
  const isWide = (id: SectionId) => rows.some((r) => r.length === 1 && r[0] === id);

  const blocks: Record<SectionId, ReactNode> = {
    perf: (
      <Section title={t("sec.perf")} std={prof.std.perf} onStd={(v) => setStd("perf", v)} wide={isWide("perf")}>
        <Field label={`${t("f.threads")}: ${threads} / ${cpus}`} tip={t("f.threads_tip")}>
          <div className="w-[220px]">
            <Slider value={threads} min={1} max={cpus} onChange={(v) => patchCustom("perf", { threads: v })} />
          </div>
        </Field>
        <Field label={t("f.priority")} tip={t("f.priority_tip")}>
          <Segmented
            value={o.priority === "normal" ? "normal" : "low_perf"}
            options={[
              { value: "low_perf", label: t("f.prio_balanced") },
              { value: "normal", label: t("f.prio_max") },
            ]}
            onChange={(v) => patchCustom("perf", { priority: v })}
          />
        </Field>
      </Section>
    ),
    fs: (
      <Section title={t("sec.fs")} std={prof.std.fs} onStd={(v) => setStd("fs", v)} wide={isWide("fs")}>
        {target.kind === "ffpfsc" && inner && (
          <Field label={t("f.inner")} tip={t("f.inner_tip")} stack>
            <Segmented value={inner} options={innerOptions} onChange={(v) => patchCustom("fs", { inner: v })} />
            <div className="mono truncate text-[11px] text-dim">{t("f.inner_file", { name: innerFileName(info.title.title_id, inner) })}</div>
          </Field>
        )}
        {inner === "exfat" && (
          <Field label={t("f.cluster")} tip={t("f.cluster_tip")}>
            <Segmented
              value={o.cluster_kib}
              options={[
                { value: 32, label: "32 KiB" },
                { value: 64, label: "64 KiB" },
              ]}
              onChange={(v) => patchCustom("fs", { cluster_kib: v })}
            />
          </Field>
        )}
        {inner !== "pfs" && (
          <Field label={t("f.free")} tip={t("f.free_tip")}>
            <Segmented
              value={o.free_mib}
              options={[
                { value: 0, label: t("f.none") },
                { value: 256, label: "256 MiB" },
                { value: 1024, label: "1 GiB" },
                { value: 4096, label: "4 GiB" },
              ]}
              onChange={(v) => patchCustom("fs", { free_mib: v })}
            />
          </Field>
        )}
        <Field label={t("f.times")} tip={t("f.times_tip")}>
          <Switch checked={o.preserve_times} onCheckedChange={(v) => patchCustom("fs", { preserve_times: v })} />
        </Field>
        {tinfo?.route === "reuse_image" && (
          <Field label={t("f.rebuild")} tip={t("f.rebuild_tip")}>
            <Switch checked={o.rebuild} onCheckedChange={(v) => patchCustom("fs", { rebuild: v })} />
          </Field>
        )}
        {info.title.ampr && (
          <Field label={t("f.ampr")} tip={t("f.ampr_tip")}>
            <Segmented
              value={o.ampr}
              options={[
                { value: "auto", label: t("f.ampr_auto") },
                { value: "always", label: t("f.ampr_always") },
                { value: "never", label: t("f.ampr_never") },
              ]}
              onChange={(v) => patchCustom("fs", { ampr: v })}
            />
          </Field>
        )}
      </Section>
    ),
    comp: (
      <Section title={t("sec.comp")} std={prof.std.comp} onStd={(v) => setStd("comp", v)} wide={isWide("comp")}>
        <Field label={t("f.profile")} tip={t("f.profile_tip")}>
          <Segmented
            value={isPreset ? o.level : -1}
            options={[...presets, ...(isPreset ? [] : [{ value: -1, label: t("f.custom") }])]}
            onChange={(v) => v > 0 && patchCustom("comp", { level: v })}
          />
        </Field>
        <Field label={`${t("f.level")}: ${o.level}`} tip={t("f.level_tip")}>
          <div className="w-[220px]">
            <Slider value={o.level} min={3} max={9} onChange={(v) => patchCustom("comp", { level: v })} />
          </div>
        </Field>
        <Field label={t("f.skip")} tip={t("f.skip_tip")}>
          <Switch checked={o.skip_incompressible} onCheckedChange={(v) => patchCustom("comp", { skip_incompressible: v })} />
        </Field>
      </Section>
    ),
    out: (
      <Section title={t("sec.out")} std={prof.std.out} onStd={(v) => setStd("out", v)} wide={isWide("out")}>
        {!isPkg && (
          <div className={isWide("out") ? "lg:col-span-2" : undefined}>
            <Field label={t("f.where")} tip={t("f.where_tip")}>
              <Segmented
                value={toConsole ? "console" : "pc"}
                options={[
                  { value: "pc", label: "PC" },
                  { value: "console", label: "PS5" },
                ]}
                onChange={(v) => patchCustom("out", { dest: v })}
              />
            </Field>
          </div>
        )}
        <div className={isWide("out") ? "lg:col-span-2" : undefined}>
          <Field label={toConsole ? t("f.dest_console") : t("f.dest")} tip={toConsole ? t("f.dest_console_tip") : t("f.dest_tip")} stack>
            <div className="flex items-center gap-2">
              <Tip content={outputDir} side="top" className="mono break-all">
                <button
                  type="button"
                  onClick={chooseDir}
                  className="mono min-w-0 flex-1 truncate rounded-lg border border-line bg-surface-2 px-3 py-2 text-left text-[12px] hover:border-line-strong"
                >
                  {outputDir ?? "—"}
                </button>
              </Tip>
              <Button variant="secondary" size="md" onClick={chooseDir} disabled={toConsole && !online}>
                {t("f.change")}
              </Button>
            </div>
            {toConsole && (
              <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px]">
                {online ? (
                  <span className="text-ok">
                    {t("con.connected_to", { host: settings.console.host })}
                  </span>
                ) : consoleStatus === "connecting" ? (
                  <span className="flex items-center gap-1.5 text-muted">
                    <Loader2 className="spin h-3 w-3" /> {t("con.connecting")}
                  </span>
                ) : (
                  <>
                    <span className="text-warn">{t("act.console_offline")}</span>
                    <Button
                      size="sm"
                      variant="secondary"
                      icon={<PlugZap className="h-3.5 w-3.5" />}
                      onClick={() => (settings.console.host ? void connectConsole() : setPage("console"))}
                    >
                      {settings.console.host ? t("con.connect") : t("con.set_up")}
                    </Button>
                  </>
                )}
              </div>
            )}
            <div className="mt-1 text-[11.5px] text-dim">
              {t("act.free")}: <span className="mono text-muted">{free != null ? bytes(free) : "—"}</span>
              {isPkg && <span> · {t("f.pkg_pc_only")}</span>}
            </div>
          </Field>
        </div>
        <Field label={t("f.verify")} tip={isPkg ? t("f.verify_pkg_tip") : t("f.verify_tip")}>
          <Switch checked={o.verify} onCheckedChange={(v) => patchCustom("out", { verify: v })} />
        </Field>
        <Field label={t("f.overwrite")} tip={t("f.overwrite_tip")}>
          <Switch checked={o.overwrite} onCheckedChange={(v) => patchCustom("out", { overwrite: v })} />
        </Field>
      </Section>
    ),
  };

  return (
    <div className="space-y-3">
      <FolderPickerModal
        open={pickConsole}
        title={t("f.dest_console")}
        start={settings.console.destDir}
        confirm={t("con.choose_folder")}
        onPick={(p) => {
          setPickConsole(false);
          if (p) patchConsole({ destDir: p });
        }}
      />
      {rows.map((row) => (
        <div key={row.join()} className="grid gap-3 lg:grid-cols-2">
          {row.map((id) => (
            <div key={id} className={cn("min-w-0", row.length === 1 && "lg:col-span-2")}>
              {blocks[id]}
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

const INNER_TIP = {
  exfat: "inner.exfat.tip",
  ufs2: "inner.ufs2.tip",
  pfs: "inner.pfs.tip",
} as const;
