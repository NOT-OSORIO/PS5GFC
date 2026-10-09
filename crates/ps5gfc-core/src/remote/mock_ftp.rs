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

use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use super::ftp::{Ftp, FtpConfig};
use super::http::Endpoint;
use super::path as rp;

pub const SELF_MAGIC: &[u8] = b"SELF";

#[derive(Clone)]
enum Node {
    Dir(u32),
    File(Vec<u8>, u32),
}

#[derive(Default)]
pub struct FtpFaults {
    pub cut_next_store_after: Option<usize>,

    pub lose_tail_on_cut: usize,

    pub cut_next_retr_after: Option<usize>,

    pub rnto_refuses_overwrite: bool,

    pub no_chmod: bool,

    pub full_after: Option<usize>,
    pub refuse_login: bool,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Profile {
    Generic,
    Ftpsrv,
}

struct State {
    fs: BTreeMap<String, Node>,
    faults: FtpFaults,
    commands: Vec<String>,
    sessions: usize,
    controls: Vec<TcpStream>,
    profile: Profile,
    mlsd: bool,
    mlst: bool,
    epsv: bool,
}

pub struct MockFtp {
    pub endpoint: Endpoint,
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl State {
    fn is_dir(&self, p: &str) -> bool {
        p == "/" || matches!(self.fs.get(p), Some(Node::Dir(_)))
    }
    fn exists(&self, p: &str) -> bool {
        p == "/" || self.fs.contains_key(p)
    }
    fn children(&self, dir: &str) -> Vec<(String, Node)> {
        let prefix = if dir == "/" { "/".to_string() } else { format!("{dir}/") };
        self.fs
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix) && k.len() > prefix.len() && !k[prefix.len()..].contains('/'))
            .map(|(k, n)| (k[prefix.len()..].to_string(), n.clone()))
            .collect()
    }
}

fn view(data: &[u8], self2elf: bool) -> &[u8] {
    if self2elf && data.starts_with(SELF_MAGIC) {
        &data[SELF_MAGIC.len()..]
    } else {
        data
    }
}

impl MockFtp {
    pub fn start() -> Self {
        Self::with_profile(Profile::Generic)
    }

    pub fn start_ftpsrv() -> Self {
        Self::with_profile(Profile::Ftpsrv)
    }

