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

use crate::ctl::{Ctx, Stage};
use crate::emit::CHUNK;
use crate::io::ReadAt;
use crate::par::ordered_map;
use crate::remote::upload::SeqSink;
use crate::util::ceil_div;
use crate::Result;

pub fn write_image_stream(image: &dyn ReadAt, sink: &mut dyn SeqSink, threads: usize, ctx: &Ctx) -> Result<u64> {
    let total = image.len();
    ctx.progress.begin_phase(Stage::Processing, total);
    let chunks = ceil_div(total, CHUNK);
    let window = threads * 2 + 2;
    ordered_map(
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
        |_i, buf| {
            sink.write_all(&buf)?;
            ctx.progress.add_done(buf.len() as u64);
            ctx.progress.add_out(buf.len() as u64);
            Ok(())
        },
    )?;
    Ok(total)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::io::MemReader;
    use crate::pfs::{plan_container, write_container, ContainerOptions};
    use crate::pfsc::PfscOptions;

    fn sample(len: usize) -> Vec<u8> {
        let mut v = Vec::with_capacity(len);
        let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
        for i in 0..len {
            let blk = i / 65536;
            v.push(match blk % 4 {
                0 => 0,
                1 => (i % 251) as u8,
                2 => {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    (x >> 11) as u8
                }
                _ => b"abcdefgh"[i % 8],
            });
        }
        v
    }

    #[test]
    fn image_stream_equals_source() {
        let data = sample(5 * 1024 * 1024 + 4321);
        let mut out: Vec<u8> = Vec::new();
        let n = write_image_stream(&MemReader::new(data.clone()), &mut out, 4, &Ctx::new()).unwrap();
        assert_eq!(n, data.len() as u64);
        assert_eq!(out, data);
    }

    #[test]
    fn container_stream_is_byte_identical_to_file() {
        let data = sample(65536 * 37 + 12345);
        let img = MemReader::new(data);
        let opts = ContainerOptions {
            pfsc: PfscOptions {
                threads: 4,
                ..Default::default()
            },
            timestamp: 1_700_000_000,
            ..Default::default()
        };

        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.ffpfsc");
        let st_file = write_container(&img, "PPSA00001.exfat", &path, &opts, &Ctx::new()).unwrap();
        let file_bytes = std::fs::read(&path).unwrap();

        let ctx = Ctx::new();
        let plan = plan_container(&img, "PPSA00001.exfat", &opts, &ctx).unwrap();
        assert_eq!(plan.file_len(), st_file.file_len);
        let mut out: Vec<u8> = Vec::new();
        let st = plan.write(&img, &opts, &mut out, &ctx).unwrap();
        assert_eq!(st.file_len, st_file.file_len);
        assert_eq!(out.len() as u64, plan.file_len());
        assert!(out == file_bytes, "o fluxo difere do arquivo gravado");
    }

    #[test]
    fn container_stream_detects_changed_source() {
        let img = MemReader::new(sample(65536 * 5));
        let opts = ContainerOptions {
            pfsc: PfscOptions {
                threads: 2,
                ..Default::default()
            },
            ..Default::default()
        };
        let plan = plan_container(&img, "A.exfat", &opts, &Ctx::new()).unwrap();
        let other = MemReader::new(sample(65536 * 5).into_iter().map(|b| b ^ 0x5a).collect());
        let mut out: Vec<u8> = Vec::new();
        assert!(plan.write(&other, &opts, &mut out, &Ctx::new()).is_err());
    }
}
