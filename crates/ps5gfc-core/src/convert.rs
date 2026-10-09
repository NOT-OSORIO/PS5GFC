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

use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::sync::Arc;
use std::time::Instant;

use serde::{Deserialize, Serialize};

use crate::ctl::{Ctx, Stage};
use crate::emit::write_image;
use crate::exfat::{self, ExfatOptions};
use crate::extract::extract_volume;
use crate::io::ReadAt;
use crate::pfs::{self, ContainerOptions, PfsTreeOptions};
use crate::pfsc::PfscOptions;
use crate::remote::engine::{conns_for, crc_of_remote, extract_volume_remote, verify_uploaded, CrcSink, Existing};
use crate::remote::path as rp;
use crate::remote::stream::write_image_stream;
use crate::remote::upload::RemoteWriter;
use crate::remote::Client;
use crate::source::{open_source, Format, FsKind, OpenedSource};
use crate::sys::WorkerPriority;
use crate::title::{output_name, output_stem, read_title, sanitize_name, NameMode};
use crate::ufs2::{self, Ufs2Options};
use crate::util::{default_threads, human_bytes, GIB, MIB};
use crate::volume::{totals, Volume};
use crate::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum AmprMode {
    #[default]
    Auto,

    Always,

    Never,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct ConvertOptions {
    pub threads: usize,

    pub level: u32,
    pub threshold_gain_pct: f32,
    pub skip_incompressible: bool,

    pub rebuild: bool,

    pub cluster_kib: u32,
    pub preserve_times: bool,

    pub free_mib: u64,
    pub overwrite: bool,
    pub verify: bool,
    pub ampr: AmprMode,

    pub priority: WorkerPriority,

    pub out_name: Option<String>,

    pub name_mode: Option<NameMode>,
}

impl Default for ConvertOptions {
    fn default() -> Self {
        Self {
            threads: 0,
            level: 7,
            threshold_gain_pct: 0.0,
            skip_incompressible: true,
            rebuild: false,
            cluster_kib: 64,
            preserve_times: true,
            free_mib: 0,
            overwrite: false,
            verify: false,
            ampr: AmprMode::Auto,
            priority: WorkerPriority::LowPerf,
            out_name: None,
            name_mode: None,
        }
    }
}

impl ConvertOptions {
    pub fn worker_threads(&self) -> usize {
        if self.threads == 0 {
            default_threads()
        } else {
            self.threads
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Route {
    Extract,

    ReuseImage,

    Build,
}

#[derive(Debug, Clone, Serialize)]
pub struct ConvertReport {
    pub output: PathBuf,

    #[serde(default)]
    pub console: bool,
    pub route: Route,
    pub source_format: Format,
    pub target_format: Format,
    pub in_bytes: u64,
    pub out_bytes: u64,
    pub files: u64,
    pub elapsed_ms: u64,
    pub warnings: Vec<String>,
}

pub fn ampr_needed(vol: &dyn Volume, mode: AmprMode) -> bool {
    match mode {
        AmprMode::Never => false,
        AmprMode::Always => vol.entries().iter().any(|e| !e.is_dir),
        AmprMode::Auto => {
            vol.find(crate::ampr::MARKER).is_some()
                && vol.find(crate::ampr::PACK_INDEX).is_none()
                && !crate::ampr::index_is_current(vol)
        }
    }
}

pub fn effective_volume(vol: Arc<dyn Volume>, mode: AmprMode) -> (Arc<dyn Volume>, bool) {
    if !ampr_needed(vol.as_ref(), mode) {
        return (vol, false);
    }
    match crate::ampr::build_index(vol.entries()) {
        Some(idx) => {
            let o = crate::overlay::OverlayVolume::new(vol).with_file(crate::ampr::INDEX_NAME, idx);
            (Arc::new(o), true)
        }
        None => (vol, false),
    }
}

pub fn choose_route(src: &OpenedSource, target: Format, opts: &ConvertOptions) -> Route {
    match target {
        Format::Folder => Route::Extract,
        Format::Pkg => Route::Build,
        Format::Image(k) | Format::Ffpfsc(k) => {
            if !opts.rebuild
                && src.fs_image.is_some()
                && src.format.inner_fs() == Some(k)
                && !ampr_needed(src.volume.as_ref(), opts.ampr)
            {
                Route::ReuseImage
            } else {
                Route::Build
            }
        }
    }
}

pub fn build_fs_image(
    kind: FsKind,
    vol: Arc<dyn Volume>,
    opts: &ConvertOptions,
) -> Result<(Arc<dyn ReadAt>, Vec<String>)> {
    match kind {
        FsKind::Exfat => {
            let eo = ExfatOptions {
                cluster_size: opts.cluster_kib.max(1) * 1024,
                preserve_times: opts.preserve_times,
                free_bytes: opts.free_mib * MIB,
                ..Default::default()
            };
            let plan = exfat::plan(vol, &eo)?;
            Ok((Arc::new(plan.image), Vec::new()))
        }
        FsKind::Ufs2 => {
            let mut uo = Ufs2Options {
                free_bytes: opts.free_mib * MIB,
                preserve_times: opts.preserve_times,
                ..Default::default()
            };
            if !opts.preserve_times {
                uo.timestamp = 1_704_067_200;
            }
            let plan = ufs2::plan(vol, &uo)?;
            Ok((Arc::new(plan.image), Vec::new()))
        }
        FsKind::Pfs => {
            let plan = pfs::plan_tree(vol, &PfsTreeOptions::default())?;
            Ok((Arc::new(plan.image), Vec::new()))
        }
    }
}

pub fn resolve_output(stem: &str, name: Option<&str>, dest: &Path, target: Format) -> PathBuf {
    let stem = name.unwrap_or(stem);
    match target {
        Format::Folder => match name {
            Some(n) => dest.join(n),
            None => dest.to_path_buf(),
        },
        _ => {
            let ext = target.extension();
            let has_ext = dest
                .extension()
                .map(|e| e.to_string_lossy().eq_ignore_ascii_case(ext))
                .unwrap_or(false);
            if dest.is_dir() || !has_ext && dest.extension().is_none() {
                dest.join(format!("{stem}.{ext}"))
            } else {
                dest.to_path_buf()
            }
        }
    }
}

fn staging_parent(dest: &Path) -> PathBuf {
    let dest = std::path::absolute(dest).unwrap_or_else(|_| dest.to_path_buf());
    if dest.is_dir() {
        return dest;
    }
    match dest.parent() {
        Some(p) if !p.as_os_str().is_empty() => p.to_path_buf(),
        _ => PathBuf::from("."),
    }
}

fn same_path(a: &Path, b: &Path) -> bool {
    match (std::fs::canonicalize(a), std::fs::canonicalize(b)) {
        (Ok(x), Ok(y)) => x == y,
        _ => a == b,
    }
}

#[derive(Clone)]
pub enum Dest {
    Local(PathBuf),

    Console { client: Client, dir: String },
}

pub fn resolve_console_output(stem: &str, name: Option<&str>, dir: &str, target: Format) -> String {
    let stem = name.unwrap_or(stem);
    match target {
        Format::Folder => match name {
            Some(n) => rp::join(dir, n),
            None => rp::norm(dir),
        },
        _ => {
            let ext = target.extension();
            let d = rp::norm(dir);
            let has_ext = rp::file_name(&d)
                .rsplit_once('.')
                .map(|(_, e)| e.eq_ignore_ascii_case(ext))
                .unwrap_or(false);
            if has_ext {
                d
            } else {
                rp::join(&d, &format!("{stem}.{ext}"))
            }
        }
    }
}

pub fn convert(
    src_path: &Path,
    dest: &Path,
    target: Format,
    opts: &ConvertOptions,
    ctx: &Ctx,
) -> Result<ConvertReport> {
    convert_to(src_path, &Dest::Local(dest.to_path_buf()), target, opts, ctx)
}

pub fn convert_to(
    src_path: &Path,
    dest: &Dest,
    target: Format,
    opts: &ConvertOptions,
    ctx: &Ctx,
) -> Result<ConvertReport> {
    let t0 = Instant::now();
    let mut warnings: Vec<String> = Vec::new();
    let threads = opts.worker_threads();
    crate::sys::set_worker_priority(opts.priority);
    let _awake = crate::sys::AwakeGuard::new();

    ctx.progress.begin_phase(Stage::Scanning, 0);
    ctx.progress.info(crate::tl!("log.opening", path = src_path.display()));
    let src = open_source(src_path, ctx)?;
    ctx.cancel.check()?;
    if src.format.same_kind(target) {
        return Err(Error::invalid(crate::t!("err.same_format")));
    }

    let mut _staged: Option<crate::pkgsrc::StagedTree> = None;
    let src = if src.format == Format::Pkg {
        let total = totals(src.volume.as_ref()).0;
        let parent = match dest {
            Dest::Local(d) => staging_parent(d),
            Dest::Console { .. } => std::env::temp_dir(),
        };
        let staged = crate::pkgsrc::stage(src_path, &parent, total, ctx)?;
        ctx.cancel.check()?;
        let tree = open_source(&staged.tree(), ctx)?;
        _staged = Some(staged);
        OpenedSource {
            path: src.path.clone(),
            format: Format::Pkg,
            volume: tree.volume,
            fs_image: None,
            wrapper: None,
        }
    } else {
        src
    };
    let (vol, ampr_injected) = effective_volume(src.volume.clone(), opts.ampr);
    if ampr_injected {
        ctx.progress.info(crate::tl!("log.ampr"));
    }
    let (in_bytes, nfiles, _) = totals(vol.as_ref());
    let title = read_title(vol.as_ref());
    let fallback_stem = src_path
        .file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| "image".into());
    let stem = output_stem(&title, &fallback_stem);

    for issue in crate::inspect::precheck(vol.as_ref(), target) {
        match issue.severity {
            crate::inspect::Severity::Error => return Err(Error::invalid(issue.msg)),
            _ => warnings.push(issue.msg),
        }
    }
    if target == Format::Pkg && title.content_id.is_none() {
        return Err(Error::invalid(crate::t!("err.no_contentid")));
    }
    let route = choose_route(&src, target, opts);
    let chosen_name = opts
        .out_name
        .as_deref()
        .map(sanitize_name)
        .filter(|n| !n.is_empty())
        .or_else(|| opts.name_mode.map(|m| output_name(&title, m, &fallback_stem)));

    let out_path: PathBuf = match dest {
        Dest::Local(d) => resolve_output(&stem, chosen_name.as_deref(), d, target),
        Dest::Console { dir, .. } => PathBuf::from(resolve_console_output(&stem, chosen_name.as_deref(), dir, target)),
    };
    let out_str = out_path.to_string_lossy().replace('\\', "/");
    let out_display = match dest {
        Dest::Local(_) => out_path.display().to_string(),
        Dest::Console { .. } => out_str.clone(),
    };
    match dest {
        Dest::Local(_) => {
            if same_path(&out_path, src_path) {
                return Err(Error::invalid(crate::t!("err.same_path")));
            }
            if target != Format::Folder && out_path.exists() && !opts.overwrite {
                return Err(Error::invalid(crate::t!("err.exists", path = out_path.display())));
            }
            if target == Format::Folder && out_path.exists() && !opts.overwrite {
                let non_empty = std::fs::read_dir(&out_path)
                    .map(|mut d| d.next().is_some())
                    .unwrap_or(false);
                if non_empty {
                    return Err(Error::invalid(crate::t!(
                        "err.dir_not_empty",
                        path = out_path.display()
                    )));
                }
            }
        }
        Dest::Console { client, .. } => {
            if target == Format::Pkg {
                return Err(Error::unsupported(crate::t!("err.pkg_console")));
            }
            ctx.progress.info(crate::tl!(
                "log.rm.target",
                host = client.endpoint().authority(),
                path = out_str
            ));
            match client.stat(&out_str)? {
                Some(e) if target == Format::Folder => {
                    if !e.is_dir {
                        return Err(Error::invalid(crate::t!("err.exists", path = out_str)));
                    }
                    if !opts.overwrite && !client.list(&out_str)?.is_empty() {
                        return Err(Error::invalid(crate::t!("err.dir_not_empty", path = out_str)));
                    }
                }
                Some(e) if !opts.overwrite || e.is_dir => {
                    return Err(Error::invalid(crate::t!("err.exists", path = out_str)))
                }
                _ => {}
            }
        }
    }
    ctx.progress.info(crate::tl!(
        "log.summary",
        src = src.format.label(),
        dst = target.label(),
        files = nfiles,
        size = human_bytes(in_bytes)
    ));

    let free = match dest {
        Dest::Local(_) => crate::sys::free_space(&out_path),
        Dest::Console { client, .. } => client
            .storage()
            .ok()
            .and_then(|v| client.volume_of(&v, &out_str).map(|x| x.free)),
    };
    if let Some(free) = free {
        let need = match target {
            Format::Folder => in_bytes,
            Format::Pkg => in_bytes,
            _ => in_bytes / 2,
        };
        if free < need {
            return Err(Error::invalid(crate::t!(
                "err.no_space",
                free = human_bytes(free),
                need = human_bytes(need)
            )));
        }
        if matches!(target, Format::Image(_)) && free < in_bytes + GIB {
            warnings.push(crate::t!("warn.low_space", free = human_bytes(free)));
        }
    }

    let out_bytes: u64;

    let mut sent_crc: Option<(u32, u64)> = None;
    let mut uploaded: Vec<crate::remote::engine::Uploaded> = Vec::new();
    match target {
        Format::Folder => {
            ctx.progress.info(crate::tl!("log.extracting"));
            match dest {
                Dest::Local(_) => {
                    extract_volume(vol.as_ref(), &out_path, threads, ctx)?;
                }
                Dest::Console { client, .. } => {
                    ctx.progress
                        .info(crate::tl!("log.rm.folder", conns = conns_for(threads)));
                    let existing = if opts.overwrite {
                        Existing::Replace
                    } else {
                        Existing::Fail
                    };
                    let st =
                        extract_volume_remote(vol.as_ref(), client, &out_str, threads, existing, opts.verify, ctx)?;
                    uploaded = st.uploaded;
                }
            }
            out_bytes = in_bytes;
        }
        Format::Pkg => {
            let Dest::Local(_) = dest else {
                return Err(Error::unsupported(crate::t!("err.pkg_console")));
            };
            let content_id = title.content_id.clone().expect("checked above");
            ctx.progress.info(crate::tl!("log.pkg.prepare"));

            let mut staging: Option<tempfile::TempDir> = None;
            let input = if ampr_injected {
                None
            } else if src.format == Format::Folder {
                (!folder_has_ignored_names(&src.path)).then(|| PkgInput::Tree(src.path.clone()))
            } else {
                pkg_reads_image_directly(src.format).then(|| PkgInput::Image(src.path.clone()))
            };
            let input = match input {
                Some(i) => {
                    if matches!(i, PkgInput::Image(_)) {
                        ctx.progress.info(crate::tl!("log.pkg.direct"));
                    }
                    i
                }
                None => {
                    check_staging_space(in_bytes)?;
                    let dir = tempfile::Builder::new().prefix("ps5gfc-pkg-").tempdir()?;
                    let tree = dir.path().join("tree");
                    ctx.progress.info(crate::tl!("log.pkg.stage"));
                    extract_volume(vol.as_ref(), &tree, threads, ctx)?;
                    staging = Some(dir);
                    PkgInput::Tree(tree)
                }
            };
            out_bytes = build_pkg(&input, &out_path, &content_id, opts, in_bytes, ctx)?;
            drop(staging);
        }
        Format::Image(k) | Format::Ffpfsc(k) => {
            ctx.progress.begin_phase(Stage::Planning, 0);
            let image: Arc<dyn ReadAt> = match route {
                Route::ReuseImage => {
                    ctx.progress.info(crate::tl!("log.reuse"));
                    src.fs_image.clone().expect("rota ReuseImage exige fs_image")
                }
                _ => {
                    ctx.progress.info(crate::tl!("log.planning", fs = k.label()));
                    let (img, w) = build_fs_image(k, vol.clone(), opts)?;
                    ctx.cancel.check()?;
                    warnings.extend(w);
                    img
                }
            };
            ctx.progress
                .info(crate::tl!("log.logical", size = human_bytes(image.len())));
            match target {
                Format::Image(_) => match dest {
                    Dest::Local(_) => {
                        out_bytes = write_image(image.as_ref(), &out_path, threads, ctx)?;
                    }
                    Dest::Console { client, .. } => {
                        client.mkdir_all(&rp::parent(&out_str), Some(&ctx.cancel))?;
                        precheck_console_space(client, &out_str, image.len())?;
                        ctx.progress.info(crate::tl!("log.rm.send_image"));
                        let mut w = RemoteWriter::create(
                            client,
                            &out_str,
                            image.len(),
                            opts.overwrite,
                            &ctx.cancel,
                            Arc::new(|_| {}),
                        )?;
                        let mut tee = CrcSink::new(&mut w);
                        out_bytes = write_image_stream(image.as_ref(), &mut tee, threads, ctx)?;
                        sent_crc = Some(tee.finish());
                        ctx.progress.set_stage(Stage::Finalizing);
                        w.finish()?;
                    }
                },
                Format::Ffpfsc(_) => {
                    let inner_name = if k == FsKind::Pfs {
                        "pfs_image.dat".to_string()
                    } else {
                        pfs::safe_inner_name(&format!("{stem}.{}", Format::Image(k).extension()))
                    };
                    let co = ContainerOptions {
                        pfsc: PfscOptions {
                            level: opts.level,
                            threshold_gain_pct: opts.threshold_gain_pct,
                            skip_incompressible: opts.skip_incompressible,
                            threads,
                        },
                        ..Default::default()
                    };
                    ctx.progress
                        .info(crate::tl!("log.compress", threads = threads, level = opts.level));
                    let st = match dest {
                        Dest::Local(_) => pfs::write_container(image.as_ref(), &inner_name, &out_path, &co, ctx)?,
                        Dest::Console { client, .. } => {
                            ctx.progress.info(crate::tl!("log.rm.measure"));
                            ctx.progress.begin_phase(Stage::Planning, image.len());
                            let plan = pfs::plan_container(image.as_ref(), &inner_name, &co, ctx)?;
                            ctx.cancel.check()?;
                            client.mkdir_all(&rp::parent(&out_str), Some(&ctx.cancel))?;
                            precheck_console_space(client, &out_str, plan.file_len())?;
                            ctx.progress
                                .info(crate::tl!("log.rm.send", size = human_bytes(plan.file_len())));
                            ctx.progress.begin_phase(Stage::Processing, image.len());
                            let mut w = RemoteWriter::create(
                                client,
                                &out_str,
                                plan.file_len(),
                                opts.overwrite,
                                &ctx.cancel,
                                Arc::new(|_| {}),
                            )?;
                            let mut tee = CrcSink::new(&mut w);
                            let st = plan.write(image.as_ref(), &co, &mut tee, ctx)?;
                            sent_crc = Some(tee.finish());
                            ctx.progress.set_stage(Stage::Finalizing);
                            w.finish()?;
                            st
                        }
                    };
                    out_bytes = st.file_len;
                    ctx.progress.info(crate::tl!(
                        "log.pfsc_stats",
                        blocks = st.pfsc.blocks,
                        zero = st.pfsc.blocks_zero,
                        raw = st.pfsc.blocks_raw,
                        skipped = st.pfsc.blocks_skipped,
                        read = format!("{:.1}", st.pfsc.ms_read as f64 / 1000.0),
                        enc = format!("{:.1}", st.pfsc.ms_encode as f64 / 1000.0),
                        write = format!("{:.1}", st.pfsc.ms_write as f64 / 1000.0)
                    ));
                }
                Format::Folder | Format::Pkg => unreachable!(),
            }
        }
    }

    if opts.verify {
        match dest {
            Dest::Local(_) if target == Format::Pkg => {
                ctx.progress.info(crate::tl!("log.pkg.verified"));
            }
            Dest::Local(_) => {
                ctx.progress.begin_phase(Stage::Verifying, in_bytes * 2);
                ctx.progress.info(crate::tl!("log.verify"));
                crate::verify::verify_output(vol.as_ref(), &out_path, target, threads, ctx)?;
            }
            Dest::Console { client, .. } => {
                ctx.progress.info(crate::tl!("log.rm.verify"));
                match sent_crc {
                    Some((crc, len)) => {
                        ctx.progress.begin_phase(Stage::Verifying, len);
                        ctx.progress.set_current(&out_str);
                        let (got, got_len) = crc_of_remote(client, &out_str, ctx)?;
                        if got_len != len {
                            return Err(Error::invalid(crate::t!(
                                "verr.size",
                                path = out_str,
                                a = len,
                                b = got_len
                            )));
                        }
                        if got != crc {
                            return Err(Error::invalid(crate::t!("verr.content", path = out_str)));
                        }
                    }
                    None => verify_uploaded(client, &uploaded, conns_for(threads), ctx)?,
                }
            }
        }
    }

    ctx.progress.set_stage(Stage::Done);
    ctx.progress.info(crate::tl!("log.done", path = out_display));
    Ok(ConvertReport {
        output: out_path,
        console: matches!(dest, Dest::Console { .. }),
        route,
        source_format: src.format,
        target_format: target,
        in_bytes,
        out_bytes,
        files: nfiles,
        elapsed_ms: t0.elapsed().as_millis() as u64,
        warnings,
    })
}

enum PkgInput {
    Tree(PathBuf),

    Image(PathBuf),
}

fn pkg_reads_image_directly(format: Format) -> bool {
    matches!(
        format,
        Format::Image(FsKind::Exfat | FsKind::Ufs2) | Format::Ffpfsc(FsKind::Exfat | FsKind::Ufs2)
    )
}

fn folder_has_ignored_names(root: &Path) -> bool {
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(crate::util::long_path(&dir)) else {
            continue;
        };
        for e in rd.flatten() {
            if crate::util::is_ignored_name(&e.file_name().to_string_lossy()) {
                return true;
            }
            if e.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                stack.push(e.path());
            }
        }
    }
    false
}

fn check_staging_space(need: u64) -> Result<()> {
    let tmp = std::env::temp_dir();
    match crate::sys::free_space(&tmp) {
        Some(free) if free < need + GIB => Err(Error::invalid(crate::t!(
            "err.pkg_stage_space",
            path = tmp.display(),
            free = human_bytes(free),
            need = human_bytes(need)
        ))),
        _ => Ok(()),
    }
}

#[derive(Debug)]
pub(crate) enum PkgBuilder {
    Exe(PathBuf),
    DotnetProject(PathBuf),
}

impl PkgBuilder {
    pub(crate) fn command(&self) -> Command {
        let mut c = match self {
            PkgBuilder::Exe(exe) => Command::new(exe),
            PkgBuilder::DotnetProject(project) => {
                let mut c = Command::new("dotnet");
                c.arg("run")
                    .arg("--project")
                    .arg(project)
                    .arg("-c")
                    .arg("Release")
                    .arg("--");
                c
            }
        };
        if std::env::var_os("PS5GFC_DATA_DLL").is_none() {
            if let Some(dll) = console_data_dll() {
                c.env("PS5GFC_DATA_DLL", dll);
            }
        }
        c
    }
}

fn console_data_dll() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let dll = exe.parent()?.join("dll's").join("ProsperoPkgTool.Data.dll");
    dll.is_file().then_some(dll)
}

