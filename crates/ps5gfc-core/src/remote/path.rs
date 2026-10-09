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

pub fn norm(p: &str) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for c in p.split('/') {
        match c {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            c => parts.push(c),
        }
    }
    if parts.is_empty() {
        "/".to_string()
    } else {
        format!("/{}", parts.join("/"))
    }
}

pub fn join(base: &str, rel: &str) -> String {
    let mut out = norm(base);
    for c in rel.split('/') {
        if c.is_empty() || c == "." || c == ".." {
            continue;
        }
        if !out.ends_with('/') {
            out.push('/');
        }
        out.push_str(c);
    }
    out
}

pub fn parent(p: &str) -> String {
    let n = norm(p);
    match n.rfind('/') {
        Some(0) | None => "/".to_string(),
        Some(i) => n[..i].to_string(),
    }
}

pub fn file_name(p: &str) -> String {
    let n = norm(p);
    n.rsplit('/').next().unwrap_or("").to_string()
}

pub fn is_within(p: &str, base: &str) -> bool {
    let (p, b) = (norm(p), norm(base));
    b == "/" || p == b || p.starts_with(&format!("{b}/"))
}

pub fn components(p: &str) -> Vec<String> {
    norm(p)
        .split('/')
        .filter(|c| !c.is_empty())
        .map(str::to_string)
        .collect()
}

pub fn valid_name(n: &str) -> bool {
    !n.is_empty() && n != "." && n != ".." && !n.contains('/') && !n.contains('\0') && n.len() <= 255
}

pub fn split_ext(name: &str) -> (&str, &str) {
    match name.rfind('.') {
        Some(i) if i > 0 => (&name[..i], &name[i..]),
        _ => (name, ""),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes() {
        assert_eq!(norm("data//a/./b/"), "/data/a/b");
        assert_eq!(norm("/a/../../b"), "/b");
        assert_eq!(norm(""), "/");
        assert_eq!(norm("/"), "/");
    }

    #[test]
    fn joins_without_escaping() {
        assert_eq!(join("/data", "a/b"), "/data/a/b");
        assert_eq!(join("/data/", "../x"), "/data/x");
        assert_eq!(join("/", "x"), "/x");
        assert_eq!(join("/data", ""), "/data");
    }

    #[test]
    fn parents_and_names() {
        assert_eq!(parent("/data/a"), "/data");
        assert_eq!(parent("/data"), "/");
        assert_eq!(parent("/"), "/");
        assert_eq!(file_name("/data/a.exfat"), "a.exfat");
        assert_eq!(file_name("/"), "");
    }

    #[test]
    fn containment() {
        assert!(is_within("/data/a/b", "/data/a"));
        assert!(is_within("/data/a", "/data/a"));
        assert!(!is_within("/data/ab", "/data/a"));
        assert!(is_within("/anything", "/"));
    }

    #[test]
    fn names() {
        assert!(valid_name("ok.txt"));
        assert!(!valid_name("a/b"));
        assert!(!valid_name(".."));
        assert!(!valid_name(""));
        assert_eq!(split_ext("jogo.tar.gz"), ("jogo.tar", ".gz"));
        assert_eq!(split_ext(".hidden"), (".hidden", ""));
        assert_eq!(split_ext("semext"), ("semext", ""));
    }
}
