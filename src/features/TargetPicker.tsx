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

import { AlertTriangle, Check } from "lucide-react";
import { Chip } from "@/components/ui/Chip";
import { Tip } from "@/components/ui/Tip";
import { displayName, FFPFSC_INNERS, metaOf, sameFormat, sameTile, TARGETS } from "@/lib/formats";
import type { Key } from "@/lib/i18n";
import { resolveTarget } from "@/lib/options";
import { useStore, useT } from "@/lib/store";
import type { Format, Issue, Route, SourceInfo, TargetInfo } from "@/lib/types";
import { cn } from "@/lib/utils";
import { issueText } from "./SourceCard";

const ROUTE_LABEL: Record<Route, Key> = { extract: "route.extract", reuse_image: "route.direct", build: "route.rebuild" };
const ROUTE_TIP: Record<Route, Key> = { extract: "route.extract_tip", reuse_image: "route.direct_tip", build: "route.rebuild_tip" };

const COLS: Record<number, string> = { 1: "lg:grid-cols-1", 2: "lg:grid-cols-2", 3: "lg:grid-cols-3", 4: "lg:grid-cols-4", 5: "lg:grid-cols-5" };

function Tile({
  format,
  info,
  blocked,
  selected,
  isSource,
  onSelect,
}: {
  format: Format;
  info: TargetInfo | undefined;

  blocked: Issue | undefined;
  selected: boolean;
  isSource: boolean;
  onSelect: () => void;
}) {
  const t = useT();
  const meta = metaOf(format);
  const Icon = meta.icon;
  const warn = info?.issues.find((i) => i.severity === "warn");
  const name = format.kind === "folder" ? t("fmt.folder") : meta.title;
  return (
    <Tip content={blocked ? <span className="text-bad">{issueText(blocked, t)}</span> : t(meta.tip)} side="bottom">
      <button
        type="button"
        disabled={!!blocked}
        onClick={onSelect}
        aria-pressed={selected}
        className={cn(
          "relative flex flex-col rounded-xl border p-3.5 text-left",
          selected ? "border-ember bg-[var(--ember-soft)]" : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
          blocked && "cursor-not-allowed opacity-45 hover:border-line hover:bg-surface",
        )}
      >
        <div className="flex items-center justify-between">
          <span className="grid h-8 w-8 place-items-center rounded-lg" style={{ background: `color-mix(in srgb, ${meta.color} 14%, transparent)`, color: meta.color }}>
            <Icon className="h-4 w-4" strokeWidth={2.1} />
          </span>
          <span className={cn("grid h-5 w-5 place-items-center rounded-full bg-ember text-[var(--on-ember)] transition-all duration-[var(--d-med)]", selected ? "scale-100 opacity-100" : "scale-75 opacity-0")}>
            <Check className="h-3 w-3" strokeWidth={3.2} />
          </span>
        </div>
        <div className="mt-2.5 flex items-baseline gap-1.5">
          <span className="text-[14px] font-semibold">{name}</span>
          {meta.sub && <span className="text-[11.5px] font-medium text-muted">{meta.sub}</span>}
        </div>
        <div className="mono text-[11px]" style={{ color: meta.color }}>
          {meta.ext || "—"}
        </div>
        <div className="mt-2.5 flex flex-wrap items-center gap-1.5">
          {info && (
            <Tip content={t(ROUTE_TIP[info.route])}>
              <span>
                <Chip className={cn("!text-[10.5px]", info.route === "reuse_image" && "!text-ok")}>{t(ROUTE_LABEL[info.route])}</Chip>
              </span>
            </Tip>
          )}
          {isSource && <Chip className="!text-[10.5px]">{t("dest.source_chip")}</Chip>}
          {warn && (
            <Tip content={issueText(warn, t)}>
              <span className="text-warn">
                <AlertTriangle className="h-3.5 w-3.5" />
              </span>
            </Tip>
          )}
        </div>
      </button>
    </Tip>
  );
}

export function routeNote(route: Route, hasWrapper: boolean, target: Format): Key {
  if (target.kind === "pkg") return "note.pkg";
  if (route === "extract") return "note.extract";
  if (route === "build") return "note.build";
  const toPfsc = target.kind === "ffpfsc";
  return hasWrapper ? (toPfsc ? "note.rewrap" : "note.unwrap") : toPfsc ? "note.wrap" : "note.copy";
}

export function TargetPicker({ info }: { info: SourceInfo }) {
  const t = useT();
  const target = useStore((s) => s.target);
  const settings = useStore((s) => s.settings);
  const setTarget = useStore((s) => s.setTarget);
  const infoOf = (f: Format) => info.targets.find((x) => sameFormat(x.format, f));
  const errorOf = (f: Format) => infoOf(f)?.issues.find((i) => i.severity === "error");
  const sel = target ? infoOf(target) : undefined;

  const blockedOf = (f: Format) => (f.kind === "ffpfsc" ? (FFPFSC_INNERS.every((inner) => errorOf({ kind: "ffpfsc", inner })) ? errorOf({ kind: "ffpfsc", inner: "exfat" }) : undefined) : errorOf(f));

  const tiles = TARGETS.filter((f) => !(sameTile(info.format, f) && blockedOf(f)));

  return (
    <section>
      <div className="mb-2.5 flex items-baseline justify-between">
        <h2 className="eyebrow">{t("dest.title")}</h2>
        {target && sel && (
          <span className="flex items-center gap-1.5 text-[12px] text-muted">
            <span className="text-fg">{displayName(info.format, t("fmt.folder"))}</span>
            <span className="text-dim">→</span>
            <span className="text-fg">{displayName(target, t("fmt.folder"))}</span>
          </span>
        )}
      </div>
      <div className={cn("grid grid-cols-2 gap-2.5", COLS[tiles.length] ?? "lg:grid-cols-4")}>
        {tiles.map((f) => {
          const shown = resolveTarget(f, info, settings);
          return (
            <Tile key={metaOf(f).id} format={shown} info={infoOf(shown)} blocked={blockedOf(f)} selected={sameTile(target, f)} isSource={sameTile(info.format, f)} onSelect={() => setTarget(f)} />
          );
        })}
      </div>
      {target && sel && (
        <p className="mt-2.5 text-[12px] leading-snug text-muted [animation:fade-in_.16s_var(--ease)]">
          {t(routeNote(sel.route, !!info.wrapper, target))}
          {info.title.ampr_packs ? ` ${t("note.ampr_packs")}` : info.title.ampr && sel.route !== "reuse_image" ? ` ${t("note.ampr")}` : ""}
        </p>
      )}
    </section>
  );
}
