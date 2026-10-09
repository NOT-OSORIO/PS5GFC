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

use base64::Engine;
use serde::Serialize;

use crate::convert::{choose_route, ConvertOptions, Route};
use crate::ctl::{Ctx, Stage};
use crate::source::{open_source, Format, FsKind, OpenedSource, WrapperInfo};
use crate::title::{read_icon, read_title, TitleInfo};
use crate::volume::{totals, Volume};
use crate::Result;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Severity {
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Serialize)]
pub struct Issue {
    pub severity: Severity,
    pub code: &'static str,
    pub args: std::collections::BTreeMap<String, String>,
    pub msg: String,
}

macro_rules! issue {
    ($sev:expr, $key:literal, $code:literal $(, $name:ident = $val:expr)* $(,)?) => {{
        #[allow(unused_mut)]
        let mut args = std::collections::BTreeMap::new();
        $( args.insert(stringify!($name).to_string(), ($val).to_string()); )*
        let msg = crate::t!($key $(, $name = args[stringify!($name)].clone())*);
        Issue { severity: $sev, code: $code, args, msg }
    }};
}

#[derive(Debug, Clone, Serialize)]
pub struct TargetInfo {
    pub format: Format,
    pub route: Route,
    pub issues: Vec<Issue>,
}

#[derive(Debug, Clone, Serialize)]
pub struct SourceInfo {
    pub path: String,
    pub format: Format,
    pub files: u64,
    pub dirs: u64,
    pub bytes: u64,
    pub title: TitleInfo,

    pub icon: Option<String>,
    pub wrapper: Option<WrapperInfo>,
    pub details: Vec<(String, String)>,
    pub issues: Vec<Issue>,
    pub targets: Vec<TargetInfo>,
}

pub fn all_targets() -> Vec<Format> {
    vec![
        Format::Folder,
        Format::Image(FsKind::Exfat),
        Format::Image(FsKind::Ufs2),
        Format::Ffpfsc(FsKind::Exfat),
        Format::Ffpfsc(FsKind::Ufs2),
        Format::Ffpfsc(FsKind::Pfs),
        Format::Pkg,
    ]
}

pub fn precheck(vol: &dyn Volume, target: Format) -> Vec<Issue> {
    let mut out = Vec::new();
    let entries = vol.entries();
    match target.inner_fs() {
        Some(FsKind::Pfs) => {
            let bad: Vec<&str> = entries
                .iter()
                .filter(|e| !e.path.is_ascii())
                .map(|e| e.path.as_str())
                .take(3)
                .collect();
            if !bad.is_empty() {
                out.push(issue!(
                    Severity::Error,
                    "iss.pfs_ascii",
                    "pfs_ascii",
                    names = bad.join(", ")
                ));
            }
        }
        Some(FsKind::Exfat) => {
            if let Some(e) = entries.iter().find(|e| e.name().encode_utf16().count() > 255) {
                out.push(issue!(Severity::Error, "iss.exfat_long", "exfat_long", path = e.path));
            }
        }
        Some(FsKind::Ufs2) => {
            if let Some(e) = entries.iter().find(|e| e.name().len() > 255) {
                out.push(issue!(Severity::Error, "iss.ufs2_long", "ufs2_long", path = e.path));
            }
        }
        None => {
            #[cfg(windows)]
            {
                const BAD: &[char] = &['<', '>', ':', '"', '\\', '|', '?', '*'];
                if let Some(e) = entries.iter().find(|e| {
                    e.path
                        .split('/')
                        .any(|c| c.chars().any(|ch| BAD.contains(&ch)) || c.ends_with('.') || c.ends_with(' '))
                }) {
                    out.push(issue!(Severity::Error, "iss.win_name", "win_name", path = e.path));
                }
            }
        }
    }
    out
}

pub fn build_info(src: &OpenedSource) -> SourceInfo {
    let vol = src.volume.as_ref();
    let (bytes, files, dirs) = totals(vol);
    let title = read_title(vol);
    let icon = read_icon(vol).map(|b| {
        format!(
            "data:image/png;base64,{}",
            base64::engine::general_purpose::STANDARD.encode(b)
        )
    });

    let mut issues = Vec::new();
    if !title.has_eboot {
        issues.push(issue!(Severity::Warn, "iss.no_eboot", "no_eboot"));
    }
    if title.title_id.is_none() {
        issues.push(issue!(Severity::Warn, "iss.no_titleid", "no_titleid"));
    }
    for (k, v) in vol.describe() {
        if k == "cluster" && v != "64 KiB" {
            issues.push(issue!(Severity::Info, "iss.cluster", "cluster", v = v));
        }
        if k == "block" && v != "64 KiB" {
            issues.push(issue!(Severity::Info, "iss.block", "block", v = v));
        }
    }

    let opts = ConvertOptions::default();
    let targets = all_targets()
        .into_iter()
        .map(|t| {
            let route = choose_route(src, t, &opts);
            let mut issues = precheck(vol, t);
            if t == Format::Pkg && title.content_id.is_none() {
                issues.insert(0, issue!(Severity::Error, "iss.no_contentid", "no_contentid"));
            }
            if src.format.same_kind(t) {
                issues.insert(0, issue!(Severity::Error, "iss.same_format", "same_format"));
            }
            TargetInfo {
                format: t,
                route,
                issues,
            }
        })
        .collect();

    SourceInfo {
        path: src.path.to_string_lossy().into_owned(),
        format: src.format,
        files,
        dirs,
        bytes,
        title,
        icon,
        wrapper: src.wrapper.clone(),
        details: vol.describe(),
        issues,
        targets,
    }
}

pub fn inspect(path: &std::path::Path, ctx: &Ctx) -> Result<(OpenedSource, SourceInfo)> {
    let src = open_source(path, ctx)?;
    ctx.cancel.check()?;
    ctx.progress.set_stage(Stage::Planning);
    let info = build_info(&src);
    Ok((src, info))
}
