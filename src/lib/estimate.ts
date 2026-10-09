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

import type { Format, SourceInfo } from "./types";

const MIB = 1024 ** 2;

export const LEVEL_STATS: Record<number, { ratio: number; mibps: number }> = {
  3: { ratio: 0.433, mibps: 67 },
  4: { ratio: 0.428, mibps: 59 },
  5: { ratio: 0.424, mibps: 54 },
  6: { ratio: 0.424, mibps: 47 },
  7: { ratio: 0.421, mibps: 27 },
  8: { ratio: 0.421, mibps: 23 },
  9: { ratio: 0.413, mibps: 17.6 },
};

export function imageEstimate(bytes: number, inner: "exfat" | "ufs2" | "pfs"): number {
  switch (inner) {
    case "exfat":
      return bytes * 1.012 + 80 * MIB;
    case "ufs2":
      return bytes * 1.02 + 90 * MIB;
    case "pfs":
      return bytes * 1.004 + 8 * MIB;
  }
}

export function estimateOutput(info: SourceInfo, target: Format): [number, number] {
  if (target.kind === "folder") return [info.bytes, info.bytes];
  if (target.kind === "pkg") return [info.bytes * 0.8, info.bytes * 1.15];
  const img = imageEstimate(info.bytes, target.inner);
  if (target.kind === "image") return [img, img];

  if (info.wrapper && info.format.kind === "ffpfsc") {
    const w = info.wrapper.file_size;
    return [w * 0.95, w * 1.05];
  }

  return [img * 0.4, img * 0.85];
}