fn project_root_candidates() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        roots.extend(exe.ancestors().map(Path::to_path_buf));
    }
    if let Ok(cwd) = std::env::current_dir() {
        roots.extend(cwd.ancestors().map(Path::to_path_buf));
    }
    #[cfg(debug_assertions)]
    roots.extend(Path::new(env!("CARGO_MANIFEST_DIR")).ancestors().map(Path::to_path_buf));
    roots
}

pub(crate) fn find_pkg_builder() -> Result<PkgBuilder> {
    if let Some(p) = std::env::var_os("PS5GFC_PKG_BUILDER").map(PathBuf::from) {
        if p.is_file() {
            return Ok(PkgBuilder::Exe(p));
        }
    }
    if let Some(p) = crate::pkgembed::extract() {
        return Ok(PkgBuilder::Exe(p));
    }
    let mut seen = std::collections::HashSet::new();
    for root in project_root_candidates() {
        if !seen.insert(root.clone()) {
            continue;
        }
        for exe in [
            root.join("pkg-builder").join("ps5pkg-builder.exe"),
            root.join("ps5pkg-builder.exe"),
            root.join("tools")
                .join("ps5pkg-builder")
                .join("bin")
                .join("Release")
                .join("net10.0")
                .join("ps5pkg-builder.exe"),
        ] {
            if exe.is_file() {
                return Ok(PkgBuilder::Exe(exe));
            }
        }
        let project = root.join("tools").join("ps5pkg-builder").join("ps5pkg-builder.csproj");
        if project.is_file() {
            return Ok(PkgBuilder::DotnetProject(project));
        }
    }
    Err(Error::unsupported(crate::t!("err.pkg_builder_missing")))
}

