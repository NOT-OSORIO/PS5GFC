# PS5GFC — PS5 Game Format Converter

Converts PS5 game dumps directly from one format to another, with no intermediate files.

```
folder ⇄ .exfat ⇄ .ffpkg (UFS2) ⇄ .ffpfsc (exFAT | UFS2 | PFS inside)
   └──────────────→ .pkg (debug FPKG), readable as a source and writable as a destination
```

## Formats

| Format | Extension | Source | Destination |
|---|---|---|---|
| Folder | — | yes | yes |
| exFAT | `.exfat` | yes | yes |
| UFS2 | `.ffpkg` | yes | yes |
| PFS | `.ffpfs` | yes | — |
| PFSC with exFAT, UFS2 or PFS inside | `.ffpfsc` | yes | yes |
| PKG / debug FPKG | `.pkg` | yes | yes |

## Features

* Drop a game folder or image and pick the target format; the result is written in a single pass.
* Optional verification: the result is reopened and every file is compared with the source.
* Send the result straight to a PS5 over the network (Prospero Manager or FTP), without writing it to the PC.
* Browse and manage the console's files (list, create, move, copy, delete, upload, download); FTP connects anonymously,
  with no login to configure.
* Change file permissions, one by one or recursively through a whole folder.
* Job queue with progress, speed and cancellation; a cancelled job leaves no partial file behind.
* A small zoom control in the title bar scales the interface in real time, remembered between launches.
* Interface in Portuguese, English, Spanish, French and Russian; log output is always in English.

## Files

* `PS5GFC.exe` — the application.
* `ps5gfc-cli.exe` — the same engine on the command line (`ps5gfc-cli.exe --help`).

Each file works on its own. Requires Windows 10/11 64-bit; the application also needs the WebView2 Runtime, which Windows 11 already includes.

## PKG support needs a DLL you provide

Every format works out of the box except `.pkg`. For `.pkg` you must supply one file, `ProsperoPkgTool.Data.dll`, and put it
in the `dll's` folder next to `PS5GFC.exe`. It contains data that belongs to Sony and is therefore not distributed with
PS5GFC. Build it from the source repository (`tools/ps5pkg-data/res/README.md` explains which files it needs and where to
get them; then run `scripts\build-data-dll.ps1`). Without it, a `.pkg` conversion stops with a message saying the file is
missing, in whichever language the interface is set to.

## License

Copyright (C) 2026 OSØRIO. GNU General Public License v3.0 (`LICENSE`). The source code is available in the project repository.

## Thanks

My sincere thanks to the whole PlayStation 5 jailbreak and homebrew community — the researchers, developers and
enthusiasts whose patience, rigour and generosity made the console's hardware free to be used as each person sees fit.

Special thanks to the authors of the projects that taught me the formats and served as reference:

* [PS5 PKG Tool](https://github.com/pearlxcore/PS5PkgTool) — **pearlxcore**
* [MkPFS](https://github.com/PSBrew/MkPFS) — **PSBrew**
* [exFAT Image Builder](https://github.com/kerrdec97/ps5-exfat-builder) — **DecKerr97**
* [PS5 FFPFS-CLI](https://github.com/bizkut/ps5-ffpfs-cli) — **bizkut**
* [PS5 FFPFSC PRO](https://github.com/KINGDKAK/PS5-FFPFSC-PRO) — **KINGDKAK**
* [UFS2Tool](https://github.com/SvenGDK/UFS2Tool) — **SvenGDK**
* [Prospero Manager](https://github.com/notmaj0r/ProsperoMgr) — **notmaj0r**
* [ftpsrv](https://github.com/ps5-payload-dev/ftpsrv) — **ps5-payload-dev**
