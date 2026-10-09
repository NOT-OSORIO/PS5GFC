# Third-party notices

PS5GFC, Copyright (C) 2026 OSØRIO, is licensed under the GNU General Public License v3.0 (`LICENSE`). Its own code is original (Rust and TypeScript);
it also includes the GPL-3.0 code of PS5 PKG Tool. The components below keep their own licenses, all compatible with the
GPL-3.0.

**Rust** — `tauri` (MIT/Apache-2.0), `serde`, `serde_json`, `thiserror`, `crossbeam-channel`, `crc32fast`, `base64`,
`tempfile`, `windows-sys` (MIT/Apache-2.0), `flate2` with `zlib-rs` (zlib).

**Interface** — React, Vite, Tailwind CSS, Radix UI, motion, zustand, sonner, lucide-react (MIT/ISC); the Inter and
JetBrains Mono fonts (SIL OFL 1.1).

## Compatibility constants

Some values must be exactly these for the console to accept the image; they are format data, not third-party code:

* **AMPR index**: the initial FNV-1a hash base is `1469598103934665603` (not the standard `0xcbf29ce484222325`); the
  console's resolver depends on it.
* **exFAT upcase table**: the standard table from the exFAT specification (5836 bytes, checksum `0xE619D30D`).
* **Reproducible output**: serial number `0x4D6B5046` and the fixed date 2024-01-01 (DOS format) when "Keep file dates" is
  off.

## PKG / FPKG (`tools/ps5pkg-builder`, `tools/ps5pkg-engine`)

The PKG destination and source are not handled by the Rust code: PS5GFC runs `ps5pkg-builder`, a separate .NET program
(a thin shell in `tools/ps5pkg-builder`) built on the **PS5 PKG Tool** engine (pearlxcore). The two only talk through the
command line and standard output.

* **ProsperoPkgTool** — the PKG engine, MIT, © pearlxcore. `tools/ps5pkg-engine` is a modified copy of it, with its slow
  loops rewritten (obtained by decompiling the `ProsperoPkgTool.dll` shipped with PS5 PKG Tool 1.2.0, as the MIT license
  allows, keeping its copyright notice in `tools/ps5pkg-engine/LICENSE`). Its output is byte-identical to the original's.
* **PS5PKGTool.Core / Ffpfsc** — part of PS5 PKG Tool, licensed under the **GPL-3.0** (`tools/ps5pkg-core/LICENSE`). They are
  included in `tools/ps5pkg-core` as released, with one change: `PS5PKGTool.Core.csproj` references the local
  `tools/ps5pkg-engine` project instead of the prebuilt `ProsperoPkgTool.dll`, and a native-library reference was dropped.
* **PS5PKGTool.Ufs2** — based on UFS2Tool (SvenGDK), BSD-2-Clause (`tools/ps5pkg-core/PS5PKGTool.Ufs2/LICENSE`).
* **BCnEncoder.Net**, **SixLabors.ImageSharp** — builder dependencies (MIT / Six Labors Split License).

### Console data (not included)

Producing a package the console accepts requires fixed data that belongs to Sony: RSA moduli, the keystone keys and the
`right.sprx` stub. They are not in this repository. `tools/ps5pkg-data/res/README.md` lists the files, where to obtain
them and where to put them. Compiled releases load them at run time from `ProsperoPkgTool.Data.dll` in a `dll's` folder
next to the executable, which the user provides.

### Trademarks

PlayStation, PS5 and Sony are trademarks of Sony Interactive Entertainment. This project is independent and is not
affiliated with or endorsed by Sony, Epic Games or any project credited here. It is meant for dumps of games you own.

## Reference projects

Studied to understand the formats and used as comparison references in the tests; none of their code is part of PS5GFC:
MkPFS (PSBrew, GPL-3.0), exFAT Image Builder (DecKerr97), PS5 FFPFS-CLI (bizkut, MIT), PS5 FFPFSC PRO (KINGDKAK) and
UFS2Tool (SvenGDK, BSD-2-Clause). The credits appear on the application's About page.

**Prospero Manager** (notmaj0r, GPL-3.0) is the application that runs on the PS5 and serves the HTTP API used by the PS5
page: its code was only read to understand the protocol; PS5GFC only makes HTTP requests to the API and contains none of it.

**ftpsrv** (ps5-payload-dev, GPL-3.0) is the most used FTP server on the PS5. Its code was only read, to check how it
answers (codes, listing format, `SELF`); PS5GFC's FTP client was written from scratch from RFC 959 and contains none of it.