fn pkg_work_dir(output: &Path) -> PathBuf {
    let parent = output
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    parent.join(format!(".ps5gfc-pkg-{}-{nanos:x}", std::process::id()))
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum PkgMsg {
    Progress {
        stage: String,
        done: u64,
        total: u64,
        current: String,
    },
    Log(String),
    Result {
        package_size: Option<u64>,
    },

    Entry {
        path: String,
        size: u64,
    },
    Text(String),
}

pub(crate) fn parse_pkg_line(line: &str) -> Option<PkgMsg> {
    let line = line.trim_end();
    if line.trim().is_empty() {
        return None;
    }
    if let Some(rest) = line.strip_prefix("PROGRESS ") {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(rest) {
            let text = |k: &str| v.get(k).and_then(|x| x.as_str()).unwrap_or("").to_string();
            let num = |k: &str| v.get(k).and_then(|x| x.as_u64()).unwrap_or(0);
            return Some(PkgMsg::Progress {
                stage: text("stage"),
                done: num("done"),
                total: num("total"),
                current: text("current"),
            });
        }
    } else if let Some(rest) = line.strip_prefix("ENTRY ") {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(rest) {
            if let Some(path) = v.get("path").and_then(|x| x.as_str()) {
                return Some(PkgMsg::Entry {
                    path: path.to_string(),
                    size: v.get("size").and_then(|x| x.as_u64()).unwrap_or(0),
                });
            }
        }
    } else if let Some(rest) = line.strip_prefix("LOG ") {
        return Some(PkgMsg::Log(rest.to_string()));
    } else if let Some(rest) = line.strip_prefix("RESULT ") {
        let package_size = serde_json::from_str::<serde_json::Value>(rest)
            .ok()
            .and_then(|v| v.get("packageSize").and_then(|x| x.as_u64()));
        return Some(PkgMsg::Result { package_size });
    }
    Some(PkgMsg::Text(line.to_string()))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PkgStageKind {
    Items,

    Bytes,

    Verify,
}

fn pkg_stage_kind(stage: &str) -> PkgStageKind {
    let s = stage.to_ascii_lowercase();
    if s.starts_with("reading") {
        PkgStageKind::Items
    } else if s.starts_with("verif") {
        PkgStageKind::Verify
    } else {
        PkgStageKind::Bytes
    }
}

#[derive(Default)]
pub(crate) struct PkgProgress {
    scale: u64,
    stage: String,
    last_done: u64,
}

impl PkgProgress {
    pub(crate) fn new(scale: u64) -> Self {
        Self {
            scale,
            ..Default::default()
        }
    }

    pub(crate) fn apply(&mut self, ctx: &Ctx, stage: &str, done: u64, total: u64, current: &str) {
        let kind = pkg_stage_kind(stage);
        let (done, total) = match kind {
            PkgStageKind::Bytes if self.scale > 0 && total > 0 => (
                (u128::from(done.min(total)) * u128::from(self.scale) / u128::from(total)) as u64,
                self.scale,
            ),
            _ => (done, total),
        };
        if stage != self.stage {
            self.stage = stage.to_string();
            self.last_done = 0;
            match kind {
                PkgStageKind::Items => ctx.progress.begin_phase(Stage::Scanning, 0),
                PkgStageKind::Bytes => ctx.progress.begin_phase(Stage::Processing, total),
                PkgStageKind::Verify => ctx.progress.begin_phase(Stage::Verifying, 0),
            }
        }
        let delta = done.saturating_sub(self.last_done);
        self.last_done = self.last_done.max(done);
        match kind {
            PkgStageKind::Items => {
                ctx.progress.set_files_total(total);
                ctx.progress.add_files_done(delta);
            }
            PkgStageKind::Bytes => {
                if total > 0 {
                    ctx.progress.set_total(total);
                }
                ctx.progress.add_done(delta);
            }
            PkgStageKind::Verify => {}
        }
        if !current.is_empty() {
            ctx.progress.set_current(current);
        }
    }
}

fn handle_pkg_line(ctx: &Ctx, prog: &mut PkgProgress, line: &str) {
    match parse_pkg_line(line) {
        Some(PkgMsg::Progress {
            stage,
            done,
            total,
            current,
        }) => prog.apply(ctx, &stage, done, total, &current),
        Some(PkgMsg::Log(msg)) | Some(PkgMsg::Text(msg)) => ctx.progress.info(msg.trim().to_string()),
        Some(PkgMsg::Result { .. }) | Some(PkgMsg::Entry { .. }) | None => {}
    }
}

fn spawn_line_reader<R: std::io::Read + Send + 'static>(
    r: R,
    is_err: bool,
    tx: mpsc::Sender<(bool, String)>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let mut r = BufReader::new(r);
        let mut buf = Vec::new();
        loop {
            buf.clear();
            match r.read_until(b'\n', &mut buf) {
                Ok(0) | Err(_) => break,
                Ok(_) => {
                    let line = String::from_utf8_lossy(&buf).trim_end_matches(['\r', '\n']).to_string();
                    if tx.send((is_err, line)).is_err() {
                        break;
                    }
                }
            }
        }
    })
}

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
#[cfg(windows)]
const BELOW_NORMAL_PRIORITY_CLASS: u32 = 0x0000_4000;

