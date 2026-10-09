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

use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Duration;

use super::client::Client;
use super::path as rp;
use super::prospero::{map_io, Conflict};
use super::{RemoteError, RemoteKind};
use crate::ctl::{Ctx, Stage};
use crate::par::parallel_for;
use crate::util::long_path;
use crate::{Error, Result};

#[derive(Debug, Clone, Default)]
pub struct DownloadStats {
    pub files: u64,
    pub dirs: u64,
    pub bytes: u64,
    pub skipped: u64,

    pub outputs: Vec<PathBuf>,
}

struct Item {
    remote: String,

    local: PathBuf,
    is_dir: bool,
    size: u64,
}

pub fn local_name(name: &str) -> String {
    let mut s: String = name
        .chars()
        .map(|c| {
            if matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') || (c as u32) < 32 {
                '_'
            } else {
                c
            }
        })
        .collect();
    while s.ends_with('.') || s.ends_with(' ') {
        s.pop();
        s.push('_');
    }
    if s.is_empty() {
        s.push('_');
    }
    let stem = s.split('.').next().unwrap_or("").to_ascii_uppercase();
    let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || (stem.len() == 4
            && (stem.starts_with("COM") || stem.starts_with("LPT"))
            && stem.as_bytes()[3].is_ascii_digit());
    if reserved {
        s.insert(0, '_');
    }
    s
}

fn unique_local(dir: &Path, name: &str) -> PathBuf {
    let first = dir.join(name);
    if !long_path(&first).exists() {
        return first;
    }
    let (stem, ext) = rp::split_ext(name);
    for n in 2..10_000 {
        let cand = dir.join(format!("{stem} ({n}){ext}"));
        if !long_path(&cand).exists() {
            return cand;
        }
    }
    first
}

fn scan(client: &Client, remote_paths: &[String], ctx: &Ctx) -> Result<Vec<Item>> {
    let mut items = Vec::new();
    for raw in remote_paths {
        let p = rp::norm(raw);
        let top = if p == "/" { "PS5".to_string() } else { rp::file_name(&p) };
        let entry = client
            .stat(&p)?
            .ok_or_else(|| Error::Remote(RemoteError::new(RemoteKind::NotFound, 404, p.clone())))?;
        let top_local = PathBuf::from(local_name(&top));
        if !entry.is_dir {
            items.push(Item {
                remote: p,
                local: top_local,
                is_dir: false,
                size: entry.size,
            });
            continue;
        }
        items.push(Item {
            remote: p.clone(),
            local: top_local.clone(),
            is_dir: true,
            size: 0,
        });
        let mut stack = vec![(p, top_local)];
        while let Some((dir, local)) = stack.pop() {
            ctx.cancel.check()?;
            ctx.progress.set_current(&dir);
            for e in client.list(&dir)? {
                if !rp::valid_name(&e.name) {
                    continue;
                }
                let remote = rp::join(&dir, &e.name);
                let loc = local.join(local_name(&e.name));
                if e.is_dir {
                    items.push(Item {
                        remote: remote.clone(),
                        local: loc.clone(),
                        is_dir: true,
                        size: 0,
                    });
                    stack.push((remote, loc));
                } else {
                    items.push(Item {
                        remote,
                        local: loc,
                        is_dir: false,
                        size: e.size,
                    });
                }
            }
        }
    }
    Ok(items)
}

