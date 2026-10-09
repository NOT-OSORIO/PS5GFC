# PS5GFC — PS5 Game Format Converter

Converts PS5 game dumps directly between formats, with no intermediate files:

```
folder ⇄ .exfat ⇄ .ffpkg (UFS2) ⇄ .ffpfsc (exFAT | UFS2 | PFS inside)
   └──────────────→ .pkg (debug FPKG), readable as a source and writable as a destination
```

The result can also be generated and sent straight to a PS5 console over the network (Prospero Manager or FTP) without
ever being written to the PC. The engine is written in Rust; the desktop application uses Tauri and React.

## Formats

| Format | Extension | Source | Destination |
|---|---|---|---|
| Folder | — | yes | yes |
| exFAT (64 KiB cluster) | `.exfat` | yes | yes |
| UFS2 | `.ffpkg` | yes | yes |
| PFS | `.ffpfs` | yes | — |
| PFSC with exFAT, UFS2 or PFS inside | `.ffpfsc` | yes | yes |
| PKG / debug FPKG | `.pkg` | yes | yes |

## How it works

* **Direct**: the source becomes a file list and the destination is produced in a single pass.
* **No temporary files**: the layout of the destination is computed first and every 64 KiB block is produced on demand,
  in parallel, and written straight to the final file. On error or cancellation the partial file is removed.
* **Light**: all logical threads but one, at low priority, with bounded memory, whatever the size of the game.
* **Verified**: the optional verification reopens the result and compares the CRC32 of every file with the source.
* **PKG**: built and read by `tools/ps5pkg-builder`, a small .NET program that only runs while a PKG task is running. It
  uses `tools/ps5pkg-engine`, a copy of the ProsperoPkgTool engine (MIT) with its slow loops rewritten; the output is
  byte-identical to the original engine.