fn kill_tree(child: &mut std::process::Child) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let _ = Command::new("taskkill")
            .args(["/PID", &child.id().to_string(), "/T", "/F"])
            .creation_flags(CREATE_NO_WINDOW)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
    let _ = child.kill();
}

fn build_pkg(
    input: &PkgInput,
    output: &Path,
    content_id: &str,
    opts: &ConvertOptions,
    in_bytes: u64,
    ctx: &Ctx,
) -> Result<u64> {
    ctx.progress.begin_phase(Stage::Processing, 0);
    ctx.progress.info(crate::tl!("log.pkg.build"));

    let builder = find_pkg_builder()?;
    let work = pkg_work_dir(output);
    let result = run_pkg_builder(&builder, input, output, &work, content_id, opts, in_bytes, ctx);
    let _ = std::fs::remove_dir_all(crate::util::long_path(&work));
    result?;

    let len = std::fs::metadata(crate::util::long_path(output))?.len();
    ctx.progress
        .add_out(len.saturating_sub(ctx.progress.snapshot().out_bytes));
    Ok(len)
}

#[allow(clippy::too_many_arguments)]
fn run_pkg_builder(
    builder: &PkgBuilder,
    input: &PkgInput,
    output: &Path,
    work: &Path,
    content_id: &str,
    opts: &ConvertOptions,
    in_bytes: u64,
    ctx: &Ctx,
) -> Result<()> {
    let mut cmd = builder.command();
    match input {
        PkgInput::Tree(p) => cmd.arg("--source").arg(p),
        PkgInput::Image(p) => cmd.arg("--image").arg(p),
    };
    cmd.arg("--output")
        .arg(output)
        .arg("--content-id")
        .arg(content_id)
        .arg("--work")
        .arg(work)
        .arg("--threads")
        .arg(opts.worker_threads().to_string())
        .arg("--stdin-control");
    if opts.overwrite {
        cmd.arg("--overwrite");
    }
    if opts.verify {
        cmd.arg("--verify");
    }
    if !opts.preserve_times {
        cmd.arg("--deterministic");
    }
    let mut prog = PkgProgress::new(in_bytes);
    run_builder(cmd, opts.priority, Some(output), ctx, |line| {
        handle_pkg_line(ctx, &mut prog, line)
    })
}

