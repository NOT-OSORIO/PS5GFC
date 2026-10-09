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

use crate::ctl::{Ctx, Stage};
use crate::io::{OutFile, ReadAt};
use crate::par::ordered_map;
use crate::util::{ceil_div, long_path, MIB};
use crate::Result;

pub const CHUNK: u64 = 2 * MIB;

pub struct PartialFile {
    path: PathBuf,
    keep: bool,
}

impl PartialFile {
    pub fn new(path: &Path) -> Self {
        Self {
            path: long_path(path),
            keep: false,
        }
    }
    pub fn keep(mut self) {
        self.keep = true;
    }
}

impl Drop for PartialFile {
    fn drop(&mut self) {
        if !self.keep {
            let _ = std::fs::remove_file(&self.path);
        }
    }
}

pub fn write_image(image: &dyn ReadAt, path: &Path, threads: usize, ctx: &Ctx) -> Result<u64> {
    let total = image.len();
    ctx.progress.begin_phase(Stage::Processing, total);
    let guard = PartialFile::new(path);
    let out = OutFile::create(path, total)?;
    let chunks = ceil_div(total, CHUNK);
    let window = threads * 2 + 2;

    let result = ordered_map(
        chunks,
        threads,
        window,
        &ctx.cancel,
        |i| {
            let off = i * CHUNK;
            let len = CHUNK.min(total - off) as usize;
            let mut buf = vec![0u8; len];
            image.read_exact_at(off, &mut buf)?;
            Ok(buf)
        },
        |i, buf| {
            out.write_all_at(i * CHUNK, &buf)?;
            ctx.progress.add_done(buf.len() as u64);
            ctx.progress.add_out(buf.len() as u64);
            Ok(())
        },
    )
    .and_then(|()| out.sync().map_err(Into::into));
    if result.is_err() {
        out.discard();
        result?;
    }
    guard.keep();
    Ok(total)
}
