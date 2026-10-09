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

import { FFPFSC_INNERS, sameFormat, targetKey } from "./formats";
import type { ConvertOptions, Format, FsKind, Profile, SectionId, Settings, SourceInfo } from "./types";

export const SECTION_KEYS: Record<SectionId, (keyof ConvertOptions | "outputDir" | "inner" | "dest")[]> = {
  perf: ["threads", "priority"],
  fs: ["inner", "cluster_kib", "free_mib", "preserve_times", "rebuild", "ampr"],
  comp: ["level", "skip_incompressible"],
  out: ["verify", "overwrite", "dest", "outputDir"],
};

export const DEFAULT_INNER: FsKind = "exfat";

export function sectionsFor(f: Format): SectionId[] {
  switch (f.kind) {
    case "folder":
      return ["perf", "out"];
    case "image":
      return ["perf", "fs", "out"];
    case "ffpfsc":
      return ["perf", "fs", "comp", "out"];
    case "pkg":
      return ["perf", "out"];
  }
}

export function defaultsFor(_f: Format): ConvertOptions {
  return {
    threads: 0,
    priority: "low_perf",
    level: 7,
    threshold_gain_pct: 0,
    skip_incompressible: true,
    rebuild: false,
    cluster_kib: 64,
    preserve_times: true,
    free_mib: 0,
    overwrite: false,
    verify: false,
    ampr: "auto",
    name_mode: "ppsa_title",
  };
}

export function emptyProfile(): Profile {
  return { std: { perf: true, fs: true, comp: true, out: true }, custom: {} };
}

export function profileOf(settings: Settings, f: Format): Profile {
  const p = settings.profiles[targetKey(f)];
  if (!p) return emptyProfile();
  return { std: { ...emptyProfile().std, ...p.std }, custom: p.custom ?? {} };
}

export function preferredInner(settings: Settings): FsKind {
  const p = profileOf(settings, { kind: "ffpfsc", inner: DEFAULT_INNER });
  return p.std.fs ? DEFAULT_INNER : (p.custom.inner ?? DEFAULT_INNER);
}

export function resolveTarget(f: Format, info: SourceInfo | null, settings: Settings): Format {
  if (f.kind !== "ffpfsc") return f;
  const want = preferredInner(settings);
  const usable = (inner: FsKind) => !info?.targets.find((x) => sameFormat(x.format, { kind: "ffpfsc", inner }))?.issues.some((i) => i.severity === "error");
  return { kind: "ffpfsc", inner: usable(want) ? want : (FFPFSC_INNERS.find(usable) ?? want) };
}

export function effective(
  settings: Settings,
  f: Format,
  defaultOutDir: string | null,
): { options: ConvertOptions; outputDir: string | null; toConsole: boolean } {
  const base = defaultsFor(f);
  const p = profileOf(settings, f);
  const merged: Record<string, unknown> = { ...base, name_mode: settings.nameMode };
  let outputDir: string | null = defaultOutDir;
  let toConsole = false;
  for (const sec of sectionsFor(f)) {
    if (p.std[sec]) continue;
    for (const k of SECTION_KEYS[sec]) {
      if (k === "outputDir") {
        if (p.custom.outputDir) outputDir = p.custom.outputDir;
      } else if (k === "dest") {
        toConsole = p.custom.dest === "console";
      } else if (k !== "inner" && p.custom[k] !== undefined) {
        merged[k] = p.custom[k];
      }
    }
  }

  if (f.kind === "pkg") toConsole = false;

  if (toConsole) outputDir = settings.console.destDir;
  return { options: merged as unknown as ConvertOptions, outputDir, toConsole };
}