pub(crate) fn run_builder(
    mut cmd: Command,
    priority: crate::sys::WorkerPriority,
    remove_on_cancel: Option<&Path>,
    ctx: &Ctx,
    mut on_stdout: impl FnMut(&str),
) -> Result<()> {
    cmd.arg("--stdin-control");
    cmd.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;

        let mut flags = CREATE_NO_WINDOW;
        if priority != crate::sys::WorkerPriority::Normal {
            flags |= BELOW_NORMAL_PRIORITY_CLASS;
        }
        cmd.creation_flags(flags);
    }
    #[cfg(not(windows))]
    let _ = priority;

    let mut child = cmd
        .spawn()
        .map_err(|e| Error::unsupported(crate::t!("err.pkg_builder_spawn", e = e)))?;
    let mut stdin = child.stdin.take();
    let (tx, rx) = mpsc::channel::<(bool, String)>();
    let readers = [
        spawn_line_reader(child.stdout.take().expect("stdout piped"), false, tx.clone()),
        spawn_line_reader(child.stderr.take().expect("stderr piped"), true, tx),
    ];

    let mut last_error = String::new();
    let mut handle = |is_err: bool, line: &str, last_error: &mut String| {
        if is_err {
            if !line.trim().is_empty() {
                *last_error = line.trim().to_string();
                ctx.progress.warn(line.trim().to_string());
            }
        } else {
            on_stdout(line);
        }
    };
    let mut cancel_sent: Option<Instant> = None;
    loop {
        if ctx.cancel.is_cancelled() {
            match cancel_sent {
                None => {
                    if let Some(mut s) = stdin.take() {
                        let _ = s.write_all(b"CANCEL\n");
                        let _ = s.flush();
                    }
                    cancel_sent = Some(Instant::now());
                }
                Some(t) if t.elapsed() > std::time::Duration::from_secs(30) => {
                    kill_tree(&mut child);
                    let _ = child.wait();
                    return Err(Error::Cancelled);
                }
                Some(_) => {}
            }
        }
        if let Ok((is_err, line)) = rx.recv_timeout(std::time::Duration::from_millis(100)) {
            handle(is_err, &line, &mut last_error);
        }
        if let Some(status) = child.try_wait()? {
            for r in readers {
                let _ = r.join();
            }
            while let Ok((is_err, line)) = rx.try_recv() {
                handle(is_err, &line, &mut last_error);
            }
            if ctx.cancel.is_cancelled() {
                if status.success() {
                    if let Some(p) = remove_on_cancel {
                        let _ = std::fs::remove_file(crate::util::long_path(p));
                    }
                }
                return Err(Error::Cancelled);
            }
            if !status.success() {
                return Err(Error::invalid(if last_error.contains("ProsperoPkgTool.Data.dll") {
                    crate::t!("err.pkg_missing_dll")
                } else if let Some(msg) = translate_disk_space_error(&last_error) {
                    msg
                } else if last_error.is_empty() {
                    crate::t!("err.pkg_builder_failed")
                } else {
                    last_error
                }));
            }
            return Ok(());
        }
    }
}

