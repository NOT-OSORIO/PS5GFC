# Console data (not included)

The PKG destination and source need fixed data that belongs to Sony and is **not part of this repository**.
Place the files below in this folder (`tools/ps5pkg-data/res/`) before building. They are embedded into
`ProsperoPkgTool.Data.dll` at compile time by `tools/ps5pkg-data/ProsperoPkgTool.Data.csproj`; nothing else in the
source tree has to change. The names must match exactly.

| File | Size | What it is |
|---|---|---|
| `passcode.bin` | 2688 bytes | The 7 RSA moduli (7 × 384 bytes) used to wrap the package passcode keys |
| `mount_image.bin` | 384 bytes | RSA modulus used for the image key entry |
| `metadata_modulus.bin` | 384 bytes | RSA modulus used for the metadata/header signature |
| `right.sprx` | 12752 bytes | The `right.sprx` stub that goes inside the package |
| `ks_fp3.bin` | 32 bytes | Keystone v3 fingerprint key |
| `ks_mac3.bin` | 32 bytes | Keystone v3 MAC key |
| `ks_fp2.bin` | 32 bytes | Keystone v2 fingerprint key |
| `ks_mac2.bin` | 32 bytes | Keystone v2 MAC key |

## Where to get them

From [PS5 PKG Tool](https://github.com/pearlxcore/PS5PkgTool) 1.2.0, in its `ProsperoPkgTool.dll`:

* `passcode.bin`, `mount_image.bin`, `metadata_modulus.bin` and `right.sprx` are embedded resources of that DLL, named
  `ProsperoPkgTool.Keys.passcode.bin`, `ProsperoPkgTool.Keys.mount_image.bin`, `ProsperoPkgTool.Keys.metadata_modulus.bin`
  and `ProsperoPkgTool.Resources.right.sprx`. Extract each one and save it here without the prefix.
* The four keystone keys are the constants `FingerprintKeyV3`, `MacKeyV3`, `FingerprintKeyV2` and `MacKeyV2` of the class
  `ProsperoPkgTool.Crypto.Keystone` in the same DLL (32 bytes each; save the raw bytes, not the hex text).

Any .NET decompiler or resource viewer can read them (for example ILSpy).

## Turning them into the DLL

Run `scripts\build-data-dll.ps1` from the repository root. It checks that all eight files are here, builds
`ProsperoPkgTool.Data.dll` and copies it to `release\PS5GFC\dll's\`. A compiled PS5GFC finds it there at run time; give the
same DLL to anyone who runs a build that does not include it.

## Without these files

The project still compiles (with warnings) and the other formats work. Only converting to or from `.pkg` fails.

Do not commit the files: `.gitignore` excludes everything in this folder except this note.