* **Console files over FTP**: connects anonymously (the PS5's FTP server does not ask for a login) and can change file
  permissions one by one or recursively through a whole folder.
* **Interface zoom**: a small control in the title bar scales the whole interface up or down in real time, independent
  of Windows' own display scaling; the choice is remembered between launches.

## Command line

```
ps5gfc info <path>
ps5gfc convert <source> <destination> --to <folder|exfat|ffpkg|pkg|ffpfs|ffpfsc|ffpfsc:ufs2|ffpfsc:pfs> [options]

  --level N         zlib level 1-9 (default 7)
  --threads N       worker threads (default: logical cores − 1)
  --cluster-kib N   exFAT cluster size in KiB (default 64)
  --rebuild         rebuild the file system even if the image is already in the requested format
  --verify          verify the result file by file
  --overwrite       overwrite the output
  --no-times        fixed file dates (reproducible output)
  --free-mib N      extra free space in exFAT/UFS2 images
  --ampr MODE       ampr_emu.index: auto (default) | always | never
  --priority P      thread priority: low_perf (default) | normal | low
  --lang L          message language: pt-BR | en | es | fr | ru (or the PS5GFC_LANG variable)
  --console IP      the destination is a console folder (Prospero Manager, port 7070); not available for pkg

ps5gfc console <ip> info | ls <folder> | mkdir <folder> | rm <path> | mv <from> <to> | cp <from> <to>
ps5gfc console <ip> put <file|folder>... <console-folder> [--replace|--skip|--keep-both]
ps5gfc console <ip> get <path>... <local-folder> [--replace|--skip|--keep-both]
ps5gfc console <ip> cat <file>
```

## Console data (not included)

The PKG destination and source need fixed data (RSA moduli, keystone keys and the `right.sprx` stub).
It is **not part of this repository**. `tools/ps5pkg-data/res/README.md` lists the eight files and where to obtain them.

* Build the `ProsperoPkgTool.Data.dll` that holds them with `scripts\build-data-dll.ps1`.
* **Compiled releases** do not contain the data: the user puts that DLL in the `dll's` folder next to `PS5GFC.exe` and `.pkg`
  starts working. Without it every other format works and `.pkg` fails with a message saying the file is missing, in the
  interface's current language.
* **Building from source** with the eight files in `tools/ps5pkg-data/res/` also works: the data is then compiled in.

## Building

Requirements (Windows 10/11, 64-bit):

* Node.js LTS
* Rust (stable, MSVC) and the Visual Studio Build Tools with the "Desktop development with C++" workload
* WebView2 Runtime (included in Windows 11)
* .NET 10 SDK, only for the PKG destination/source (everything the builder needs is inside `tools/`; NuGet packages are restored on first build)

```
npm install
npm run tauri:dev
powershell -ExecutionPolicy Bypass -File scripts\empacotar.ps1
```

`scripts\empacotar.ps1` produces the portable version in `release\PS5GFC\`: `PS5GFC.exe` and `ps5gfc-cli.exe` (the PKG
builder is embedded in both) and an empty `dll's` folder for the console data DLL. Without the .NET SDK it still produces the
application, without PKG support.

Tests: `cargo test --workspace --release` and `npx tsc --noEmit`. The end-to-end PKG tests need the builder compiled
(`dotnet build tools\ps5pkg-builder -c Release`) and skip themselves otherwise.

## Layout

* `crates/ps5gfc-core` — the engine: formats, planners, parallel pipeline, verification, console client.
* `crates/ps5gfc-cli` — the command-line front end.
* `src-tauri` — the desktop application backend (commands, job queue, console transfers).
* `src` — the React interface.
* `tools/ps5pkg-builder` — the PKG builder and reader (.NET).
* `tools/ps5pkg-engine` — the patched ProsperoPkgTool engine.
* `tools/ps5pkg-data` — embeds the Sony console data into `ProsperoPkgTool.Data.dll`; the data files are **not included**, see below.
* `tools/ps5pkg-core` — the PS5 PKG Tool libraries the builder uses (GPL-3.0, unmodified except for one project reference).

## Known limits

* UFS2 (`.ffpkg`), PFS and PKG pass the structural tests and read-back.
* Only standard zlib is used for compression.
* Compressed archives (zip/7z/rar) are not supported as sources; only the first item is loaded when several are dropped.
* A `.pkg` can only be opened if it is a debug/FPKG package with the default passcode.

## License

Copyright (C) 2026 OSØRIO.

PS5GFC is free software, licensed under the **GNU General Public License v3.0** (`LICENSE`). Anyone who distributes a
compiled build must also make this source code available under the same license.

| Part | License |
|---|---|
| PS5GFC (`crates`, `src`, `src-tauri`, `tools/ps5pkg-builder`) | GPL-3.0 |
| `tools/ps5pkg-core` | GPL-3.0 (PS5 PKG Tool), BSD-2-Clause for `PS5PKGTool.Ufs2` |
| `tools/ps5pkg-engine` | MIT (ProsperoPkgTool; its own notice in `tools/ps5pkg-engine/LICENSE`) |

Third-party licenses and trademarks are described in `NOTICE.md`. PS5GFC is independent of Sony Interactive
Entertainment and is meant for dumps of games you own.

## Credits

The PS5GFC code was written from scratch. These community projects were the base of knowledge about the formats and the
reference used to check results:

* [PS5 PKG Tool](https://github.com/pearlxcore/PS5PkgTool), by **pearlxcore** — the engine that builds, verifies and opens PKG/FPKG files.
* [MkPFS](https://github.com/PSBrew/MkPFS), by **PSBrew** — PFS/PFSC format, compression and AMPR index.
* [exFAT Image Builder](https://github.com/kerrdec97/ps5-exfat-builder), by **DecKerr97** — `.exfat` and `.ffpkg` workflow.
* [PS5 FFPFS-CLI](https://github.com/bizkut/ps5-ffpfs-cli), by **bizkut** — layout of `.ffpfsc` with a nested PFS.
* [PS5 FFPFSC PRO](https://github.com/KINGDKAK/PS5-FFPFSC-PRO), by **KINGDKAK** — `.ffpfsc` workflow.
* [UFS2Tool](https://github.com/SvenGDK/UFS2Tool), by **SvenGDK** — UFS1/UFS2 and the `.ffpkg` format.
* [Prospero Manager](https://github.com/notmaj0r/ProsperoMgr), by **notmaj0r** — the console file API.
* [ftpsrv](https://github.com/ps5-payload-dev/ftpsrv), by **ps5-payload-dev** — reference for how the console FTP server answers.

My sincere thanks to the whole jailbreak and homebrew community — the researchers, developers and
enthusiasts whose patience, rigour and generosity made the console's hardware free to be used as each person sees fit.
