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

use std::path::{Path, PathBuf};
use std::sync::Arc;

use serde::{Deserialize, Serialize};

use crate::ctl::Ctx;
use crate::exfat::{is_exfat, ExfatVolume};
use crate::io::{FileReader, ReadAt};
use crate::pfs::format::is_pfs;
use crate::pfs::PfsImage;
use crate::pfsc::is_pfsc;
use crate::ufs2::Ufs2Volume;
use crate::volume::{FolderVolume, Volume, VolumeKind};
use crate::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FsKind {
    Exfat,
    Ufs2,
    Pfs,
}

impl FsKind {
    pub fn label(self) -> &'static str {
        match self {
            FsKind::Exfat => "exFAT",
            FsKind::Ufs2 => "UFS2",
            FsKind::Pfs => "PFS",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "kind", content = "inner", rename_all = "snake_case")]
pub enum Format {
    Folder,

    Image(FsKind),

    Ffpfsc(FsKind),

    Pkg,
}

impl Format {
    pub fn extension(self) -> &'static str {
        match self {
            Format::Folder => "",
            Format::Image(FsKind::Exfat) => "exfat",
            Format::Image(FsKind::Ufs2) => "ffpkg",
            Format::Image(FsKind::Pfs) => "ffpfs",
            Format::Ffpfsc(_) => "ffpfsc",
            Format::Pkg => "pkg",
        }
    }
    pub fn label(self) -> String {
        match self {
            Format::Folder => crate::t!("fmt.folder"),
            Format::Image(k) => match k {
                FsKind::Exfat => "exFAT (.exfat)".into(),
                FsKind::Ufs2 => "FFPKG / UFS2 (.ffpkg)".into(),
                FsKind::Pfs => "PFS (.ffpfs)".into(),
            },
            Format::Ffpfsc(k) => format!("FFPFSC ({})", crate::t!("fmt.inner", fs = k.label())),
            Format::Pkg => "PKG / FPKG (.pkg)".into(),
        }
    }
    pub fn inner_fs(self) -> Option<FsKind> {
        match self {
            Format::Folder | Format::Pkg => None,
            Format::Image(k) | Format::Ffpfsc(k) => Some(k),
        }
    }

    pub fn same_kind(self, other: Format) -> bool {
        match (self, other) {
            (Format::Folder, Format::Folder) | (Format::Ffpfsc(_), Format::Ffpfsc(_)) => true,
            (Format::Pkg, Format::Pkg) => true,
            (Format::Image(a), Format::Image(b)) => a == b,
            _ => false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Magic {
    Exfat,
    Ufs2,
    Pfs,
    Pfsc,
    Zip,
    Rar,
    SevenZ,
    Unknown,
}

#[cfg(test)]
mod same_kind_tests {
    use super::{Format, FsKind};

    #[test]
    fn same_kind_ignores_the_ffpfsc_inner_image_only() {
        assert!(Format::Folder.same_kind(Format::Folder));
        assert!(Format::Image(FsKind::Exfat).same_kind(Format::Image(FsKind::Exfat)));
        assert!(!Format::Image(FsKind::Exfat).same_kind(Format::Image(FsKind::Ufs2)));
        assert!(Format::Ffpfsc(FsKind::Exfat).same_kind(Format::Ffpfsc(FsKind::Ufs2)));

        assert!(!Format::Ffpfsc(FsKind::Exfat).same_kind(Format::Image(FsKind::Exfat)));
        assert!(!Format::Folder.same_kind(Format::Image(FsKind::Exfat)));
    }
}

const UFS2_SB_OFFSET: u64 = 65536;
const UFS2_MAGIC: u32 = 0x1954_0119;

pub fn sniff(r: &dyn ReadAt) -> Magic {
    let mut head = [0u8; 512];
    let n = r.read_at(0, &mut head).unwrap_or(0);
    let head = &head[..n];
    if is_exfat(head) {
        return Magic::Exfat;
    }
    if is_pfs(head) {
        return Magic::Pfs;
    }
    if is_pfsc(head) {
        return Magic::Pfsc;
    }
    if head.starts_with(b"PK\x03\x04") || head.starts_with(b"PK\x05\x06") {
        return Magic::Zip;
    }
    if head.starts_with(b"Rar!\x1A\x07") {
        return Magic::Rar;
    }
    if head.starts_with(&[b'7', b'z', 0xBC, 0xAF, 0x27, 0x1C]) {
        return Magic::SevenZ;
    }
    let mut m = [0u8; 4];
    if r.read_at(UFS2_SB_OFFSET + 0x55C, &mut m).unwrap_or(0) == 4 && u32::from_le_bytes(m) == UFS2_MAGIC {
        return Magic::Ufs2;
    }
    Magic::Unknown
}

pub struct OpenedSource {
    pub path: PathBuf,
    pub format: Format,

    pub volume: Arc<dyn Volume>,

    pub fs_image: Option<Arc<dyn ReadAt>>,

    pub wrapper: Option<WrapperInfo>,
}

#[derive(Debug, Clone, Serialize)]
pub struct WrapperInfo {
    pub file_size: u64,
    pub inner_name: String,
    pub inner_size: u64,
}

fn open_fs_image(img: Arc<dyn ReadAt>, ctx: &Ctx) -> Result<Option<(FsKind, Arc<dyn Volume>)>> {
    match sniff(img.as_ref()) {
        Magic::Exfat => Ok(Some((FsKind::Exfat, Arc::new(ExfatVolume::open_with(img, Some(ctx))?)))),
        Magic::Pfs => Ok(Some((FsKind::Pfs, Arc::new(PfsImage::open_with(img, Some(ctx))?)))),
        Magic::Ufs2 => Ok(Some((FsKind::Ufs2, Arc::new(Ufs2Volume::open_with(img, Some(ctx))?)))),
        _ => Ok(None),
    }
}

pub fn open_source(path: &Path, ctx: &Ctx) -> Result<OpenedSource> {
    let md = std::fs::metadata(crate::util::long_path(path))
        .map_err(|e| Error::invalid(crate::t!("err.access", path = path.display(), err = e)))?;

    if md.is_file() && path.extension().is_some_and(|e| e.eq_ignore_ascii_case("pkg")) {
        return crate::pkgsrc::open(path, ctx);
    }
    if md.is_dir() {
        let vol = FolderVolume::scan(path, ctx)?;
        return Ok(OpenedSource {
            path: path.to_path_buf(),
            format: Format::Folder,
            volume: Arc::new(vol),
            fs_image: None,
            wrapper: None,
        });
    }

    let file: Arc<dyn ReadAt> = Arc::new(FileReader::open_with_len(path, md.len())?);
    match sniff(file.as_ref()) {
        Magic::Exfat => {
            let vol = ExfatVolume::open_with(file.clone(), Some(ctx))?;
            Ok(OpenedSource {
                path: path.to_path_buf(),
                format: Format::Image(FsKind::Exfat),
                volume: Arc::new(vol),
                fs_image: Some(file),
                wrapper: None,
            })
        }
        Magic::Ufs2 => {
            let vol = Ufs2Volume::open_with(file.clone(), Some(ctx))?;
            Ok(OpenedSource {
                path: path.to_path_buf(),
                format: Format::Image(FsKind::Ufs2),
                volume: Arc::new(vol),
                fs_image: Some(file),
                wrapper: None,
            })
        }
        Magic::Pfs => {
            let pfs = PfsImage::open_with(file.clone(), Some(ctx))?;

            if let Some(idx) = pfs.single_file() {
                let inner = pfs.open(idx)?;
                if let Some((kind, vol)) = open_fs_image(inner.clone(), ctx)? {
                    let name = pfs.entries()[idx].path.clone();
                    let size = inner.len();
                    return Ok(OpenedSource {
                        path: path.to_path_buf(),
                        format: Format::Ffpfsc(kind),
                        volume: vol,
                        fs_image: Some(inner),
                        wrapper: Some(WrapperInfo {
                            file_size: md.len(),
                            inner_name: name,
                            inner_size: size,
                        }),
                    });
                }
            }
            Ok(OpenedSource {
                path: path.to_path_buf(),
                format: Format::Image(FsKind::Pfs),
                volume: Arc::new(pfs),
                fs_image: None,
                wrapper: None,
            })
        }
        Magic::Zip | Magic::Rar | Magic::SevenZ => Err(Error::invalid(crate::t!("err.archive"))),
        Magic::Pfsc => Err(Error::invalid(crate::t!("err.bare_pfsc"))),
        Magic::Unknown => Err(Error::invalid(crate::t!("err.unknown_format", path = path.display()))),
    }
}

impl OpenedSource {
    pub fn kind(&self) -> VolumeKind {
        self.volume.kind()
    }
}
