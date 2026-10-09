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

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use super::client::Client;
use super::path as rp;
use super::prospero::Conflict;
use super::upload::{upload_direct, DirectOpts, RemoteWriter, SeqSink, DIRECT_MAX};
use super::{RemoteError, RemoteKind};
use crate::ctl::{Ctx, Stage};
use crate::io::ReadAt;
use crate::par::parallel_for;
use crate::util::MIB;
use crate::volume::{totals, Volume};
use crate::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Existing {
    Replace,

    Skip,

    Fail,
}

impl From<Conflict> for Existing {
    fn from(c: Conflict) -> Self {
        match c {
            Conflict::Replace => Existing::Replace,
            Conflict::Skip => Existing::Skip,
            Conflict::Cancel | Conflict::KeepBoth => Existing::Fail,
        }
    }
}

#[derive(Debug, Clone)]
pub struct Item {
    pub rel: String,
    pub is_dir: bool,
    pub size: u64,
    pub src: usize,
}

#[derive(Debug, Clone)]
pub struct Uploaded {
    pub path: String,
    pub size: u64,
    pub crc: Option<u32>,
}

#[derive(Debug, Clone, Default)]
pub struct UploadStats {
    pub files: u64,
    pub dirs: u64,
    pub bytes: u64,
    pub skipped: u64,

    pub uploaded: Vec<Uploaded>,
}

pub struct UploadParams<'a> {
    pub client: &'a Client,
    pub dest_dir: &'a str,

    pub conns: usize,
    pub existing: Existing,

    pub want_crc: bool,
}

const READ_PIECE: u64 = 4 * MIB;

fn check_rel(rel: &str) -> Result<()> {
    if rel.split('/').any(|c| c == ".." || c.contains('\0')) {
        return Err(Error::invalid(crate::t!("err.unsafe_path", path = rel)));
    }
    Ok(())
}

pub fn conns_for(threads: usize) -> usize {
    (threads / 2).clamp(2, 6)
}

