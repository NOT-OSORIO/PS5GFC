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

import { Boxes, FileArchive, FolderOpen, HardDrive, Layers, Package, type LucideIcon } from "lucide-react";
import type { Key } from "./i18n";
import type { Format, FsKind } from "./types";

export interface FormatMeta {
  id: string;

  title: string;
  sub?: string;
  ext: string;
  color: string;
  icon: LucideIcon;
  tip: Key;
}

const FOLDER: FormatMeta = { id: "folder", title: "", ext: "", color: "var(--fmt-folder)", icon: FolderOpen, tip: "fmt.folder.tip" };
const EXFAT: FormatMeta = { id: "image-exfat", title: "exFAT", ext: ".exfat", color: "var(--fmt-exfat)", icon: HardDrive, tip: "fmt.exfat.tip" };
const UFS2: FormatMeta = { id: "image-ufs2", title: "FFPKG", sub: "UFS2", ext: ".ffpkg", color: "var(--fmt-ufs2)", icon: Boxes, tip: "fmt.ufs2.tip" };
const PKG: FormatMeta = { id: "pkg", title: "PKG", sub: "FPKG", ext: ".pkg", color: "var(--fmt-pkg)", icon: Package, tip: "fmt.pkg.tip" };

const PFS: FormatMeta = { id: "image-pfs", title: "FFPFS", sub: "PFS", ext: ".ffpfs", color: "var(--fmt-pfsc)", icon: Layers, tip: "fmt.exfat.tip" };

export const FFPFSC_INNERS: FsKind[] = ["exfat", "ufs2", "pfs"];

export const INNER_LABEL: Record<FsKind, string> = { exfat: "exFAT", ufs2: "UFS2", pfs: "PFS" };

const FFPFSC = (inner: FsKind): FormatMeta => ({
  id: "ffpfsc",
  title: "FFPFSC",
  sub: INNER_LABEL[inner],
  ext: ".ffpfsc",
  color: "var(--fmt-pfsc)",
  icon: FileArchive,
  tip: "fmt.ffpfsc.tip",
});

export function metaOf(f: Format): FormatMeta {
  switch (f.kind) {
    case "folder":
      return FOLDER;
    case "image":
      return f.inner === "exfat" ? EXFAT : f.inner === "ufs2" ? UFS2 : PFS;
    case "ffpfsc":
      return FFPFSC(f.inner);
    case "pkg":
      return PKG;
  }
}

export function sameFormat(a: Format | null, b: Format | null): boolean {
  if (!a || !b) return false;
  if (a.kind !== b.kind) return false;
  return a.kind === "folder" || a.kind === "pkg" || (a as { inner: FsKind }).inner === (b as { inner: FsKind }).inner;
}

export function sameTile(a: Format | null, b: Format | null): boolean {
  if (!a || !b) return false;
  return a.kind === "ffpfsc" && b.kind === "ffpfsc" ? true : sameFormat(a, b);
}

export function innerFileName(titleId: string | null, inner: FsKind): string {
  if (inner === "pfs") return "pfs_image.dat";
  return `${titleId ?? "IMAGE"}.${inner === "exfat" ? "exfat" : "ffpkg"}`;
}

export function targetKey(f: Format): string {
  return metaOf(f).id;
}

export const TARGETS: Format[] = [
  { kind: "folder" },
  { kind: "image", inner: "exfat" },
  { kind: "image", inner: "ufs2" },
  { kind: "ffpfsc", inner: "exfat" },
  { kind: "pkg" },
];

export function fileNameFor(stem: string, f: Format): string {
  return f.kind === "folder" ? stem : `${stem}${metaOf(f).ext}`;
}

export function displayName(f: Format, folderLabel: string): string {
  const m = metaOf(f);
  if (f.kind === "folder") return folderLabel;
  return m.sub ? `${m.title} · ${m.sub}` : m.title;
}
