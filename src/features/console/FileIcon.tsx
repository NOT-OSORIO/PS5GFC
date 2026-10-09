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

import { Boxes, Cpu, File, FileArchive, FileCode, FileText, Film, Folder, HardDrive, Image, Layers, Music, Package, type LucideIcon } from "lucide-react";
import { fileKind, type FileKind, type RemoteEntry } from "@/lib/console";
import { cn } from "@/lib/utils";

const KINDS: Record<FileKind, { icon: LucideIcon; color: string }> = {
  folder: { icon: Folder, color: "var(--fmt-folder)" },
  exfat: { icon: HardDrive, color: "var(--fmt-exfat)" },
  ffpkg: { icon: Boxes, color: "var(--fmt-ufs2)" },
  ffpfsc: { icon: FileArchive, color: "var(--fmt-pfsc)" },
  ffpfs: { icon: Layers, color: "var(--fmt-pfsc)" },
  archive: { icon: FileArchive, color: "var(--fg-muted)" },
  exec: { icon: Cpu, color: "var(--info)" },
  pkg: { icon: Package, color: "var(--ember)" },
  image: { icon: Image, color: "var(--fg-muted)" },
  audio: { icon: Music, color: "var(--fg-muted)" },
  video: { icon: Film, color: "var(--fg-muted)" },
  text: { icon: FileText, color: "var(--fg-muted)" },
  config: { icon: FileCode, color: "var(--fg-muted)" },
  file: { icon: File, color: "var(--fg-dim)" },
};

export function FileIcon({ entry, className }: { entry: Pick<RemoteEntry, "name" | "is_dir">; className?: string }) {
  const k = KINDS[fileKind(entry)];
  const Icon = k.icon;
  return <Icon className={cn("h-[17px] w-[17px] shrink-0", className)} style={{ color: k.color }} strokeWidth={entry.is_dir ? 1.9 : 1.7} />;
}