pub fn upload_items(
    p: &UploadParams<'_>,
    items: &[Item],
    open: &(dyn Fn(usize) -> Result<Arc<dyn ReadAt>> + Sync),
    ctx: &Ctx,
) -> Result<UploadStats> {
    let client = p.client;
    let dest = rp::norm(p.dest_dir);
    for it in items {
        check_rel(&it.rel)?;
    }

    client.mkdir_all(&dest, Some(&ctx.cancel))?;
    let mut dirs: Vec<&str> = items
        .iter()
        .filter(|i| i.is_dir && !i.rel.is_empty())
        .map(|i| i.rel.as_str())
        .collect();

    let mut implicit: Vec<String> = Vec::new();
    for it in items.iter().filter(|i| !i.is_dir) {
        let mut parent = rp::parent(&format!("/{}", it.rel));
        while parent != "/" {
            implicit.push(parent[1..].to_string());
            parent = rp::parent(&parent);
        }
    }
    implicit.sort();
    implicit.dedup();
    let mut all_dirs: Vec<String> = dirs.drain(..).map(str::to_string).chain(implicit).collect();
    all_dirs.sort();
    all_dirs.dedup();
    for d in &all_dirs {
        ctx.cancel.check()?;
        ctx.progress.set_current(d);
        client.retry_busy(Some(&ctx.cancel), || client.mkdir(&rp::join(&dest, d)))?;
    }

    let mut files: Vec<&Item> = items.iter().filter(|i| !i.is_dir).collect();
    let mut skipped = 0u64;
    if p.existing != Existing::Replace && !files.is_empty() {
        let mut present: std::collections::HashSet<String> = std::collections::HashSet::new();
        for batch in files.chunks(400) {
            ctx.cancel.check()?;
            let paths: Vec<String> = batch.iter().map(|i| rp::join(&dest, &i.rel)).collect();
            present.extend(client.conflicts(&paths)?);
        }
        if !present.is_empty() {
            if p.existing == Existing::Fail {
                let first = present.iter().min().cloned().unwrap_or_default();
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Exists,
                    409,
                    crate::t!("err.exists", path = first),
                )));
            }
            let before = files.len();
            files.retain(|i| !present.contains(&rp::join(&dest, &i.rel)));
            skipped = (before - files.len()) as u64;

            let skipped_bytes: u64 = items
                .iter()
                .filter(|i| !i.is_dir && present.contains(&rp::join(&dest, &i.rel)))
                .map(|i| i.size)
                .sum();
            ctx.progress.add_done(skipped_bytes);
            ctx.progress.add_files_done(skipped);
        }
    }

    files.sort_by_key(|i| std::cmp::Reverse(i.size));

    let uploaded: Mutex<Vec<Uploaded>> = Mutex::new(Vec::new());
    let sent = AtomicU64::new(0);
    let first_err: Mutex<Option<Error>> = Mutex::new(None);
    let overwrite = p.existing != Existing::Fail;
    let result = parallel_for(files.len() as u64, p.conns.max(1), &ctx.cancel, |k| {
        let it = files[k as usize];
        let run = || -> Result<()> {
            let remote = rp::join(&dest, &it.rel);
            ctx.progress.set_current(&it.rel);
            let src = open(it.src)?;
            let add = |n: u64| {
                ctx.progress.add_done(n);
                ctx.progress.add_out(n);
            };
            let crc = if it.size <= DIRECT_MAX {
                upload_direct(
                    client,
                    &remote,
                    it.size,
                    src.as_ref(),
                    DirectOpts {
                        overwrite,
                        want_crc: p.want_crc,
                    },
                    &ctx.cancel,
                    &add,
                )?
            } else {
                send_big(client, &remote, it.size, src.as_ref(), overwrite, p.want_crc, ctx)?
            };
            sent.fetch_add(it.size, Ordering::Relaxed);
            ctx.progress.add_files_done(1);
            if p.want_crc {
                uploaded.lock().unwrap().push(Uploaded {
                    path: remote,
                    size: it.size,
                    crc,
                });
            }
            Ok(())
        };
        let r = run();
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
    Ok(UploadStats {
        files: files.len() as u64,
        dirs: all_dirs.len() as u64,
        bytes: sent.load(Ordering::Relaxed),
        skipped,
        uploaded: uploaded.into_inner().unwrap(),
    })
}

fn send_big(
    client: &Client,
    remote: &str,
    size: u64,
    src: &dyn ReadAt,
    overwrite: bool,
    want_crc: bool,
    ctx: &Ctx,
) -> Result<Option<u32>> {
    let mut w = RemoteWriter::create(client, remote, size, overwrite, &ctx.cancel, Arc::new(|_| {}))?;
    let mut hasher = want_crc.then(crc32fast::Hasher::new);
    let mut buf = vec![0u8; READ_PIECE.min(size.max(1)) as usize];
    let mut off = 0u64;
    while off < size {
        ctx.cancel.check()?;
        let n = (size - off).min(buf.len() as u64) as usize;
        src.read_exact_at(off, &mut buf[..n])?;
        if let Some(h) = hasher.as_mut() {
            h.update(&buf[..n]);
        }
        w.write_all(&buf[..n])?;
        ctx.progress.add_done(n as u64);
        ctx.progress.add_out(n as u64);
        off += n as u64;
    }
    w.finish()?;
    Ok(hasher.map(|h| h.finalize()))
}

pub fn extract_volume_remote(
    vol: &dyn Volume,
    client: &Client,
    dest: &str,
    threads: usize,
    existing: Existing,
    want_crc: bool,
    ctx: &Ctx,
) -> Result<UploadStats> {
    let (total_bytes, nfiles, _) = totals(vol);
    ctx.progress.begin_phase(Stage::Processing, total_bytes);
    ctx.progress.set_files_total(nfiles);
    let items: Vec<Item> = vol
        .entries()
        .iter()
        .enumerate()
        .map(|(i, e)| Item {
            rel: e.path.trim_matches('/').to_string(),
            is_dir: e.is_dir,
            size: e.size,
            src: i,
        })
        .collect();
    let params = UploadParams {
        client,
        dest_dir: dest,
        conns: conns_for(threads),
        existing,
        want_crc,
    };
    upload_items(&params, &items, &|i| vol.open(i), ctx)
}

pub fn verify_uploaded(client: &Client, files: &[Uploaded], conns: usize, ctx: &Ctx) -> Result<()> {
    let total: u64 = files.iter().map(|f| f.size).sum();
    ctx.progress.begin_phase(Stage::Verifying, total);
    let mut order: Vec<&Uploaded> = files.iter().collect();
    order.sort_by_key(|f| std::cmp::Reverse(f.size));
    parallel_for(order.len() as u64, conns.max(1), &ctx.cancel, |k| {
        let f = order[k as usize];
        ctx.progress.set_current(&f.path);
        let (crc, len) = crc_of_remote(client, &f.path, ctx)?;
        if len != f.size {
            return Err(Error::invalid(crate::t!(
                "verr.size",
                path = f.path,
                a = f.size,
                b = len
            )));
        }
        if let Some(want) = f.crc {
            if want != crc {
                return Err(Error::invalid(crate::t!("verr.content", path = f.path)));
            }
        }
        Ok(())
    })
}

pub fn crc_of_remote(client: &Client, path: &str, ctx: &Ctx) -> Result<(u32, u64)> {
    use std::io::Read;
    let (mut resp, expected) = client.open_download(path)?;
    let mut h = crc32fast::Hasher::new();
    let mut buf = vec![0u8; 1 << 20];
    let mut total = 0u64;
    loop {
        ctx.cancel.check()?;
        let n = resp.read(&mut buf).map_err(super::prospero::map_io)?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
        total += n as u64;
        ctx.progress.add_done(n as u64);
    }
    if total != expected {
        return Err(Error::Remote(RemoteError::new(
            RemoteKind::Unreachable,
            0,
            format!("download incompleto: {total} de {expected} bytes"),
        )));
    }
    Ok((h.finalize(), total))
}

pub struct CrcSink<'a> {
    inner: &'a mut dyn SeqSink,
    hasher: crc32fast::Hasher,
    len: u64,
}

