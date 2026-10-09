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

import { metaOf } from "./formats";
import type { Format, NameMode, SourceInfo } from "./types";

const BAD = /[<>:"/\\|?*\u0000-\u001f]/g;

export function sanitizeName(s: string): string {
  const joined = s.replace(BAD, " ").split(/\s+/).filter(Boolean).join(" ");
  return Array.from(joined.replace(/^[. ]+|[. ]+$/g, ""))
    .slice(0, 120)
    .join("")
    .replace(/[. ]+$/g, "");
}

export function outputBase(info: SourceInfo, mode: NameMode): string {
  const fallback = sanitizeName(info.title.title_id ?? info.path.split(/[\\/]/).filter(Boolean).pop()?.replace(/\.[^.]*$/, "") ?? "game");
  const id = sanitizeName(info.title.title_id ?? "") || null;
  const name = sanitizeName(info.title.title_name ?? "") || null;
  const ver = sanitizeName(info.title.content_version ?? "") || null;
  const idName = id && name ? `${id} ${name}` : (id ?? name ?? fallback);
  let raw: string;
  switch (mode) {
    case "ppsa":
      raw = id ?? fallback;
      break;
    case "ppsa_title":
      raw = idName;
      break;
    case "ppsa_title_version":
      raw = ver ? `${idName} (${ver})` : idName;
      break;
  }
  return sanitizeName(raw) || fallback;
}

export function outputFileName(info: SourceInfo, mode: NameMode, target: Format): string {
  const isFolder = target.kind === "folder";
  return `${outputBase(info, mode)}${isFolder ? "" : metaOf(target).ext}`;
}
