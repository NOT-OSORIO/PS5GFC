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

use std::path::Path;
use std::time::{Duration, UNIX_EPOCH};

use crate::ctl::{Ctx, Stage};
use crate::io::{Closer, OutFile, ReadAt};
use crate::par::{ordered_map, parallel_for};
use crate::util::{ceil_div, join_rel, long_path, MIB};
use crate::volume::{totals, Volume};
use crate::{Error, Result};

const BIG_FILE: u64 = 128 * MIB;
const BIG_CHUNK: u64 = 4 * MIB;
const SMALL_CHUNK: u64 = MIB;

#[derive(Debug, Clone, Default)]
pub struct ExtractStats {
    pub files: u64,
    pub dirs: u64,
    pub bytes: u64,
}

#[cfg(windows)]
fn invalid_on_windows(name: &str) -> bool {
    const BAD: &[char] = &['<', '>', ':', '"', '\\', '|', '?', '*'];
    if name.chars().any(|c| BAD.contains(&c) || (c as u32) < 32) || name.ends_with('.') || name.ends_with(' ') {
        return true;
    }
    let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
    matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || (stem.len() == 4
            && (stem.starts_with("COM") || stem.starts_with("LPT"))
            && stem.as_bytes()[3].is_ascii_digit())
}

pub fn extract_volume(vol: &dyn Volume, dest: &Path, threads: usize, ctx: &Ctx) -> Result<ExtractStats> {
    let (total_bytes, nfiles, ndirs) = totals(vol);
    ctx.progress.begin_phase(Stage::Processing, total_bytes);
    ctx.progress.set_files_total(nfiles);

    #[cfg(windows)]
    {
        let bad: Vec<&str> = vol
            .entries()
            .iter()
            .filter(|e| e.path.split('/').any(invalid_on_windows))
            .map(|e| e.path.as_str())
            .take(5)
            .collect();
        if !bad.is_empty() {
            return Err(Error::invalid(crate::t!("err.win_names", names = bad.join(", "))));
        }
    }

    std::fs::create_dir_all(long_path(dest))?;
    let entries = vol.entries();

    let mut dirs: Vec<usize> = (0..entries.len()).filter(|&i| entries[i].is_dir).collect();
    dirs.sort_by(|&a, &b| entries[a].path.cmp(&entries[b].path));
    for &i in &dirs {
        ctx.cancel.check()?;
        std::fs::create_dir_all(long_path(&join_rel(dest, &entries[i].path)?))?;
    }

    let mut big: Vec<usize> = Vec::new();
    let mut small: Vec<usize> = Vec::new();
    for (i, e) in entries.iter().enumerate() {
        if e.is_dir {
            continue;
        }
        if e.size >= BIG_FILE {
            big.push(i);
        } else {
            small.push(i);
        }
    }
    big.sort_by_key(|&i| std::cmp::Reverse(entries[i].size));

    small.sort_by_key(|&i| std::cmp::Reverse(entries[i].size));

    for &i in &big {
        ctx.cancel.check()?;
        let e = &entries[i];
        ctx.progress.set_current(&e.path);
        let src = vol.open(i)?;
        let out_path = join_rel(dest, &e.path)?;
        copy_big(src.as_ref(), &out_path, e.size, e.mtime, threads, ctx)?;
        ctx.progress.add_files_done(1);
    }

    let closer = Closer::new((threads / 3).clamp(2, 8), threads * 4);
    let run = parallel_for(small.len() as u64, threads, &ctx.cancel, |k| {
        let i = small[k as usize];
        let e = &entries[i];
        ctx.progress.set_current(&e.path);
        let out_path = join_rel(dest, &e.path)?;
        let out = OutFile::create_light(&out_path)?;
        if e.size > 0 {
            let src = vol.open(i)?;
            let mut buf = vec![0u8; SMALL_CHUNK.min(e.size) as usize];
            let mut off = 0u64;
            while off < e.size {
                let n = (e.size - off).min(buf.len() as u64) as usize;
                src.read_exact_at(off, &mut buf[..n])?;
                out.write_all_at(off, &buf[..n])?;
                off += n as u64;
                ctx.progress.add_done(n as u64);
                ctx.progress.add_out(n as u64);
            }
        }
        apply_mtime(&out, e.mtime);
        closer.close(out);
        ctx.progress.add_files_done(1);
        Ok(())
    });
    closer.finish();
    run?;

    Ok(ExtractStats {
        files: nfiles,
        dirs: ndirs,
        bytes: total_bytes,
    })
}

fn apply_mtime(out: &OutFile, mtime: Option<i64>) {
    if let Some(t) = mtime {
        if t >= 0 {
            let _ = out.file().set_modified(UNIX_EPOCH + Duration::from_secs(t as u64));
        }
    }
}

fn copy_big(src: &dyn ReadAt, path: &Path, size: u64, mtime: Option<i64>, threads: usize, ctx: &Ctx) -> Result<()> {
    let out = OutFile::create(path, size)?;
    let chunks = ceil_div(size, BIG_CHUNK);
    let result = ordered_map(
        chunks,
        threads,
        threads * 2 + 2,
        &ctx.cancel,
        |i| {
            let off = i * BIG_CHUNK;
            let n = BIG_CHUNK.min(size - off) as usize;
            let mut buf = vec![0u8; n];
            src.read_exact_at(off, &mut buf)?;
            Ok(buf)
        },
        |i, buf| {
            out.write_all_at(i * BIG_CHUNK, &buf)?;
            ctx.progress.add_done(buf.len() as u64);
            ctx.progress.add_out(buf.len() as u64);
            Ok(())
        },
    );
    if result.is_err() {
        out.discard();
        drop(out);
        let _ = std::fs::remove_file(long_path(path));
        return result;
    }
    apply_mtime(&out, mtime);
    Ok(())
}
