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

import type { Key } from "./i18n";

export interface Credit {

  author: string;

  project: string;

  role: Key;
  url: string;
}

export const CREDITS: Credit[] = [
  { author: "pearlxcore", project: "PS5 PKG Tool", role: "cr.pkgtool", url: "https://github.com/pearlxcore/PS5PkgTool" },
  { author: "PSBrew", project: "MkPFS", role: "cr.mkpfs", url: "https://github.com/PSBrew/MkPFS" },
  { author: "DecKerr97", project: "exFAT Image Builder", role: "cr.exfat", url: "https://github.com/kerrdec97/ps5-exfat-builder" },
  { author: "bizkut", project: "PS5 FFPFS-CLI", role: "cr.ffpfs", url: "https://github.com/bizkut/ps5-ffpfs-cli" },
  { author: "KINGDKAK", project: "PS5 FFPFSC PRO", role: "cr.pro", url: "https://github.com/KINGDKAK/PS5-FFPFSC-PRO" },
  { author: "SvenGDK", project: "UFS2Tool", role: "cr.ufs2", url: "https://github.com/SvenGDK/UFS2Tool" },
  { author: "notmaj0r", project: "Prospero Manager", role: "cr.prospero", url: "https://github.com/notmaj0r/ProsperoMgr" },
  { author: "ps5-payload-dev", project: "ftpsrv", role: "cr.ftpsrv", url: "https://github.com/ps5-payload-dev/ftpsrv" },
];