pub fn download_paths(
    client: &Client,
    remote_paths: &[String],
    local_dir: &Path,
    policy: Conflict,
    conns: usize,
    ctx: &Ctx,
) -> Result<DownloadStats> {
    ctx.progress.begin_phase(Stage::Scanning, 0);
    let mut items = scan(client, remote_paths, ctx)?;
    std::fs::create_dir_all(long_path(local_dir))?;

    let mut outputs = Vec::new();
    let mut skipped_top: Vec<PathBuf> = Vec::new();
    let tops: Vec<PathBuf> = {
        let mut v: Vec<PathBuf> = items
            .iter()
            .filter_map(|i| i.local.components().next().map(|c| PathBuf::from(c.as_os_str())))
            .collect();
        v.sort();
        v.dedup();
        v
    };
    for top in &tops {
        let target = local_dir.join(top);
        if !long_path(&target).exists() {
            outputs.push(target);
            continue;
        }
        match policy {
            Conflict::Cancel => return Err(Error::invalid(crate::t!("err.exists", path = target.display()))),
            Conflict::Skip => skipped_top.push(top.clone()),
            Conflict::Replace => outputs.push(target),
            Conflict::KeepBoth => {
                let name = top.to_string_lossy().into_owned();
                let unique = unique_local(local_dir, &name);
                let new_top = PathBuf::from(unique.file_name().unwrap_or_default());
                for it in items.iter_mut() {
                    if it.local.starts_with(top) {
                        let rest = it.local.strip_prefix(top).unwrap_or(Path::new("")).to_path_buf();

                        it.local = if rest.as_os_str().is_empty() {
                            new_top.clone()
                        } else {
                            new_top.join(rest)
                        };
                    }
                }
                outputs.push(unique);
            }
        }
    }

    let mut skipped = 0u64;
    let mut todo: Vec<Item> = Vec::new();
    for it in items {
        if policy == Conflict::Skip && !it.is_dir && long_path(&local_dir.join(&it.local)).exists() {
            skipped += 1;
            continue;
        }
        todo.push(it);
    }
    for top in &skipped_top {
        if !todo.iter().any(|i| i.local.starts_with(top) && i.is_dir) {
            outputs.retain(|o| o != &local_dir.join(top));
        }
    }

    let total: u64 = todo.iter().filter(|i| !i.is_dir).map(|i| i.size).sum();
    let nfiles = todo.iter().filter(|i| !i.is_dir).count() as u64;
    ctx.progress.begin_phase(Stage::Processing, total);
    ctx.progress.set_files_total(nfiles);

    let mut dirs = 0u64;
    for it in todo.iter().filter(|i| i.is_dir) {
        ctx.cancel.check()?;
        std::fs::create_dir_all(long_path(&local_dir.join(&it.local)))?;
        dirs += 1;
    }
    let mut files: Vec<&Item> = todo.iter().filter(|i| !i.is_dir).collect();
    files.sort_by_key(|i| std::cmp::Reverse(i.size));

    let first_err: Mutex<Option<Error>> = Mutex::new(None);
    let result = parallel_for(files.len() as u64, conns.max(1), &ctx.cancel, |k| {
        let it = files[k as usize];
        let r = download_file(
            client,
            &it.remote,
            &local_dir.join(&it.local),
            policy != Conflict::Cancel,
            ctx,
        )
        .map(|_| {
            ctx.progress.add_files_done(1);
        });
        if let Err(e) = &r {
            if !e.is_cancelled() {
                let mut g = first_err.lock().unwrap();
                if g.is_none() {
                    *g = Some(match e {
                        Error::Remote(r) => Error::Remote(r.clone()),
                        other => Error::invalid(other.localized()),
                    });
                }
                ctx.cancel.cancel();
            }
        }
        r
    });
    if let Some(e) = first_err.into_inner().unwrap() {
        return Err(e);
    }
    result?;
    Ok(DownloadStats {
        files: nfiles,
        dirs,
        bytes: total,
        skipped,
        outputs,
    })
}

fn download_file(client: &Client, remote: &str, dest: &Path, replace: bool, ctx: &Ctx) -> Result<u64> {
    let dest_l = long_path(dest);
    if let Some(parent) = dest_l.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut tmp = dest_l.clone().into_os_string();
    tmp.push(".ps5gfc-part");
    let tmp = PathBuf::from(tmp);
    ctx.progress.set_current(remote);

    let mut attempt = 0u32;
    loop {
        let counted = std::cell::Cell::new(0u64);
        let res: Result<u64> = (|| {
            let (mut resp, len) = client.open_download(remote)?;
            let mut f = File::create(&tmp)?;
            let mut buf = vec![0u8; 1 << 20];
            let mut total = 0u64;
            loop {
                ctx.cancel.check()?;
                let n = resp.read(&mut buf).map_err(map_io)?;
                if n == 0 {
                    break;
                }
                f.write_all(&buf[..n])?;
                total += n as u64;
                counted.set(total);
                ctx.progress.add_done(n as u64);
                ctx.progress.add_out(n as u64);
            }
            if total != len {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Unreachable,
                    0,
                    format!("download incompleto: {total} de {len} bytes"),
                )));
            }
            f.flush()?;
            drop(f);
            if replace && dest_l.exists() {
                std::fs::remove_file(&dest_l)?;
            }
            std::fs::rename(&tmp, &dest_l)?;
            Ok(total)
        })();
        match res {
            Ok(n) => return Ok(n),
            Err(e) => {
                let _ = std::fs::remove_file(&tmp);
                let again = matches!(&e, Error::Remote(r) if r.is_transient()) && attempt < 2;
                if !again {
                    return Err(e);
                }
                attempt += 1;
                ctx.progress.sub_done(counted.get());
                ctx.progress.sub_out(counted.get());

                let t0 = std::time::Instant::now();
                while t0.elapsed() < Duration::from_millis(800 * attempt as u64) {
                    ctx.cancel.check()?;
                    std::thread::sleep(Duration::from_millis(50));
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn local_names_are_safe() {
        assert_eq!(local_name("a:b?c.txt"), "a_b_c.txt");
        assert_eq!(local_name("fim."), "fim_");
        assert_eq!(local_name("CON.txt"), "_CON.txt");
        assert_eq!(local_name("ok.exfat"), "ok.exfat");
        assert_eq!(local_name(""), "_");
    }
}
