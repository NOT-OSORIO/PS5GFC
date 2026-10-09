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

use crate::ctl::Ctx;
use crate::io::ReadAt;
use crate::par::parallel_for;
use crate::source::{open_source, Format};
use crate::volume::Volume;
use crate::{Error, Result};

const CHUNK: usize = 1 << 20;

fn crc_of(r: &dyn ReadAt, size: u64, ctx: &Ctx) -> Result<u32> {
    let mut h = crc32fast::Hasher::new();
    let mut buf = vec![0u8; CHUNK.min(size.max(1) as usize)];
    let mut off = 0u64;
    while off < size {
        ctx.cancel.check()?;
        let n = (size - off).min(buf.len() as u64) as usize;
        r.read_exact_at(off, &mut buf[..n])?;
        h.update(&buf[..n]);
        off += n as u64;
        ctx.progress.add_done(n as u64);
    }
    Ok(h.finalize())
}

pub fn compare_volumes(a: &dyn Volume, b: &dyn Volume, threads: usize, ctx: &Ctx) -> Result<()> {
    use std::collections::HashMap;
    let key = |p: &str| p.to_ascii_lowercase();
    let mut bmap: HashMap<String, usize> = HashMap::new();
    for (j, e) in b.entries().iter().enumerate() {
        if !e.is_dir {
            bmap.insert(key(&e.path), j);
        }
    }
    let files_a: Vec<usize> = (0..a.entries().len()).filter(|&i| !a.entries()[i].is_dir).collect();
    if files_a.len() != bmap.len() {
        return Err(Error::invalid(crate::t!(
            "verr.count",
            a = files_a.len(),
            b = bmap.len()
        )));
    }
    parallel_for(files_a.len() as u64, threads, &ctx.cancel, |k| {
        let i = files_a[k as usize];
        let ea = &a.entries()[i];
        let Some(&j) = bmap.get(&key(&ea.path)) else {
            return Err(Error::invalid(crate::t!("verr.missing", path = ea.path)));
        };
        let eb = &b.entries()[j];
        if ea.size != eb.size {
            return Err(Error::invalid(crate::t!(
                "verr.size",
                path = ea.path,
                a = ea.size,
                b = eb.size
            )));
        }
        ctx.progress.set_current(&ea.path);
        let ca = crc_of(a.open(i)?.as_ref(), ea.size, ctx)?;
        let cb = crc_of(b.open(j)?.as_ref(), eb.size, ctx)?;
        if ca != cb {
            return Err(Error::invalid(crate::t!("verr.content", path = ea.path)));
        }
        Ok(())
    })
}

pub fn verify_output(src: &dyn Volume, out: &Path, target: Format, threads: usize, ctx: &Ctx) -> Result<()> {
    let opened = open_source(out, ctx)?;
    if opened.format != target && target != Format::Folder {
        return Err(Error::invalid(crate::t!(
            "verr.format",
            got = opened.format.label(),
            want = target.label()
        )));
    }
    compare_volumes(src, opened.volume.as_ref(), threads, ctx)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::io::MemReader;

    #[test]
    fn crc_stops_when_cancelled() {
        let ctx = Ctx::new();
        let data = MemReader::new(vec![1u8; 8 << 20]);
        assert!(crc_of(&data, data.len(), &ctx).is_ok());
        ctx.cancel.cancel();
        assert!(matches!(crc_of(&data, data.len(), &ctx), Err(Error::Cancelled)));
    }
}