impl<'a> CrcSink<'a> {
    pub fn new(inner: &'a mut dyn SeqSink) -> Self {
        Self {
            inner,
            hasher: crc32fast::Hasher::new(),
            len: 0,
        }
    }
    pub fn finish(self) -> (u32, u64) {
        (self.hasher.finalize(), self.len)
    }
}

impl SeqSink for CrcSink<'_> {
    fn write_all(&mut self, buf: &[u8]) -> Result<()> {
        self.hasher.update(buf);
        self.len += buf.len() as u64;
        self.inner.write_all(buf)
    }
}

pub struct LocalTree {
    pub items: Vec<Item>,
    pub files: Vec<std::path::PathBuf>,
}

pub fn scan_local(paths: &[std::path::PathBuf], ctx: &Ctx) -> Result<LocalTree> {
    use crate::util::long_path;
    let mut tree = LocalTree {
        items: Vec::new(),
        files: Vec::new(),
    };
    let name_of = |p: &std::path::Path| -> Result<String> {
        p.file_name()
            .and_then(|n| n.to_str())
            .map(str::to_string)
            .ok_or_else(|| Error::invalid(crate::t!("err.bad_chars", path = p.display())))
    };
    for top in paths {
        ctx.cancel.check()?;
        let md = std::fs::symlink_metadata(long_path(top))
            .map_err(|e| Error::invalid(crate::t!("err.access", path = top.display(), err = e)))?;
        let name = name_of(top)?;
        if md.is_dir() {
            tree.items.push(Item {
                rel: name.clone(),
                is_dir: true,
                size: 0,
                src: 0,
            });
            let mut stack = vec![(top.clone(), name)];
            while let Some((dir, rel)) = stack.pop() {
                ctx.cancel.check()?;
                ctx.progress.set_current(&rel);
                for e in std::fs::read_dir(long_path(&dir))
                    .map_err(|e| Error::invalid(crate::t!("err.access", path = dir.display(), err = e)))?
                {
                    let e = e?;
                    let p = dir.join(e.file_name());
                    let n = name_of(&p)?;
                    let child_rel = format!("{rel}/{n}");
                    let ft = e.file_type()?;
                    if ft.is_symlink() {
                        ctx.progress.warn(format!("symbolic link ignored: {}", p.display()));
                    } else if ft.is_dir() {
                        tree.items.push(Item {
                            rel: child_rel.clone(),
                            is_dir: true,
                            size: 0,
                            src: 0,
                        });
                        stack.push((p, child_rel));
                    } else {
                        let size = e.metadata()?.len();
                        tree.items.push(Item {
                            rel: child_rel,
                            is_dir: false,
                            size,
                            src: tree.files.len(),
                        });
                        tree.files.push(p);
                    }
                }
            }
        } else if md.is_file() {
            tree.items.push(Item {
                rel: name,
                is_dir: false,
                size: md.len(),
                src: tree.files.len(),
            });
            tree.files.push(top.clone());
        } else {
            ctx.progress.warn(format!("ignored: {}", top.display()));
        }
    }
    Ok(tree)
}