/// The engine (`tools/ps5pkg-engine`, `ProsperoPkgTool.Containers.DiskSpaceGuard`) reports insufficient
/// disk space as a fixed English sentence on stderr, e.g.:
/// "not enough free space: need ~57.7 GB on C:\ (temp workspace + output), have 40.8 GB free"
/// It is parsed here (instead of changed at the source, to keep that decompiled engine a minimal diff from
/// the original) and turned into a localized message for the job's visible error; the raw English line is
/// still what shows up in the log, matching every other PKG message.
fn translate_disk_space_error(last_error: &str) -> Option<String> {
    let rest = last_error.strip_prefix("not enough free space: need ~")?;
    let (need, rest) = rest.split_once(" GB on ")?;
    let (root, rest) = rest.split_once(" (")?;
    let (what, rest) = rest.split_once("), have ")?;
    let have = rest.strip_suffix(" GB free")?;
    let what = match what {
        "temp workspace + output" => crate::t!("err.pkg_disk_what_both"),
        "temp workspace" => crate::t!("err.pkg_disk_what_temp"),
        "output package" => crate::t!("err.pkg_disk_what_output"),
        other => other.to_string(),
    };
    Some(crate::t!("err.pkg_disk_insufficient", need = need, root = root, what = what, have = have))
}

fn precheck_console_space(client: &Client, path: &str, size: u64) -> Result<()> {
    match client.preflight(path, size, false) {
        Ok(p) if !p.ok && p.available < size => Err(Error::invalid(crate::t!(
            "err.no_space",
            free = human_bytes(p.available),
            need = human_bytes(size)
        ))),
        Ok(p) if !p.ok => Err(Error::Remote(crate::remote::RemoteError::from_response(500, &p.error))),
        _ => Ok(()),
    }
}

#[cfg(test)]
mod pkg_tests {
    use super::*;

    #[test]
    fn disk_space_error_from_the_builder_is_localized() {
        let raw = "not enough free space: need ~57.7 GB on C:\\ (temp workspace + output), have 40.8 GB free";
        // One sentence fragment per language that can only come from that language's translation,
        // so a missing or mistranslated entry in the table fails this test.
        let expect: &[(&str, &str)] = &[
            ("pt-BR", "Espaço insuficiente"),
            ("en", "Not enough free space"),
            ("es", "Espacio insuficiente"),
            ("fr", "Espace insuffisant"),
            ("ru", "Недостаточно места"),
        ];
        for (lang, phrase) in expect {
            crate::i18n::set_lang(lang);
            let msg = translate_disk_space_error(raw).unwrap_or_else(|| panic!("{lang}: not recognized"));
            assert!(msg.contains(phrase), "{lang}: {msg:?} is missing {phrase:?}");
            assert!(msg.contains("57.7"), "{lang}: {msg}");
            assert!(msg.contains("40.8"), "{lang}: {msg}");
            assert!(msg.contains("C:\\"), "{lang}: {msg}");
        }

        let what_both: &[(&str, &str)] = &[
            ("pt-BR", "pasta temporária e saída"),
            ("en", "temp workspace and output"),
            ("es", "carpeta temporal y salida"),
            ("fr", "dossier temporaire et sortie"),
            ("ru", "временная папка и результат"),
        ];
        for (lang, phrase) in what_both {
            crate::i18n::set_lang(lang);
            let msg = translate_disk_space_error(raw).unwrap();
            assert!(msg.contains(phrase), "{lang}: {msg:?} is missing {phrase:?}");
        }

        let what_output: &[(&str, &str)] = &[
            ("pt-BR", "pacote de saída"),
            ("en", "output package"),
            ("es", "paquete de salida"),
            ("fr", "paquet de sortie"),
            ("ru", "выходной пакет"),
        ];
        let raw_output = "not enough free space: need ~1.2 GB on D:\\jogos (output package), have 0.3 GB free";
        for (lang, phrase) in what_output {
            crate::i18n::set_lang(lang);
            let msg = translate_disk_space_error(raw_output).unwrap();
            assert!(msg.contains(phrase), "{lang}: {msg:?} is missing {phrase:?}");
        }

        let what_temp: &[(&str, &str)] = &[
            ("pt-BR", "pasta temporária"),
            ("en", "temp workspace"),
            ("es", "carpeta temporal"),
            ("fr", "dossier temporaire"),
            ("ru", "временная папка"),
        ];
        let raw_temp = "not enough free space: need ~2.0 GB on E:\\ (temp workspace), have 1.0 GB free";
        for (lang, phrase) in what_temp {
            crate::i18n::set_lang(lang);
            let msg = translate_disk_space_error(raw_temp).unwrap();
            assert!(msg.contains(phrase), "{lang}: {msg:?} is missing {phrase:?}");
        }

        crate::i18n::set_lang("en");
        assert_eq!(translate_disk_space_error("some other failure"), None);
    }

    #[test]
    fn parses_the_builder_protocol() {
        assert_eq!(
            parse_pkg_line(
                r#"PROGRESS {"stage":"Reading source folder","done":3,"total":10,"current":"sce_sys/param.json"}"#
            ),
            Some(PkgMsg::Progress {
                stage: "Reading source folder".into(),
                done: 3,
                total: 10,
                current: "sce_sys/param.json".into()
            })
        );
        assert_eq!(
            parse_pkg_line("LOG hello world"),
            Some(PkgMsg::Log("hello world".into()))
        );
        assert_eq!(
            parse_pkg_line("RESULT {\"packageSize\":123,\"verified\":true}\r"),
            Some(PkgMsg::Result {
                package_size: Some(123)
            })
        );
        assert_eq!(
            parse_pkg_line("RESULT not-json"),
            Some(PkgMsg::Result { package_size: None })
        );

        assert_eq!(
            parse_pkg_line("PROGRESS {oops"),
            Some(PkgMsg::Text("PROGRESS {oops".into()))
        );
        assert_eq!(
            parse_pkg_line("Build started"),
            Some(PkgMsg::Text("Build started".into()))
        );
        assert_eq!(parse_pkg_line("   "), None);
    }