    pub fn with_profile(profile: Profile) -> Self {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        l.set_nonblocking(true).unwrap();
        let port = l.local_addr().unwrap().port();
        let mut fs = BTreeMap::new();
        for d in ["/data", "/mnt", "/user"] {
            fs.insert(d.to_string(), Node::Dir(0o755));
        }
        let generic = profile == Profile::Generic;
        let state = Arc::new(Mutex::new(State {
            fs,
            faults: FtpFaults::default(),
            commands: Vec::new(),
            sessions: 0,
            controls: Vec::new(),
            profile,
            mlsd: true,
            mlst: generic,
            epsv: generic,
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let (st2, stop2) = (state.clone(), stop.clone());
        let handle = std::thread::spawn(move || {
            while !stop2.load(Ordering::Relaxed) {
                match l.accept() {
                    Ok((s, _)) => {
                        let _ = s.set_nonblocking(false);
                        let st = st2.clone();
                        std::thread::spawn(move || {
                            let _ = serve(s, st);
                        });
                    }
                    Err(_) => std::thread::sleep(Duration::from_millis(2)),
                }
            }
        });
        MockFtp {
            endpoint: Endpoint {
                host: "127.0.0.1".into(),
                port,
            },
            state,
            stop,
            handle: Some(handle),
        }
    }

    pub fn client(&self) -> Ftp {
        Ftp::new(FtpConfig {
            ep: self.endpoint.clone(),
            user: "anonymous".into(),
            pass: "x@y".into(),
        })
    }

    pub fn put_dir(&self, p: &str) {
        let mut s = self.state.lock().unwrap();
        let mut cur = String::new();
        for c in rp::components(p) {
            cur = format!("{cur}/{c}");
            s.fs.entry(cur.clone()).or_insert(Node::Dir(0o755));
        }
    }

    pub fn put_file(&self, p: &str, data: &[u8]) {
        self.put_dir(&rp::parent(p));
        self.state
            .lock()
            .unwrap()
            .fs
            .insert(rp::norm(p), Node::File(data.to_vec(), 0o644));
    }

    pub fn file(&self, p: &str) -> Option<Vec<u8>> {
        match self.state.lock().unwrap().fs.get(&rp::norm(p)) {
            Some(Node::File(d, _)) => Some(d.clone()),
            _ => None,
        }
    }

    pub fn mode(&self, p: &str) -> Option<u32> {
        match self.state.lock().unwrap().fs.get(&rp::norm(p)) {
            Some(Node::File(_, m)) | Some(Node::Dir(m)) => Some(*m),
            None => None,
        }
    }

    pub fn is_dir(&self, p: &str) -> bool {
        self.state.lock().unwrap().is_dir(&rp::norm(p))
    }

    pub fn paths(&self) -> Vec<String> {
        self.state.lock().unwrap().fs.keys().cloned().collect()
    }

    pub fn faults<R>(&self, f: impl FnOnce(&mut FtpFaults) -> R) -> R {
        f(&mut self.state.lock().unwrap().faults)
    }

    pub fn commands(&self) -> Vec<String> {
        self.state.lock().unwrap().commands.clone()
    }

    pub fn sessions(&self) -> usize {
        self.state.lock().unwrap().sessions
    }

    pub fn features(&self, mlsd: bool, mlst: bool, epsv: bool) {
        let mut s = self.state.lock().unwrap();
        s.mlsd = mlsd;
        s.mlst = mlst;
        s.epsv = epsv;
    }

    pub fn disconnect_all(&self) {
        let mut s = self.state.lock().unwrap();
        for c in s.controls.drain(..) {
            let _ = c.shutdown(Shutdown::Both);
        }
    }
}

impl Drop for MockFtp {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        self.disconnect_all();
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

fn perms(kind: char, m: u32) -> String {
    let mut s = String::from(kind);
    for (i, c) in "rwxrwxrwx".chars().enumerate() {
        s.push(if m & (0o400 >> i) != 0 { c } else { '-' });
    }
    s
}

fn unix_line(profile: Profile, name: &str, n: &Node, self2elf: bool) -> String {
    let when = if profile == Profile::Ftpsrv {
        "Jan 01 00:00"
    } else {
        "Jan  1  2026"
    };
    match n {
        Node::Dir(m) => format!("{} 1 ps5 ps5 0 {when} {name}\r\n", perms('d', *m)),
        Node::File(d, m) => format!(
            "{} 1 ps5 ps5 {} {when} {name}\r\n",
            perms('-', *m),
            view(d, self2elf).len()
        ),
    }
}

fn mlsd_line(profile: Profile, name: &str, n: &Node, self2elf: bool) -> String {
    let mode = |m: u32| {
        if profile == Profile::Ftpsrv {
            String::new()
        } else {
            format!(";UNIX.mode=0{m:o}")
        }
    };
    match n {
        Node::Dir(m) => format!("type=dir;modify=20260101000000{}; {name}\r\n", mode(*m)),
        Node::File(d, m) => format!(
            "type=file;size={};modify=20260101000000{}; {name}\r\n",
            view(d, self2elf).len(),
            mode(*m)
        ),
    }
}

fn say(w: &mut TcpStream, s: &str) -> std::io::Result<()> {
    w.write_all(s.as_bytes())?;
    w.write_all(b"\r\n")?;
    w.flush()
}

fn accept_data(l: &TcpListener) -> Option<TcpStream> {
    l.set_nonblocking(true).ok()?;
    let t0 = Instant::now();
    while t0.elapsed() < Duration::from_secs(5) {
        if let Ok((s, _)) = l.accept() {
            let _ = s.set_nonblocking(false);
            let _ = s.set_read_timeout(Some(Duration::from_secs(10)));
            return Some(s);
        }
        std::thread::sleep(Duration::from_millis(1));
    }
    None
}

fn serve(ctl: TcpStream, st: Arc<Mutex<State>>) -> std::io::Result<()> {
    let profile = {
        let mut g = st.lock().unwrap();
        g.sessions += 1;
        g.controls.push(ctl.try_clone()?);
        g.profile
    };
    let ftpsrv = profile == Profile::Ftpsrv;
    let mut w = ctl.try_clone()?;
    let mut rd = BufReader::new(ctl.try_clone()?);
    if ftpsrv {
        say(&mut w, "220-Welcome to ftpsrv.elf running on pid 4242\r\n220-Version: v0.0-mock (built today)\r\n220 Service is ready")?;
    } else {
        say(&mut w, "220 mock ftp ready")?;
    }
    let mut pasv: Option<TcpListener> = None;
    let mut rnfr: Option<String> = None;
    let mut rest: u64 = 0;
    let mut cwd = "/".to_string();

    let mut self2elf = ftpsrv;

    let okc = |generic: &'static str| if ftpsrv { "226" } else { generic };
    loop {
        let mut line = String::new();
        if rd.read_line(&mut line)? == 0 {
            return Ok(());
        }
        let line = line.trim_end_matches(['\r', '\n']).to_string();

        let body = if line.len() > 5 && line[..5].eq_ignore_ascii_case("SITE ") && ftpsrv {
            line[5..].to_string()
        } else {
            line.clone()
        };
        let (cmd, arg) = match body.split_once(' ') {
            Some((c, a)) => (c.to_ascii_uppercase(), a.to_string()),
            None => (body.to_ascii_uppercase(), String::new()),
        };
        st.lock().unwrap().commands.push(line.clone());
        let abs = |p: &str| {
            if p.starts_with('/') {
                rp::norm(p)
            } else {
                rp::join(&cwd, p)
            }
        };
        match cmd.as_str() {
            "USER" => {
                if ftpsrv {
                    say(&mut w, "230 User logged in")?
                } else {
                    say(&mut w, "331 password please")?
                }
            }
            "PASS" if !ftpsrv => {
                if st.lock().unwrap().faults.refuse_login {
                    say(&mut w, "530 Login incorrect.")?
                } else {
                    say(&mut w, "230 welcome")?
                }
            }
            "SYST" => say(&mut w, "215 UNIX Type: L8")?,
            "FEAT" => {
                let g = st.lock().unwrap();
                let mut s = String::from("211-Features:\r\n");
                if ftpsrv {
                    s.push_str(" MLSD\r\n KILL\r\n MTRW\r\n SELF\r\n");
                } else {
                    s.push_str(" SIZE\r\n REST STREAM\r\n");
                    if g.mlsd {
                        s.push_str(" MLSD\r\n");
                    }
                    if g.mlst {
                        s.push_str(" MLST type*;size*;modify*;UNIX.mode*;\r\n");
                    }
                    if g.epsv {
                        s.push_str(" EPSV\r\n");
                    }
                }
                s.push_str("211 End");
                drop(g);
                say(&mut w, &s)?
            }
            "SELF" if ftpsrv => {
                self2elf = !self2elf;
                say(
                    &mut w,
                    if self2elf {
                        "226 SELF transfer mode enabled"
                    } else {
                        "226 SELF transfer mode disabled"
                    },
                )?
            }
            "TYPE" => say(&mut w, "200 Type set to I")?,
            "OPTS" if !ftpsrv => say(&mut w, "200 ok")?,
            "NOOP" => say(&mut w, "200 NOOP OK")?,
            "PWD" => say(&mut w, &format!("257 \"{cwd}\""))?,
            "QUIT" => {
                say(&mut w, "221 bye")?;
                return Ok(());
            }
            "CWD" => {
                let p = abs(&arg);
                if st.lock().unwrap().is_dir(&p) {
                    cwd = p;
                    say(&mut w, "250 OK")?
                } else {
                    say(&mut w, "550 No such file or directory")?
                }
            }
            "EPSV" | "PASV" => {
                if cmd == "EPSV" && !st.lock().unwrap().epsv {
                    say(
                        &mut w,
                        if ftpsrv {
                            "502 Command not recognized"
                        } else {
                            "500 EPSV not understood"
                        },
                    )?;
                    continue;
                }
                let l = TcpListener::bind("127.0.0.1:0")?;
                let port = l.local_addr()?.port();
                pasv = Some(l);
                if cmd == "EPSV" {
                    say(&mut w, &format!("229 Entering Extended Passive Mode (|||{port}|)"))?
                } else {
                    say(
                        &mut w,
                        &format!("227 Entering Passive Mode (127,0,0,1,{},{}).", port / 256, port % 256),
                    )?
                }
            }
            "REST" => {
                rest = arg.trim().parse().unwrap_or(0);
                say(&mut w, "350 REST OK")?
            }
            "LIST" | "MLSD" => {
                let p = if cmd == "LIST" && !arg.is_empty() && !arg.starts_with('-') {
                    abs(&arg)
                } else {
                    cwd.clone()
                };
                let (listing, ok) = {
                    let g = st.lock().unwrap();
                    if cmd == "MLSD" && !g.mlsd {
                        drop(g);
                        say(&mut w, "500 unknown command")?;
                        continue;
                    }
                    if !g.is_dir(&p) {
                        (String::new(), false)
                    } else {
                        let mut s = String::new();
                        if cmd == "LIST" {
                            if ftpsrv {
                                s.push_str(&format!(
                                    "{} 1 0 0 0 Jan 01 00:00 .\r\n{} 1 0 0 0 Jan 01 00:00 ..\r\n",
                                    perms('d', 0o755),
                                    perms('d', 0o755)
                                ));
                            } else {
                                s.push_str("total 0\r\n");
                            }
                        }
                        for (n, node) in g.children(&p) {
                            s.push_str(&if cmd == "LIST" {
                                unix_line(profile, &n, &node, self2elf)
                            } else {
                                mlsd_line(profile, &n, &node, self2elf)
                            });
                        }
                        (s, true)
                    }
                };
                let Some(l) = pasv.take() else {
                    say(&mut w, "425 use PASV first")?;
                    continue;
                };
                if !ok {
                    say(&mut w, "550 No such file or directory")?;
                    continue;
                }
                say(&mut w, "150 Opening data transfer")?;
                if let Some(mut d) = accept_data(&l) {
                    let _ = d.write_all(listing.as_bytes());
                    let _ = d.shutdown(Shutdown::Both);
                }
                say(&mut w, "226 Transfer complete")?
            }
            "MLST" if !ftpsrv => {
                let p = abs(&arg);
                let g = st.lock().unwrap();
                if !g.mlst {
                    drop(g);
                    say(&mut w, "500 unknown command")?;
                    continue;
                }
                match g.fs.get(&p) {
                    Some(n) => {
                        let l = mlsd_line(profile, &rp::file_name(&p), n, false);
                        drop(g);
                        say(&mut w, &format!("250- Listing {p}\r\n {}250 End", l))?
                    }
                    None if p == "/" => {
                        drop(g);
                        say(&mut w, "250- Listing /\r\n type=dir; /\r\n250 End")?
                    }
                    None => {
                        drop(g);
                        say(&mut w, "550 No such file or directory")?
                    }
                }
            }
            "SIZE" => {
                let p = abs(&arg);
                let g = st.lock().unwrap();
                match g.fs.get(&p) {
                    Some(Node::File(d, _)) => {
                        let n = view(d, self2elf).len();
                        drop(g);
                        say(&mut w, &format!("213 {n}"))?
                    }

                    Some(Node::Dir(_)) if ftpsrv => {
                        drop(g);
                        say(&mut w, "213 512")?
                    }
                    _ => {
                        drop(g);
                        say(&mut w, "550 No such file or directory")?
                    }
                }
            }
            "MKD" | "XMKD" => {
                let p = abs(&arg);
                let mut g = st.lock().unwrap();
                if g.exists(&p) {
                    drop(g);
                    say(&mut w, "550 File exists")?
                } else if !g.is_dir(&rp::parent(&p)) {
                    drop(g);
                    say(&mut w, "550 No such file or directory")?
                } else {
                    g.fs.insert(p.clone(), Node::Dir(0o755));
                    drop(g);
                    say(
                        &mut w,
                        &if ftpsrv {
                            "226 Directory created".to_string()
                        } else {
                            format!("257 \"{p}\" created")
                        },
                    )?
                }
            }
            "RMD" | "XRMD" => {
                let p = abs(&arg);
                let mut g = st.lock().unwrap();
                if !g.is_dir(&p) {
                    drop(g);
                    say(&mut w, "550 No such file or directory")?
                } else if !g.children(&p).is_empty() {
                    drop(g);
                    say(&mut w, "550 Directory not empty")?
                } else {
                    g.fs.remove(&p);
                    drop(g);
                    say(&mut w, &format!("{} Directory deleted", okc("250")))?
                }
            }
            "DELE" => {
                let p = abs(&arg);
                let mut g = st.lock().unwrap();
                if matches!(g.fs.get(&p), Some(Node::File(..))) {
                    g.fs.remove(&p);
                    drop(g);
                    say(&mut w, &format!("{} File deleted", okc("250")))?
                } else {
                    drop(g);
                    say(&mut w, "550 No such file or directory")?
                }
            }
            "RNFR" => {
                let p = abs(&arg);
                if st.lock().unwrap().exists(&p) {
                    rnfr = Some(p);
                    say(&mut w, "350 Awaiting new name")?
                } else {
                    say(&mut w, "550 No such file or directory")?
                }
            }
            "RNTO" => {
                let to = abs(&arg);
                let Some(from) = rnfr.take() else {
                    say(&mut w, "503 RNFR first")?;
                    continue;
                };
                let mut g = st.lock().unwrap();
                if g.faults.rnto_refuses_overwrite && g.exists(&to) {
                    drop(g);
                    say(&mut w, "550 File exists")?;
                } else if !g.is_dir(&rp::parent(&to)) {
                    drop(g);
                    say(&mut w, "550 No such file or directory")?;
                } else {
                    let prefix = format!("{from}/");
                    let moved: Vec<(String, Node)> =
                        g.fs.iter()
                            .filter(|(k, _)| **k == from || k.starts_with(&prefix))
                            .map(|(k, v)| (k.clone(), v.clone()))
                            .collect();
                    for (k, _) in &moved {
                        g.fs.remove(k);
                    }
                    for (k, v) in moved {
                        let nk = format!("{to}{}", &k[from.len()..]);
                        g.fs.insert(nk, v);
                    }
                    drop(g);
                    say(&mut w, &format!("{} Path renamed", okc("250")))?;
                }
            }
            "SITE" | "CHMOD" => {
                let args = if cmd == "SITE" {
                    let mut it = arg.splitn(2, ' ');
                    if !it.next().map(|s| s.eq_ignore_ascii_case("CHMOD")).unwrap_or(false) {
                        say(&mut w, "500 unknown SITE command")?;
                        continue;
                    }
                    it.next().unwrap_or("").to_string()
                } else {
                    arg.clone()
                };
                let mut g = st.lock().unwrap();
                if g.faults.no_chmod {
                    drop(g);
                    say(&mut w, "500 SITE CHMOD not understood")?;
                    continue;
                }
                let mut it = args.splitn(2, ' ');
                let mode = u32::from_str_radix(it.next().unwrap_or("0"), 8).unwrap_or(0);
                let p = abs(it.next().unwrap_or(""));
                match g.fs.get_mut(&p) {
                    Some(Node::File(_, m)) | Some(Node::Dir(m)) => {
                        *m = mode;
                        drop(g);
                        say(&mut w, "200 OK")?
                    }
                    None => {
                        drop(g);
                        say(&mut w, "550 No such file or directory")?
                    }
                }
            }
            "RETR" => {
                let p = abs(&arg);
                let (data, cut) = {
                    let mut g = st.lock().unwrap();
                    let data = match g.fs.get(&p) {
                        Some(Node::File(d, _)) => Some(view(d, self2elf).to_vec()),
                        _ => None,
                    };
                    (data, g.faults.cut_next_retr_after.take())
                };
                let Some(l) = pasv.take() else {
                    say(&mut w, "425 use PASV first")?;
                    continue;
                };
                let Some(data) = data else {
                    say(&mut w, "550 No such file or directory")?;
                    continue;
                };
                let from = (rest as usize).min(data.len());
                rest = 0;
                say(&mut w, "150 Starting data transfer")?;
                if let Some(mut d) = accept_data(&l) {
                    match cut {
                        Some(n) => {
                            let _ = d.write_all(&data[from..(from + n).min(data.len())]);
                            let _ = d.shutdown(Shutdown::Both);
                            let _ = ctl.shutdown(Shutdown::Both);
                            return Ok(());
                        }
                        None => {
                            let _ = d.write_all(&data[from..]);
                            let _ = d.shutdown(Shutdown::Both);
                        }
                    }
                }
                say(&mut w, "226 Transfer completed")?
            }
            "STOR" | "APPE" => {
                let p = abs(&arg);
                let Some(l) = pasv.take() else {
                    say(&mut w, "425 use PASV first")?;
                    continue;
                };
                {
                    let mut g = st.lock().unwrap();
                    if !g.is_dir(&rp::parent(&p)) {
                        drop(g);
                        say(&mut w, "550 No such file or directory")?;
                        continue;
                    }
                    if matches!(g.fs.get(&p), Some(Node::Dir(_))) {
                        drop(g);
                        say(&mut w, "550 Is a directory")?;
                        continue;
                    }
                    let keep = rest as usize;
                    rest = 0;
                    match (cmd.as_str(), g.fs.get_mut(&p)) {
                        ("APPE", Some(Node::File(..))) => {}
                        ("APPE", None) if ftpsrv => {
                            drop(g);
                            say(&mut w, "550 No such file or directory")?;
                            continue;
                        }
                        (_, Some(Node::File(d, _))) if keep > 0 => d.truncate(keep),

                        _ => {
                            g.fs.insert(p.clone(), Node::File(Vec::new(), 0o644));
                        }
                    }
                }
                say(&mut w, "150 Opening data transfer")?;
                let Some(mut d) = accept_data(&l) else {
                    say(&mut w, "425 can't open data connection")?;
                    continue;
                };
                let (cut, lose, full) = {
                    let mut g = st.lock().unwrap();
                    (
                        g.faults.cut_next_store_after.take(),
                        g.faults.lose_tail_on_cut,
                        g.faults.full_after.take(),
                    )
                };
                let mut got = 0usize;
                let mut buf = vec![0u8; 64 * 1024];
                loop {
                    let n = match d.read(&mut buf) {
                        Ok(0) => break,
                        Ok(n) => n,
                        Err(_) => break,
                    };
                    let mut take = n;
                    if let Some(c) = cut {
                        take = take.min(c.saturating_sub(got));
                    }
                    let mut g = st.lock().unwrap();
                    if let Some(Node::File(data, _)) = g.fs.get_mut(&p) {
                        data.extend_from_slice(&buf[..take]);
                    }
                    got += take;
                    if let Some(c) = cut {
                        if got >= c {
                            if let Some(Node::File(data, _)) = g.fs.get_mut(&p) {
                                let keep = data.len().saturating_sub(lose);
                                data.truncate(keep);
                            }
                            drop(g);
                            let _ = d.shutdown(Shutdown::Both);
                            let _ = ctl.shutdown(Shutdown::Both);
                            return Ok(());
                        }
                    }
                    drop(g);
                    if let Some(f) = full {
                        if got >= f {
                            let _ = d.shutdown(Shutdown::Both);
                            say(&mut w, "552 Exceeded storage allocation (no space left on device)")?;
                            break;
                        }
                    }
                }
                if full.map(|f| got >= f).unwrap_or(false) {
                    continue;
                }
                say(&mut w, "226 Data transfer complete")?
            }
            _ => say(
                &mut w,
                if ftpsrv {
                    "502 Command not recognized"
                } else {
                    "502 command not implemented"
                },
            )?,
        }
    }
}