pub fn upload_local(
    client: &Client,
    paths: &[std::path::PathBuf],
    dest_dir: &str,
    policy: Conflict,
    conns: usize,
    ctx: &Ctx,
) -> Result<UploadStats> {
    ctx.progress.begin_phase(Stage::Scanning, 0);
    let mut tree = scan_local(paths, ctx)?;
    let dest = rp::norm(dest_dir);

    let mut tops: Vec<String> = tree
        .items
        .iter()
        .map(|i| i.rel.split('/').next().unwrap_or("").to_string())
        .collect();
    tops.sort();
    tops.dedup();
    let top_paths: Vec<String> = tops.iter().map(|t| rp::join(&dest, t)).collect();
    let present = client.conflicts(&top_paths).unwrap_or_default();
    let mut existing: Existing = policy.into();
    if !present.is_empty() {
        match policy {
            Conflict::Cancel => {
                return Err(Error::Remote(RemoteError::new(
                    RemoteKind::Exists,
                    409,
                    crate::t!("err.exists", path = present[0].clone()),
                )));
            }
            Conflict::KeepBoth => {
                let renamed = client.unique_names(&top_paths)?;
                for (old, new) in top_paths.iter().zip(renamed.iter()) {
                    if old != new {
                        let (o, n) = (rp::file_name(old), rp::file_name(new));
                        for it in tree.items.iter_mut() {
                            if it.rel == o || it.rel.starts_with(&format!("{o}/")) {
                                it.rel = format!("{n}{}", &it.rel[o.len()..]);
                            }
                        }
                    }
                }
                existing = Existing::Fail;
            }
            _ => {}
        }
    } else if matches!(policy, Conflict::KeepBoth | Conflict::Cancel) {
        existing = Existing::Fail;
    }

    let total: u64 = tree.items.iter().filter(|i| !i.is_dir).map(|i| i.size).sum();
    let nfiles = tree.items.iter().filter(|i| !i.is_dir).count() as u64;
    ctx.progress.begin_phase(Stage::Processing, total);
    ctx.progress.set_files_total(nfiles);
    client.mkdir_all(&dest, Some(&ctx.cancel))?;
    if total > 0 {
        let pf = client.preflight(&dest, total, true)?;
        if !pf.ok {
            return Err(Error::invalid(crate::t!(
                "err.no_space",
                free = crate::util::human_bytes(pf.available),
                need = crate::util::human_bytes(total)
            )));
        }
    }
    let files = tree.files.clone();
    let params = UploadParams {
        client,
        dest_dir: &dest,
        conns,
        existing,
        want_crc: false,
    };
    upload_items(
        &params,
        &tree.items,
        &|i| -> Result<Arc<dyn ReadAt>> { Ok(Arc::new(crate::io::FileReader::open(&files[i])?)) },
        ctx,
    )
}