    #[test]
    fn classifies_builder_stages() {
        assert_eq!(pkg_stage_kind("Reading source folder"), PkgStageKind::Items);
        assert_eq!(pkg_stage_kind("Reading exFAT filesystem"), PkgStageKind::Items);
        assert_eq!(pkg_stage_kind("Verifying package"), PkgStageKind::Verify);
        assert_eq!(pkg_stage_kind("Encoding inner image"), PkgStageKind::Bytes);
        assert_eq!(pkg_stage_kind(""), PkgStageKind::Bytes);
    }

    #[test]
    fn progress_opens_a_phase_per_stage_and_only_moves_forward() {
        let ctx = Ctx::new();
        let mut p = PkgProgress::new(0);
        p.apply(&ctx, "Reading source folder", 5, 10, "a.bin");
        let s = ctx.progress.snapshot();
        assert_eq!((s.stage, s.files_total, s.files_done), (Stage::Scanning, 10, 5));
        p.apply(&ctx, "Reading source folder", 4, 10, "b.bin");
        assert_eq!(ctx.progress.snapshot().files_done, 5);

        p.apply(&ctx, "Encoding inner image", 0, 1000, "");
        let s = ctx.progress.snapshot();
        assert_eq!((s.stage, s.total, s.done), (Stage::Processing, 1000, 0));
        p.apply(&ctx, "Encoding inner image", 400, 1000, "data/x.pak");
        p.apply(&ctx, "Encoding inner image", 250, 1000, "data/x.pak");
        let s = ctx.progress.snapshot();
        assert_eq!((s.done, s.current.as_str()), (400, "data/x.pak"));

        p.apply(&ctx, "Writing package", 100, 2000, "");
        let s = ctx.progress.snapshot();
        assert_eq!((s.total, s.done), (2000, 100));
        p.apply(&ctx, "Verifying package", 0, 0, "x.pkg");
        assert_eq!(ctx.progress.snapshot().stage, Stage::Verifying);
    }

    #[test]
    fn progress_of_every_byte_stage_is_scaled_to_the_source_size() {
        let ctx = Ctx::new();
        let mut p = PkgProgress::new(1000);

        p.apply(&ctx, "Outer PFS", 0, 111, "");
        p.apply(&ctx, "Outer PFS", 111 / 2, 111, "");
        let s = ctx.progress.snapshot();
        assert_eq!((s.stage, s.total), (Stage::Processing, 1000));
        assert!((495..=500).contains(&s.done), "done = {}", s.done);
        p.apply(&ctx, "Outer PFS", 111, 111, "");
        assert_eq!(ctx.progress.snapshot().done, 1000);

        p.apply(&ctx, "Outer PFS", 500, 111, "");
        assert_eq!(ctx.progress.snapshot().done, 1000);
    }

    #[test]
    fn only_exfat_and_ufs2_images_go_straight_to_the_builder() {
        assert!(pkg_reads_image_directly(Format::Image(FsKind::Exfat)));
        assert!(pkg_reads_image_directly(Format::Image(FsKind::Ufs2)));
        assert!(pkg_reads_image_directly(Format::Ffpfsc(FsKind::Exfat)));
        assert!(pkg_reads_image_directly(Format::Ffpfsc(FsKind::Ufs2)));

        assert!(!pkg_reads_image_directly(Format::Ffpfsc(FsKind::Pfs)));
        assert!(!pkg_reads_image_directly(Format::Image(FsKind::Pfs)));
        assert!(!pkg_reads_image_directly(Format::Folder));
    }

