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

use serde::{Deserialize, Serialize};

use crate::volume::Volume;

#[derive(Debug, Clone, Default, Serialize)]
pub struct TitleInfo {
    pub title_id: Option<String>,
    pub content_id: Option<String>,
    pub title_name: Option<String>,
    pub content_version: Option<String>,
    pub has_eboot: bool,

    pub ampr: bool,

    pub ampr_packs: bool,

    pub playgo: bool,
}

fn pick_str(v: &serde_json::Value, key: &str) -> Option<String> {
    v.get(key)
        .and_then(|x| x.as_str())
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
}

pub fn read_title(vol: &dyn Volume) -> TitleInfo {
    let mut info = TitleInfo::default();
    info.has_eboot = vol.find("eboot.bin").is_some();
    info.ampr = vol.find("fakelib/libSceAmpr.sprx").is_some();
    info.ampr_packs = vol.find(crate::ampr::PACK_INDEX).is_some();
    info.playgo = vol.find("sce_sys/playgo-chunk.dat").is_some();
    let Ok(Some(raw)) = vol.read_path("sce_sys/param.json") else {
        return info;
    };

    let raw = raw.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&raw);
    let Ok(v) = serde_json::from_slice::<serde_json::Value>(raw) else {
        return info;
    };
    info.title_id = pick_str(&v, "titleId").or_else(|| pick_str(&v, "title_id"));
    info.content_id = pick_str(&v, "contentId").or_else(|| pick_str(&v, "content_id"));
    info.content_version = pick_str(&v, "contentVersion").or_else(|| pick_str(&v, "masterVersion"));

    if let Some(loc) = v.get("localizedParameters") {
        let default = loc.get("defaultLanguage").and_then(|x| x.as_str()).map(str::to_string);
        let mut tries: Vec<String> = Vec::new();
        if let Some(d) = default {
            tries.push(d);
        }
        tries.push("en-US".into());
        for k in tries {
            if let Some(n) = loc.get(&k).and_then(|o| pick_str(o, "titleName")) {
                info.title_name = Some(n);
                break;
            }
        }
        if info.title_name.is_none() {
            if let Some(obj) = loc.as_object() {
                info.title_name = obj.values().find_map(|o| pick_str(o, "titleName"));
            }
        }
    }
    if info.title_name.is_none() {
        info.title_name = pick_str(&v, "titleName");
    }
    info
}

pub fn read_icon(vol: &dyn Volume) -> Option<Vec<u8>> {
    let i = vol.find("sce_sys/icon0.png")?;
    if vol.entries()[i].size > 8 * 1024 * 1024 {
        return None;
    }
    vol.read_path("sce_sys/icon0.png").ok().flatten()
}

