PKG support needs one file that is not included: ProsperoPkgTool.Data.dll

Put it in this folder ("dll's"), next to PS5GFC.exe. Nothing else has to be set up.
Without it every format works except .pkg, and a .pkg conversion fails with a message saying the file is missing.

The DLL holds data that belongs to Sony, so it is not distributed with PS5GFC. It is built from the
source repository: see tools/ps5pkg-data/res/README.md for the eight data files it needs and where to obtain them,
then run scripts\build-data-dll.ps1.