    #[test]
    fn folders_with_os_junk_are_staged_instead_of_read_directly() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("sce_sys")).unwrap();
        std::fs::write(dir.path().join("eboot.bin"), b"x").unwrap();
        std::fs::write(dir.path().join("sce_sys").join("param.json"), b"{}").unwrap();
        assert!(!folder_has_ignored_names(dir.path()));
        std::fs::write(dir.path().join("sce_sys").join("Thumbs.db"), b"x").unwrap();
        assert!(folder_has_ignored_names(dir.path()));
    }

    #[test]
    fn work_dir_sits_next_to_the_output() {
        let w = pkg_work_dir(Path::new("D:/out/game.pkg"));
        assert_eq!(w.parent(), Some(Path::new("D:/out")));
        assert!(w.file_name().unwrap().to_string_lossy().starts_with(".ps5gfc-pkg-"));
        assert!(pkg_work_dir(Path::new("game.pkg")).starts_with("."));
    }

    fn builder_available() -> bool {
        let ok = matches!(find_pkg_builder(), Ok(PkgBuilder::Exe(_)));
        if !ok {
            eprintln!(
                "ps5pkg-builder.exe não compilado: teste de PKG pulado (dotnet build tools/ps5pkg-builder -c Release)"
            );
        }
        ok
    }

    fn tiny_elf() -> Vec<u8> {
        let mut e = vec![0u8; 0x2000];
        e[..8].copy_from_slice(&[0x7F, b'E', b'L', b'F', 2, 1, 1, 9]);
        e[16..18].copy_from_slice(&2u16.to_le_bytes());
        e[18..20].copy_from_slice(&0x3Eu16.to_le_bytes());
        e[20..24].copy_from_slice(&1u32.to_le_bytes());
        e[24..32].copy_from_slice(&0x40_0000u64.to_le_bytes());
        e[32..40].copy_from_slice(&64u64.to_le_bytes());
        e[52..54].copy_from_slice(&64u16.to_le_bytes());
        e[54..56].copy_from_slice(&56u16.to_le_bytes());
        e[56..58].copy_from_slice(&1u16.to_le_bytes());
        e[64..68].copy_from_slice(&1u32.to_le_bytes());
        e[68..72].copy_from_slice(&5u32.to_le_bytes());
        e[72..80].copy_from_slice(&0x1000u64.to_le_bytes());
        e[80..96].copy_from_slice(&[0, 0, 0x40, 0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0, 0, 0, 0]);
        e[96..104].copy_from_slice(&0x1000u64.to_le_bytes());
        e[104..112].copy_from_slice(&0x1000u64.to_le_bytes());
        e[112..120].copy_from_slice(&0x4000u64.to_le_bytes());
        e
    }

    fn fake_game(root: &Path, content_id: &str, payload_mib: usize) {
        std::fs::create_dir_all(root.join("sce_sys")).unwrap();
        std::fs::create_dir_all(root.join("data")).unwrap();
        std::fs::write(root.join("eboot.bin"), tiny_elf()).unwrap();
        let param = format!(
            r#"{{"contentId":"{content_id}","titleId":"PPSA00001","contentVersion":"01.000.000","masterVersion":"01.00","applicationCategoryType":0,"localizedParameters":{{"defaultLanguage":"en-US","en-US":{{"titleName":"Test Game"}}}}}}"#
        );
        std::fs::write(root.join("sce_sys").join("param.json"), param).unwrap();
        let mut f = std::fs::File::create(root.join("data").join("blob.pak")).unwrap();
        let (mut x, mut buf) = (0x9E37_79B9_7F4A_7C15u64, vec![0u8; 1 << 20]);
        for _ in 0..payload_mib {
            for c in buf.chunks_mut(8) {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                c.copy_from_slice(&x.to_le_bytes()[..c.len()]);
            }
            f.write_all(&buf).unwrap();
        }
    }

    const CONTENT_ID: &str = "IV0000-PPSA00001_00-TESTGAME00000000";

    #[test]
    fn pkg_is_built_and_verified_from_a_folder() {
        if !builder_available() {
            return;
        }
        let (src, out) = (tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap());
        fake_game(src.path(), CONTENT_ID, 4);
        let opts = ConvertOptions {
            verify: true,
            ..Default::default()
        };
        let rep = convert(src.path(), out.path(), Format::Pkg, &opts, &Ctx::new()).expect("PKG");
        assert_eq!(rep.output, out.path().join("PPSA00001.pkg"));
        assert!(rep.out_bytes > 0 && rep.out_bytes == std::fs::metadata(&rep.output).unwrap().len());

        assert_eq!(std::fs::read_dir(out.path()).unwrap().count(), 1);
    }

    #[test]
    fn a_pkg_can_be_opened_as_a_source_and_converted_back() {
        if !builder_available() {
            return;
        }
        let (src, out) = (tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap());
        fake_game(src.path(), CONTENT_ID, 3);
        let ctx = Ctx::new();
        let pkg_dir = out.path().join("pkgs");
        let rep = convert(src.path(), &pkg_dir, Format::Pkg, &ConvertOptions::default(), &ctx).expect("PKG");
        let pkg = rep.output;

        let (opened, info) = crate::inspect::inspect(&pkg, &ctx).expect("inspect");
        assert_eq!(opened.format, Format::Pkg);
        assert_eq!(info.title.title_id.as_deref(), Some("PPSA00001"));
        assert_eq!(info.title.content_id.as_deref(), Some(CONTENT_ID));
        let pkg_target = info.targets.iter().find(|t| t.format == Format::Pkg).unwrap();
        assert!(
            pkg_target.issues.iter().any(|i| i.code == "same_format"),
            "PKG → PKG deve ser recusado"
        );
        assert!(info
            .targets
            .iter()
            .filter(|t| t.format != Format::Pkg)
            .all(|t| t.issues.iter().all(|i| i.severity != crate::inspect::Severity::Error)));
        drop(opened);

        let folder_out = out.path().join("folder");
        let opts = ConvertOptions {
            out_name: Some("game".into()),
            ..Default::default()
        };
        let r = convert(&pkg, &folder_out, Format::Folder, &opts, &ctx).expect("pkg -> pasta");
        assert_eq!(r.source_format, Format::Pkg);
        let game = folder_out.join("game");
        assert_eq!(
            std::fs::read(game.join("data").join("blob.pak")).unwrap(),
            std::fs::read(src.path().join("data").join("blob.pak")).unwrap()
        );
        assert_eq!(
            std::fs::read(game.join("sce_sys").join("param.json")).unwrap(),
            std::fs::read(src.path().join("sce_sys").join("param.json")).unwrap()
        );
        assert_eq!(
            std::fs::read_dir(&folder_out).unwrap().count(),
            1,
            "sobrou a pasta de trabalho da extração"
        );

        let img_dir = out.path().join("img");
        let r = convert(
            &pkg,
            &img_dir,
            Format::Image(FsKind::Exfat),
            &ConvertOptions::default(),
            &ctx,
        )
        .expect("pkg -> exFAT");
        assert_eq!(std::fs::read_dir(&img_dir).unwrap().count(), 1);
        let reopened = open_source(&r.output, &ctx).unwrap();
        let blob = reopened.volume.read_path("data/blob.pak").unwrap().unwrap();
        assert_eq!(blob, std::fs::read(src.path().join("data").join("blob.pak")).unwrap());

        let err = convert(
            &pkg,
            &pkg_dir,
            Format::Pkg,
            &ConvertOptions {
                overwrite: true,
                ..Default::default()
            },
            &ctx,
        )
        .unwrap_err();
        assert!(
            err.to_string().to_lowercase().contains("format") || err.to_string().contains("formato"),
            "{err}"
        );
    }

    #[test]
    fn pkg_builder_errors_reach_the_caller() {
        if !builder_available() {
            return;
        }
        let (src, out) = (tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap());
        fake_game(src.path(), "NOT-A-CONTENT-ID", 1);
        let err = convert(
            src.path(),
            out.path(),
            Format::Pkg,
            &ConvertOptions::default(),
            &Ctx::new(),
        )
        .unwrap_err();
        assert!(err.to_string().contains("Content ID"), "{err}");
        assert_eq!(std::fs::read_dir(out.path()).unwrap().count(), 0);
    }

    #[test]
    fn pkg_refuses_an_existing_output_unless_overwrite() {
        let (src, out) = (tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap());
        fake_game(src.path(), CONTENT_ID, 1);
        let target = out.path().join("PPSA00001.pkg");
        std::fs::write(&target, b"antigo").unwrap();
        let err = convert(
            src.path(),
            out.path(),
            Format::Pkg,
            &ConvertOptions::default(),
            &Ctx::new(),
        )
        .unwrap_err();
        assert!(err.to_string().contains("PPSA00001.pkg"), "{err}");
        assert_eq!(std::fs::read(&target).unwrap(), b"antigo");
    }

    #[test]
    fn cancelling_a_pkg_build_leaves_nothing_behind() {
        if !builder_available() {
            return;
        }
        let (src, out) = (tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap());
        fake_game(src.path(), CONTENT_ID, 400);
        let ctx = Ctx::new();
        let watcher = {
            let ctx = ctx.clone();
            std::thread::spawn(move || {
                let t0 = Instant::now();
                while t0.elapsed() < std::time::Duration::from_secs(60) {
                    let s = ctx.progress.snapshot();
                    if s.stage == Stage::Processing && s.done > 0 {
                        ctx.cancel.cancel();
                        return;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
            })
        };
        let err = convert(src.path(), out.path(), Format::Pkg, &ConvertOptions::default(), &ctx).unwrap_err();
        watcher.join().unwrap();
        assert!(err.is_cancelled(), "{err}");
        assert_eq!(
            std::fs::read_dir(out.path()).unwrap().count(),
            0,
            "o cancelamento deixou arquivos na saída"
        );
    }

    #[test]
    fn pkg_target_has_a_fixed_extension_and_a_distinct_kind() {
        assert_eq!(Format::Pkg.extension(), "pkg");
        assert!(Format::Pkg.same_kind(Format::Pkg));
        assert!(!Format::Pkg.same_kind(Format::Folder));
        assert!(!Format::Pkg.same_kind(Format::Image(FsKind::Ufs2)));
    }
}