pub fn output_stem(info: &TitleInfo, fallback: &str) -> String {
    let base = info.title_id.clone().unwrap_or_else(|| fallback.to_string());
    let s: String = base
        .chars()
        .map(|c| {
            if c.is_alphanumeric() || "._-".contains(c) {
                c
            } else {
                '_'
            }
        })
        .collect();
    if s.is_empty() {
        "image".into()
    } else {
        s
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum NameMode {
    Ppsa,

    #[default]
    PpsaTitle,

    PpsaTitleVersion,
}

impl NameMode {
    pub fn parse(s: &str) -> Option<Self> {
        match s {
            "ppsa" => Some(Self::Ppsa),
            "ppsa-title" | "ppsa_title" => Some(Self::PpsaTitle),
            "ppsa-title-version" | "ppsa_title_version" => Some(Self::PpsaTitleVersion),
            _ => None,
        }
    }
}

pub fn sanitize_name(s: &str) -> String {
    const BAD: &[char] = &['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    let spaced: String = s
        .chars()
        .map(|c| if c.is_control() || BAD.contains(&c) { ' ' } else { c })
        .collect();
    let joined = spaced.split_whitespace().collect::<Vec<_>>().join(" ");
    let mut r: String = joined
        .trim_matches(|c: char| c == '.' || c == ' ')
        .chars()
        .take(120)
        .collect();
    while r.ends_with('.') || r.ends_with(' ') {
        r.pop();
    }
    r
}

pub fn output_name(info: &TitleInfo, mode: NameMode, fallback: &str) -> String {
    let id = info.title_id.as_deref().map(sanitize_name).filter(|s| !s.is_empty());
    let name = info.title_name.as_deref().map(sanitize_name).filter(|s| !s.is_empty());
    let ver = info
        .content_version
        .as_deref()
        .map(sanitize_name)
        .filter(|s| !s.is_empty());
    let fb = sanitize_name(fallback);
    let id_name = || match (&id, &name) {
        (Some(i), Some(n)) => format!("{i} {n}"),
        (Some(i), None) => i.clone(),
        (None, Some(n)) => n.clone(),
        (None, None) => fb.clone(),
    };
    let raw = match mode {
        NameMode::Ppsa => id.clone().unwrap_or_else(|| fb.clone()),
        NameMode::PpsaTitle => id_name(),
        NameMode::PpsaTitleVersion => match &ver {
            Some(v) => format!("{} ({v})", id_name()),
            None => id_name(),
        },
    };
    let clean = sanitize_name(&raw);
    if clean.is_empty() {
        fb
    } else {
        clean
    }
}

#[cfg(test)]
mod name_tests {
    use super::*;

    fn info() -> TitleInfo {
        TitleInfo {
            title_id: Some("PPSA26893".into()),
            content_id: Some("IV0000-PPSA26893_00-SPONGEBOB000000".into()),
            title_name: Some("SpongeBob SquarePants: Titans of the Tide".into()),
            content_version: Some("01.000.000".into()),
            ..Default::default()
        }
    }

    #[test]
    fn modes() {
        let i = info();
        assert_eq!(output_name(&i, NameMode::Ppsa, "x"), "PPSA26893");
        assert_eq!(
            output_name(&i, NameMode::PpsaTitle, "x"),
            "PPSA26893 SpongeBob SquarePants Titans of the Tide"
        );
        assert_eq!(
            output_name(&i, NameMode::PpsaTitleVersion, "x"),
            "PPSA26893 SpongeBob SquarePants Titans of the Tide (01.000.000)"
        );

        assert!(!output_name(&i, NameMode::default(), "x").contains("_pkg"));
    }

    #[test]
    fn hostile_characters_are_cleaned_before_composing() {
        let mut i = info();
        i.title_name = Some("Ampr: Test*Game?".into());
        assert_eq!(output_name(&i, NameMode::PpsaTitle, "x"), "PPSA26893 Ampr Test Game");
        assert_eq!(
            output_name(&i, NameMode::PpsaTitleVersion, "x"),
            "PPSA26893 Ampr Test Game (01.000.000)"
        );
    }

    #[test]
    fn falls_back_when_metadata_is_missing() {
        let empty = TitleInfo::default();
        assert_eq!(output_name(&empty, NameMode::Ppsa, "meu jogo"), "meu jogo");
        assert_eq!(output_name(&empty, NameMode::PpsaTitleVersion, "meu jogo"), "meu jogo");
        let mut only_name = TitleInfo::default();
        only_name.title_name = Some("Só o nome".into());
        assert_eq!(output_name(&only_name, NameMode::PpsaTitle, "x"), "Só o nome");
    }

    #[test]
    fn sanitizes_windows_hostile_names() {
        assert_eq!(sanitize_name("  a<b>c:d\"e/f|g?h*  "), "a b c d e f g h");
        assert_eq!(sanitize_name("Nome..."), "Nome");
        assert_eq!(sanitize_name("a\t\nb"), "a b");
        assert!(sanitize_name(&"x".repeat(300)).chars().count() <= 120);
    }
}
