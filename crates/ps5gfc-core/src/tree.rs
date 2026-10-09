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

use std::collections::HashMap;

use crate::volume::Entry;

#[derive(Debug, Clone)]
pub struct TNode {
    pub name: String,
    pub is_dir: bool,
    pub size: u64,
    pub mtime: Option<i64>,

    pub entry: Option<usize>,
    pub parent: usize,
    pub children: Vec<usize>,
}

#[derive(Debug, Clone)]
pub struct Tree {
    pub nodes: Vec<TNode>,
}

impl Tree {
    pub fn build(entries: &[Entry]) -> Tree {
        let mut nodes = vec![TNode {
            name: String::new(),
            is_dir: true,
            size: 0,
            mtime: None,
            entry: None,
            parent: 0,
            children: Vec::new(),
        }];
        let mut dir_index: HashMap<String, usize> = HashMap::new();
        dir_index.insert(String::new(), 0);

        let mut order: Vec<usize> = (0..entries.len()).collect();
        order.sort_by(|&a, &b| entries[a].path.cmp(&entries[b].path));

        fn ensure_dir(nodes: &mut Vec<TNode>, dir_index: &mut HashMap<String, usize>, path: &str) -> usize {
            if let Some(&i) = dir_index.get(path) {
                return i;
            }
            let (parent_path, name) = match path.rfind('/') {
                Some(p) => (&path[..p], &path[p + 1..]),
                None => ("", path),
            };
            let parent = ensure_dir(nodes, dir_index, parent_path);
            let idx = nodes.len();
            nodes.push(TNode {
                name: name.to_string(),
                is_dir: true,
                size: 0,
                mtime: None,
                entry: None,
                parent,
                children: Vec::new(),
            });
            nodes[parent].children.push(idx);
            dir_index.insert(path.to_string(), idx);
            idx
        }

        for &ei in &order {
            let e = &entries[ei];
            if e.path.is_empty() {
                continue;
            }
            if e.is_dir {
                let idx = ensure_dir(&mut nodes, &mut dir_index, &e.path);
                nodes[idx].entry = Some(ei);
                nodes[idx].mtime = e.mtime;
            } else {
                let parent = ensure_dir(&mut nodes, &mut dir_index, e.parent());
                let idx = nodes.len();
                nodes.push(TNode {
                    name: e.name().to_string(),
                    is_dir: false,
                    size: e.size,
                    mtime: e.mtime,
                    entry: Some(ei),
                    parent,
                    children: Vec::new(),
                });
                nodes[parent].children.push(idx);
            }
        }

        for i in 0..nodes.len() {
            if nodes[i].children.len() > 1 {
                let mut ch = std::mem::take(&mut nodes[i].children);
                ch.sort_by(|&a, &b| {
                    let (na, nb) = (&nodes[a].name, &nodes[b].name);
                    na.to_lowercase().cmp(&nb.to_lowercase()).then_with(|| na.cmp(nb))
                });
                nodes[i].children = ch;
            }
        }
        Tree { nodes }
    }

    pub fn preorder(&self) -> Vec<usize> {
        let mut out = Vec::with_capacity(self.nodes.len());
        let mut stack: Vec<usize> = self.nodes[0].children.iter().rev().copied().collect();
        while let Some(i) = stack.pop() {
            out.push(i);
            for &c in self.nodes[i].children.iter().rev() {
                stack.push(c);
            }
        }
        out
    }

    pub fn path_of(&self, mut idx: usize) -> String {
        let mut parts = Vec::new();
        while idx != 0 {
            parts.push(self.nodes[idx].name.as_str());
            idx = self.nodes[idx].parent;
        }
        parts.reverse();
        parts.join("/")
    }

    pub fn file_count(&self) -> usize {
        self.nodes.iter().skip(1).filter(|n| !n.is_dir).count()
    }
    pub fn dir_count(&self) -> usize {
        self.nodes.iter().skip(1).filter(|n| n.is_dir).count()
    }
    pub fn total_bytes(&self) -> u64 {
        self.nodes.iter().skip(1).filter(|n| !n.is_dir).map(|n| n.size).sum()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn e(p: &str, dir: bool, size: u64) -> Entry {
        Entry {
            path: p.into(),
            is_dir: dir,
            size,
            mtime: None,
        }
    }

    #[test]
    fn builds_implicit_parents_and_sorts() {
        let t = Tree::build(&[
            e("b/x.bin", false, 5),
            e("A.txt", false, 1),
            e("b", true, 0),
            e("c/d/e.bin", false, 2),
        ]);
        let order: Vec<String> = t.preorder().iter().map(|&i| t.path_of(i)).collect();
        assert_eq!(order, vec!["A.txt", "b", "b/x.bin", "c", "c/d", "c/d/e.bin"]);
        assert_eq!(t.file_count(), 3);
        assert_eq!(t.dir_count(), 3);
        assert_eq!(t.total_bytes(), 8);
    }
}
