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

use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rustc-check-cfg=cfg(embedded_builder)");
    println!("cargo:rerun-if-env-changed=PS5GFC_EMBED_BUILDER");
    println!("cargo:rerun-if-changed=build.rs");

    let out = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR"));
    let target = out.join("embedded_builder.rs");
    let source = env::var_os("PS5GFC_EMBED_BUILDER")
        .map(PathBuf::from)
        .filter(|p| p.is_file());

    match source {
        Some(path) => {
            let path = path.canonicalize().unwrap_or(path);
            println!("cargo:rerun-if-changed={}", path.display());
            println!("cargo:rustc-cfg=embedded_builder");
            let literal = path.display().to_string().trim_start_matches(r"\\?\").to_string();
            fs::write(
                &target,
                format!("pub static BUILDER: &[u8] = include_bytes!(r#\"{literal}\"#);\n"),
            )
            .expect("write embedded_builder.rs");
        }
        None => {
            fs::write(&target, "pub static BUILDER: &[u8] = &[];\n").expect("write embedded_builder.rs");
        }
    }
}
