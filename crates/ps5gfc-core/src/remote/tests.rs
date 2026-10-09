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

use std::sync::Arc;

use super::download::download_paths;
use super::engine::{upload_items, verify_uploaded, Existing, Item, UploadParams};
use super::mock::MockConsole;
use super::path as rp;
use super::prospero::Conflict;
use super::upload::{RemoteWriter, SeqSink};
use super::RemoteKind;
use crate::ctl::{Cancel, Ctx};
use crate::io::{MemReader, ReadAt};
use crate::{Error, Result};

fn data(len: usize, seed: u8) -> Vec<u8> {
    (0..len)
        .map(|i| {
            (i as u8)
                .wrapping_mul(31)
                .wrapping_add(seed)
                .wrapping_add((i >> 9) as u8)
        })
        .collect()
}

fn noop() -> Arc<dyn Fn(u64) + Send + Sync> {
    Arc::new(|_| {})
}

fn no_junk(m: &MockConsole) {
    let junk: Vec<String> = m.paths().into_iter().filter(|p| p.contains(".pmgr-")).collect();
    assert!(junk.is_empty(), "sobrou lixo no console: {junk:?}");
    assert_eq!(m.reservations(), 0, "ficou uma reserva de envio no console");
}

#[test]
fn connect_and_browse() {
    let m = MockConsole::start();
    m.put_file("/data/etaHEN/games/a.txt", b"hello");
    let c = m.client();
    let info = c.connect().unwrap();
    assert_eq!(info.name, "Prospero Manager");
    assert_eq!(info.volumes.len(), 2);
    assert_eq!(c.volume_of(&info.volumes, "/data/etaHEN/games").unwrap().path, "/data");
    let l = c.list("/data/etaHEN/games").unwrap();
    assert_eq!(l.len(), 1);
    assert_eq!((l[0].name.as_str(), l[0].size, l[0].is_dir), ("a.txt", 5, false));
    assert!(c.stat("/data/etaHEN/games/a.txt").unwrap().is_some());
    assert!(c.stat("/data/nada").unwrap().is_none());
    assert!(c.stat("/nao/existe/x").unwrap().is_none());
}

#[test]
fn not_a_prospero_is_rejected() {
    let l = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = l.local_addr().unwrap().port();
    std::thread::spawn(move || {
        use std::io::{Read, Write};
        if let Ok((mut s, _)) = l.accept() {
            let mut b = [0u8; 1024];
            let _ = s.read(&mut b);
            let _ = s.write_all(
                b"HTTP/1.1 200 OK\r\nContent-Length: 15\r\nContent-Type: application/json\r\n\r\n{\"name\":\"nginx\"}",
            );
        }
    });
    let c = super::Prospero::new(super::Endpoint {
        host: "127.0.0.1".into(),
        port,
    });
    match c.connect() {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Protocol),
        other => panic!("esperava erro de protocolo, veio {:?}", other.map(|i| i.name)),
    }
}

#[test]
fn unreachable_is_reported() {
    let c = super::Prospero::new(super::Endpoint {
        host: "127.0.0.1".into(),
        port: 1,
    });
    match c.connect() {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Unreachable),
        other => panic!("esperava inalcançável, veio {:?}", other.map(|i| i.name)),
    }
}

#[test]
fn folder_operations() {
    let m = MockConsole::start();
    let c = m.client();
    c.mkdir_all("/data/a/b/c", None).unwrap();
    assert!(m.is_dir("/data/a/b/c"));
    c.mkdir("/data/a/b/c").unwrap();
    c.rename("/data/a/b", "/data/a/z").unwrap();
    assert!(m.is_dir("/data/a/z/c") && !m.is_dir("/data/a/b"));
    m.put_file("/data/a/z/f.bin", b"x");
    let job = c.copy_to("/data/a/z", "/data/copia", Conflict::Cancel).unwrap();
    let j = c.wait_job(job, &Cancel::new(), |_| {}).unwrap();
    assert!(j.finished() && !j.failed());
    assert_eq!(m.file("/data/copia/f.bin").unwrap(), b"x");
    c.delete_and_wait("/data/a", &Cancel::new()).unwrap();
    assert!(!m.is_dir("/data/a"));
    assert!(c.delete("/data").is_err());
}

#[test]
fn mutations_wait_while_console_is_busy() {
    let m = MockConsole::start();
    m.faults(|f| f.busy_mutations = 2);
    let c = m.client();
    c.mkdir("/data/depois-de-ocupado").unwrap();
    assert!(m.is_dir("/data/depois-de-ocupado"));

    m.faults(|f| f.busy_mutations = 1000);
    let c2 = m.client().with_busy_wait(std::time::Duration::from_millis(300));
    match c2.mkdir("/data/x") {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Busy),
        other => panic!("{other:?}"),
    }
}

fn send_via_writer(c: &super::Client, path: &str, bytes: &[u8], chunk: usize, overwrite: bool) -> Result<()> {
    let cancel = Cancel::new();
    let mut w = RemoteWriter::with_chunk(c, path, bytes.len() as u64, overwrite, &cancel, noop(), chunk)?;
    for piece in bytes.chunks(70_001) {
        w.write_all(piece)?;
    }
    w.finish()
}

#[test]
fn chunked_upload_roundtrip() {
    let m = MockConsole::start();
    let c = m.client();
    let bytes = data(3 * 1024 * 1024 + 1234, 1);
    send_via_writer(&c, "/data/img.exfat", &bytes, 1 << 20, false).unwrap();
    assert_eq!(m.file("/data/img.exfat").unwrap(), bytes);
    no_junk(&m);
    assert!(m.faults(|f| f.chunks_seen) >= 4);

    send_via_writer(&c, "/data/one.bin", b"abc", 1 << 20, false).unwrap();
    assert_eq!(m.file("/data/one.bin").unwrap(), b"abc");
    send_via_writer(&c, "/data/empty.bin", b"", 1 << 20, false).unwrap();
    assert_eq!(m.file("/data/empty.bin").unwrap(), b"");
}

#[test]
fn existing_destination_needs_overwrite() {
    let m = MockConsole::start();
    m.put_file("/data/img.exfat", b"velho");
    let c = m.client();
    let bytes = data(300_000, 2);
    match send_via_writer(&c, "/data/img.exfat", &bytes, 100_000, false) {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
        other => panic!("{other:?}"),
    }
    assert_eq!(m.file("/data/img.exfat").unwrap(), b"velho");
    no_junk(&m);
    send_via_writer(&c, "/data/img.exfat", &bytes, 100_000, true).unwrap();
    assert_eq!(m.file("/data/img.exfat").unwrap(), bytes);
    no_junk(&m);
}

#[test]
fn connection_cut_mid_chunk_resumes_from_what_the_console_kept() {
    let m = MockConsole::start();
    let c = m.client();
    let bytes = data(2 * 1024 * 1024, 3);

    m.faults(|f| f.cut_next_chunk_after = None);
    let cancel = Cancel::new();
    let mut w =
        RemoteWriter::with_chunk(&c, "/data/cut.bin", bytes.len() as u64, false, &cancel, noop(), 1 << 20).unwrap();
    w.write_all(&bytes[..1 << 20]).unwrap();

    for _ in 0..200 {
        if m.faults(|f| f.chunks_seen) >= 1 {
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }
    std::thread::sleep(std::time::Duration::from_millis(100));
    m.faults(|f| f.cut_next_chunk_after = Some(300_000));
    w.write_all(&bytes[1 << 20..]).unwrap();
    w.finish().unwrap();
    assert_eq!(m.file("/data/cut.bin").unwrap(), bytes);
    no_junk(&m);
}

#[test]
fn lost_reply_after_commit_is_not_resent_twice() {
    let m = MockConsole::start();
    let c = m.client();
    let bytes = data(1_500_000, 4);
    m.faults(|f| f.drop_reply_next_chunk = true);
    send_via_writer(&c, "/data/lost.bin", &bytes, 700_000, false).unwrap();
    assert_eq!(m.file("/data/lost.bin").unwrap(), bytes);
    no_junk(&m);
}

#[test]
fn abandoned_upload_releases_the_console() {
    let m = MockConsole::start();
    let c = m.client();
    let bytes = data(3 * 1024 * 1024, 5);
    {
        let cancel = Cancel::new();
        let mut w = RemoteWriter::with_chunk(
            &c,
            "/data/abandon.bin",
            bytes.len() as u64,
            false,
            &cancel,
            noop(),
            1 << 20,
        )
        .unwrap();
        w.write_all(&bytes[..2 << 20]).unwrap();
    }

    no_junk(&m);
    assert!(m.file("/data/abandon.bin").is_none());
    c.mkdir("/data/pode-criar").unwrap();
}

#[test]
fn abandoned_upload_over_existing_file_keeps_the_old_one() {
    let m = MockConsole::start();
    m.put_file("/data/keep.bin", b"original");
    let c = m.client();
    let bytes = data(2 * 1024 * 1024, 6);
    {
        let cancel = Cancel::new();
        let mut w =
            RemoteWriter::with_chunk(&c, "/data/keep.bin", bytes.len() as u64, true, &cancel, noop(), 1 << 20).unwrap();
        w.write_all(&bytes[..1 << 20]).unwrap();
    }
    no_junk(&m);
    assert_eq!(m.file("/data/keep.bin").unwrap(), b"original");
}

#[test]
fn cancel_stops_a_running_upload_and_cleans() {
    let m = MockConsole::start();
    let c = m.client();
    let bytes = data(4 * 1024 * 1024, 7);
    let cancel = Cancel::new();
    let mut w = RemoteWriter::with_chunk(
        &c,
        "/data/cancel.bin",
        bytes.len() as u64,
        false,
        &cancel,
        noop(),
        1 << 20,
    )
    .unwrap();
    w.write_all(&bytes[..2 << 20]).unwrap();
    cancel.cancel();
    assert!(matches!(w.write_all(&bytes[2 << 20..]), Err(Error::Cancelled)));
    drop(w);
    no_junk(&m);
    assert!(m.file("/data/cancel.bin").is_none());
}

#[test]
fn wrong_total_is_rejected_and_cleaned() {
    let m = MockConsole::start();
    let c = m.client();
    let cancel = Cancel::new();
    let mut w = RemoteWriter::with_chunk(&c, "/data/short.bin", 3_000_000, false, &cancel, noop(), 1 << 20).unwrap();
    w.write_all(&data(1_500_000, 8)).unwrap();
    assert!(w.finish().is_err());
    no_junk(&m);
    assert!(m.file("/data/short.bin").is_none());
}

fn tree_items() -> (Vec<Item>, Vec<Arc<Vec<u8>>>) {
    let files: Vec<(&str, usize)> = vec![
        ("Jogo/eboot.bin", 5000),
        ("Jogo/sce_sys/param.json", 300),
        ("Jogo/data/big.pak", 3_000_000),
        ("Jogo/data/sub/a.bin", 10),
        ("Jogo/vazio.txt", 0),
    ];
    let mut items = vec![
        Item {
            rel: "Jogo".into(),
            is_dir: true,
            size: 0,
            src: 0,
        },
        Item {
            rel: "Jogo/sce_sys".into(),
            is_dir: true,
            size: 0,
            src: 0,
        },
        Item {
            rel: "Jogo/data".into(),
            is_dir: true,
            size: 0,
            src: 0,
        },
        Item {
            rel: "Jogo/data/sub".into(),
            is_dir: true,
            size: 0,
            src: 0,
        },
    ];
    let mut bufs = Vec::new();
    for (i, (rel, n)) in files.iter().enumerate() {
        bufs.push(Arc::new(data(*n, i as u8)));
        items.push(Item {
            rel: rel.to_string(),
            is_dir: false,
            size: *n as u64,
            src: i,
        });
    }
    (items, bufs)
}

#[test]
fn upload_tree_makes_all_folders_before_any_file() {
    let m = MockConsole::start();
    let c = m.client();
    let (items, bufs) = tree_items();
    let open = |i: usize| -> Result<Arc<dyn ReadAt>> { Ok(Arc::new(MemReader(bufs[i].clone()))) };
    let params = UploadParams {
        client: &c,
        dest_dir: "/data/etaHEN/games",
        conns: 3,
        existing: Existing::Fail,
        want_crc: true,
    };
    let ctx = Ctx::new();
    let st = upload_items(&params, &items, &open, &ctx).unwrap();
    assert_eq!((st.files, st.dirs), (5, 4));
    for (rel, i) in [
        ("Jogo/eboot.bin", 0),
        ("Jogo/sce_sys/param.json", 1),
        ("Jogo/data/big.pak", 2),
        ("Jogo/data/sub/a.bin", 3),
        ("Jogo/vazio.txt", 4),
    ] {
        assert_eq!(m.file(&rp::join("/data/etaHEN/games", rel)).unwrap(), *bufs[i], "{rel}");
    }
    no_junk(&m);

    let reqs = m.requests();
    let last_mkdir = reqs.iter().rposition(|r| r.contains("/mkdir")).unwrap();
    let first_upload = reqs.iter().position(|r| r.contains("/upload")).unwrap();
    assert!(
        last_mkdir < first_upload,
        "criou pasta depois de começar a enviar: {reqs:?}"
    );

    verify_uploaded(&c, &st.uploaded, 2, &ctx).unwrap();
}

#[test]
fn verify_catches_corruption() {
    let m = MockConsole::start();
    let c = m.client();
    let (items, bufs) = tree_items();
    let open = |i: usize| -> Result<Arc<dyn ReadAt>> { Ok(Arc::new(MemReader(bufs[i].clone()))) };
    let params = UploadParams {
        client: &c,
        dest_dir: "/data/x",
        conns: 2,
        existing: Existing::Replace,
        want_crc: true,
    };
    let ctx = Ctx::new();
    let st = upload_items(&params, &items, &open, &ctx).unwrap();
    let mut bad = bufs[0].as_ref().clone();
    bad[100] ^= 0xff;
    m.put_file("/data/x/Jogo/eboot.bin", &bad);
    assert!(verify_uploaded(&c, &st.uploaded, 2, &ctx).is_err());
}

#[test]
fn existing_policies() {
    let m = MockConsole::start();
    m.put_file("/data/x/Jogo/eboot.bin", b"ja existe");
    let c = m.client();
    let (items, bufs) = tree_items();
    let open = |i: usize| -> Result<Arc<dyn ReadAt>> { Ok(Arc::new(MemReader(bufs[i].clone()))) };
    let ctx = Ctx::new();

    let fail = UploadParams {
        client: &c,
        dest_dir: "/data/x",
        conns: 2,
        existing: Existing::Fail,
        want_crc: false,
    };
    match upload_items(&fail, &items, &open, &ctx) {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
        other => panic!("{:?}", other.map(|s| s.files)),
    }
    assert_eq!(m.file("/data/x/Jogo/eboot.bin").unwrap(), b"ja existe");
    assert!(
        m.file("/data/x/Jogo/data/big.pak").is_none(),
        "nada deve ser enviado quando há conflito"
    );

    let skip = UploadParams {
        client: &c,
        dest_dir: "/data/x",
        conns: 2,
        existing: Existing::Skip,
        want_crc: false,
    };
    let st = upload_items(&skip, &items, &open, &Ctx::new()).unwrap();
    assert_eq!((st.files, st.skipped), (4, 1));
    assert_eq!(m.file("/data/x/Jogo/eboot.bin").unwrap(), b"ja existe");

    let replace = UploadParams {
        client: &c,
        dest_dir: "/data/x",
        conns: 2,
        existing: Existing::Replace,
        want_crc: false,
    };
    upload_items(&replace, &items, &open, &Ctx::new()).unwrap();
    assert_eq!(m.file("/data/x/Jogo/eboot.bin").unwrap(), *bufs[0]);
}

#[test]
fn failed_source_stops_everything_with_the_real_error() {
    let m = MockConsole::start();
    let c = m.client();
    let (items, bufs) = tree_items();
    let open = |i: usize| -> Result<Arc<dyn ReadAt>> {
        if i == 3 {
            return Err(Error::invalid("origem ilegível"));
        }
        Ok(Arc::new(MemReader(bufs[i].clone())))
    };
    let params = UploadParams {
        client: &c,
        dest_dir: "/data/x",
        conns: 3,
        existing: Existing::Replace,
        want_crc: false,
    };
    match upload_items(&params, &items, &open, &Ctx::new()) {
        Err(Error::Invalid(m)) => assert!(m.contains("origem ilegível")),
        other => panic!(
            "o erro verdadeiro deveria sobreviver ao cancelamento dos outros: {:?}",
            other.map(|s| s.files)
        ),
    }
    no_junk(&m);
}

#[test]
fn download_folder_and_file() {
    let m = MockConsole::start();
    m.put_file("/data/g/Jogo/a.bin", &data(2_000_000, 9));
    m.put_file("/data/g/Jogo/sub/b.txt", b"bbb");
    m.put_file("/data/g/solto.cfg", b"cfg");
    let c = m.client();
    let dir = tempfile::tempdir().unwrap();
    let ctx = Ctx::new();
    let st = download_paths(
        &c,
        &["/data/g/Jogo".into(), "/data/g/solto.cfg".into()],
        dir.path(),
        Conflict::Cancel,
        2,
        &ctx,
    )
    .unwrap();
    assert_eq!((st.files, st.bytes), (3, 2_000_000 + 3 + 3));
    assert_eq!(
        std::fs::read(dir.path().join("Jogo/a.bin")).unwrap(),
        data(2_000_000, 9)
    );
    assert_eq!(std::fs::read(dir.path().join("Jogo/sub/b.txt")).unwrap(), b"bbb");
    assert_eq!(std::fs::read(dir.path().join("solto.cfg")).unwrap(), b"cfg");
    assert!(!dir.path().join("solto.cfg.ps5gfc-part").exists());

    assert!(download_paths(
        &c,
        &["/data/g/solto.cfg".into()],
        dir.path(),
        Conflict::Cancel,
        2,
        &Ctx::new()
    )
    .is_err());
    let st = download_paths(
        &c,
        &["/data/g/solto.cfg".into()],
        dir.path(),
        Conflict::Skip,
        2,
        &Ctx::new(),
    )
    .unwrap();
    assert_eq!(st.skipped, 1);
    download_paths(
        &c,
        &["/data/g/solto.cfg".into()],
        dir.path(),
        Conflict::KeepBoth,
        2,
        &Ctx::new(),
    )
    .unwrap();
    assert_eq!(
        std::fs::read(dir.path().join("solto (2).cfg"))
            .unwrap_or_default()
            .len()
            + std::fs::read(dir.path().join("solto.cfg (2)"))
                .unwrap_or_default()
                .len(),
        3
    );
}

#[test]
fn download_retries_when_connection_drops() {
    let m = MockConsole::start();
    let bytes = data(1_000_000, 10);
    m.put_file("/data/f.bin", &bytes);
    m.faults(|f| f.cut_next_download_after = Some(400_000));
    let c = m.client();
    let dir = tempfile::tempdir().unwrap();
    let ctx = Ctx::new();
    download_paths(&c, &["/data/f.bin".into()], dir.path(), Conflict::Cancel, 1, &ctx).unwrap();
    assert_eq!(std::fs::read(dir.path().join("f.bin")).unwrap(), bytes);

    assert_eq!(ctx.progress.snapshot().done, 1_000_000);
}

#[test]
fn preflight_and_conflicts() {
    let m = MockConsole::start();
    m.put_file("/data/a.bin", b"x");
    m.set_free(1000);
    let c = m.client();
    assert!(c.preflight("/data/novo.bin", 500, false).unwrap().ok);
    let p = c.preflight("/data/novo.bin", 5000, false).unwrap();
    assert!(!p.ok && p.available == 1000);
    assert_eq!(
        c.conflicts(&["/data/a.bin".into(), "/data/b.bin".into()]).unwrap(),
        vec!["/data/a.bin".to_string()]
    );
    assert_eq!(
        c.unique_names(&["/data/a.bin".into(), "/data/b.bin".into()]).unwrap(),
        vec!["/data/a (1).bin".to_string(), "/data/b.bin".to_string()]
    );
}

mod convert_to_console {
    use super::*;
    use crate::convert::{convert, convert_to, AmprMode, ConvertOptions, Dest};
    use crate::source::{Format, FsKind};
    use std::path::Path;

    fn make_game(dir: &Path) {
        let w = |rel: &str, bytes: &[u8]| {
            let p = dir.join(rel);
            std::fs::create_dir_all(p.parent().unwrap()).unwrap();
            std::fs::write(p, bytes).unwrap();
        };
        w("eboot.bin", &data(200_000, 1));
        w(
            "sce_sys/param.json",
            br#"{"titleId":"PPSA99999","contentVersion":"01.000.000","localizedParameters":{"defaultLanguage":"en-US","en-US":{"titleName":"Jogo de Teste"}}}"#,
        );
        w("data/a.pak", &data(2_500_000, 2));
        w("data/b.bin", &vec![0u8; 70_000]);
        w("data/sub/c.txt", b"texto");
        w("vazio.txt", b"");
    }

    fn opts() -> ConvertOptions {
        ConvertOptions {
            threads: 3,
            level: 5,
            preserve_times: false,
            ampr: AmprMode::Never,
            verify: true,
            out_name: Some("PPSA99999".into()),
            name_mode: None,
            ..Default::default()
        }
    }

    fn console(m: &MockConsole, dir: &str) -> Dest {
        Dest::Console {
            client: m.client(),
            dir: dir.into(),
        }
    }

    #[test]
    fn image_to_console_folder() {
        let m = MockConsole::start();
        let src = tempfile::tempdir().unwrap();
        make_game(src.path());

        let same = convert_to(
            src.path(),
            &console(&m, "/data/etaHEN/games"),
            Format::Folder,
            &opts(),
            &Ctx::new(),
        );
        assert!(same.is_err());

        let img = tempfile::tempdir().unwrap();
        let rep = convert(
            src.path(),
            img.path(),
            Format::Image(FsKind::Exfat),
            &opts(),
            &Ctx::new(),
        )
        .unwrap();
        let rep2 = convert_to(
            &rep.output,
            &console(&m, "/data/etaHEN/games"),
            Format::Folder,
            &opts(),
            &Ctx::new(),
        )
        .unwrap();
        assert!(rep2.console);
        assert_eq!(rep2.output.to_string_lossy(), "/data/etaHEN/games/PPSA99999");
        for rel in [
            "eboot.bin",
            "sce_sys/param.json",
            "data/a.pak",
            "data/b.bin",
            "data/sub/c.txt",
            "vazio.txt",
        ] {
            assert_eq!(
                m.file(&format!("/data/etaHEN/games/PPSA99999/{rel}")).unwrap(),
                std::fs::read(src.path().join(rel)).unwrap(),
                "{rel}"
            );
        }
        no_junk(&m);

        assert!(convert_to(
            &rep.output,
            &console(&m, "/data/etaHEN/games"),
            Format::Folder,
            &opts(),
            &Ctx::new()
        )
        .is_err());
    }

    fn remote_equals_local(target: Format) {
        let m = MockConsole::start();
        let src = tempfile::tempdir().unwrap();
        make_game(src.path());
        let out_local = tempfile::tempdir().unwrap();
        let local = convert(src.path(), out_local.path(), target, &opts(), &Ctx::new()).unwrap();
        let local_bytes = std::fs::read(&local.output).unwrap();

        let ctx = Ctx::new();
        let rep = convert_to(src.path(), &console(&m, "/data/etaHEN/games"), target, &opts(), &ctx).unwrap();
        assert!(rep.console);
        let remote_path = rep.output.to_string_lossy().into_owned();
        assert!(
            remote_path.starts_with("/data/etaHEN/games/PPSA99999."),
            "{remote_path}"
        );
        let remote_bytes = m.file(&remote_path).expect("arquivo no console");
        assert_eq!(
            remote_bytes.len(),
            local_bytes.len(),
            "tamanho diferente do gerado localmente"
        );
        assert_eq!(rep.out_bytes, local.out_bytes);
        if !matches!(target, Format::Ffpfsc(_)) {
            assert!(
                remote_bytes == local_bytes,
                "o conteúdo enviado difere do gerado localmente"
            );
        } else {
            let tmp = tempfile::tempdir().unwrap();
            let p = tmp.path().join("x.ffpfsc");
            std::fs::write(&p, &remote_bytes).unwrap();
            let a = crate::source::open_source(&p, &Ctx::new()).unwrap();
            let b = crate::source::open_source(src.path(), &Ctx::new()).unwrap();
            crate::verify::compare_volumes(b.volume.as_ref(), a.volume.as_ref(), 2, &Ctx::new()).unwrap();
        }
        no_junk(&m);
    }

    #[test]
    fn folder_to_exfat_on_console_matches_local() {
        remote_equals_local(Format::Image(FsKind::Exfat));
    }

    #[test]
    fn folder_to_ffpkg_on_console_matches_local() {
        remote_equals_local(Format::Image(FsKind::Ufs2));
    }

    #[test]
    fn folder_to_ffpfsc_on_console_is_a_valid_container() {
        remote_equals_local(Format::Ffpfsc(FsKind::Exfat));
    }

    #[test]
    fn folder_to_ffpfsc_with_ufs2_and_pfs_inside_on_console() {
        remote_equals_local(Format::Ffpfsc(FsKind::Ufs2));
        remote_equals_local(Format::Ffpfsc(FsKind::Pfs));
    }

    #[test]
    fn existing_output_needs_overwrite_and_space_is_checked() {
        let m = MockConsole::start();
        let src = tempfile::tempdir().unwrap();
        make_game(src.path());
        let mut o = opts();
        o.verify = false;
        let t = Format::Image(FsKind::Exfat);
        convert_to(src.path(), &console(&m, "/data/g"), t, &o, &Ctx::new()).unwrap();
        let first = m.file("/data/g/PPSA99999.exfat").unwrap();

        assert!(convert_to(src.path(), &console(&m, "/data/g"), t, &o, &Ctx::new()).is_err());
        assert_eq!(m.file("/data/g/PPSA99999.exfat").unwrap(), first);

        o.overwrite = true;
        o.free_mib = 1;
        convert_to(src.path(), &console(&m, "/data/g"), t, &o, &Ctx::new()).unwrap();
        assert_ne!(m.file("/data/g/PPSA99999.exfat").unwrap().len(), first.len());
        no_junk(&m);

        m.set_free(1000);
        o.overwrite = false;
        let r = convert_to(src.path(), &console(&m, "/data/outro"), t, &o, &Ctx::new());
        assert!(r.is_err());
        assert!(m.file("/data/outro/PPSA99999.exfat").is_none());
        no_junk(&m);
    }

    #[test]
    fn cancelling_a_conversion_leaves_nothing_behind() {
        let m = MockConsole::start();
        let src = tempfile::tempdir().unwrap();
        make_game(src.path());

        std::fs::write(src.path().join("data/big.pak"), data(100_000_000, 3)).unwrap();
        let mut o = opts();
        o.verify = false;
        let ctx = Ctx::new();
        let cancel = ctx.cancel.clone();
        let m_ref = &m;
        let res = std::thread::scope(|s| {
            let h = s.spawn(|| {
                convert_to(
                    src.path(),
                    &console(m_ref, "/data/g"),
                    Format::Image(FsKind::Exfat),
                    &o,
                    &ctx,
                )
            });

            for _ in 0..5000 {
                if m_ref.faults(|f| f.chunks_seen) >= 1 {
                    break;
                }
                std::thread::sleep(std::time::Duration::from_millis(2));
            }
            cancel.cancel();
            h.join().unwrap()
        });
        assert!(matches!(res, Err(Error::Cancelled)), "{:?}", res.map(|r| r.out_bytes));
        no_junk(&m);
        assert!(m.file("/data/g/PPSA99999.exfat").is_none());

        m.client().mkdir("/data/g/livre").unwrap();
    }
}

mod upload_local_policies {
    use super::*;
    use crate::remote::engine::upload_local;
    use std::path::PathBuf;

    fn make(dir: &std::path::Path, tag: u8) -> Vec<PathBuf> {
        std::fs::create_dir_all(dir.join("pasta/sub")).unwrap();
        std::fs::write(dir.join("a.txt"), data(1000, tag)).unwrap();
        std::fs::write(dir.join("pasta/b.bin"), data(70_000, tag)).unwrap();
        std::fs::write(dir.join("pasta/sub/c.bin"), data(5, tag)).unwrap();
        vec![dir.join("a.txt"), dir.join("pasta")]
    }

    #[test]
    fn cancel_skip_replace_and_keep_both() {
        let m = MockConsole::start();
        let c = m.client();
        let src = tempfile::tempdir().unwrap();
        let items = make(src.path(), 1);

        let st = upload_local(&c, &items, "/data/dst", Conflict::Cancel, 2, &Ctx::new()).unwrap();
        assert_eq!((st.files, st.dirs, st.skipped), (3, 2, 0));
        assert_eq!(m.file("/data/dst/pasta/sub/c.bin").unwrap(), data(5, 1));

        let items2 = make(src.path(), 2);
        match upload_local(&c, &items2, "/data/dst", Conflict::Cancel, 2, &Ctx::new()) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
            other => panic!("{:?}", other.map(|s| s.files)),
        }
        assert_eq!(m.file("/data/dst/a.txt").unwrap(), data(1000, 1));

        let st = upload_local(&c, &items2, "/data/dst", Conflict::Skip, 2, &Ctx::new()).unwrap();
        assert_eq!((st.files, st.skipped), (0, 3));
        assert_eq!(m.file("/data/dst/pasta/b.bin").unwrap(), data(70_000, 1));

        let st = upload_local(&c, &items2, "/data/dst", Conflict::Replace, 2, &Ctx::new()).unwrap();
        assert_eq!(st.files, 3);
        assert_eq!(m.file("/data/dst/a.txt").unwrap(), data(1000, 2));
        assert_eq!(m.file("/data/dst/pasta/sub/c.bin").unwrap(), data(5, 2));

        let items3 = make(src.path(), 3);
        let st = upload_local(&c, &items3, "/data/dst", Conflict::KeepBoth, 2, &Ctx::new()).unwrap();
        assert_eq!(st.files, 3);
        assert_eq!(m.file("/data/dst/a (1).txt").unwrap(), data(1000, 3));
        assert_eq!(m.file("/data/dst/pasta (1)/sub/c.bin").unwrap(), data(5, 3));

        assert_eq!(m.file("/data/dst/a.txt").unwrap(), data(1000, 2));
        no_junk(&m);
    }

    #[test]
    fn missing_source_and_no_space_fail_cleanly() {
        let m = MockConsole::start();
        let c = m.client();
        let src = tempfile::tempdir().unwrap();
        let items = make(src.path(), 1);
        assert!(upload_local(
            &c,
            &[src.path().join("nao_existe")],
            "/data/x",
            Conflict::Cancel,
            2,
            &Ctx::new()
        )
        .is_err());
        m.set_free(100);
        match upload_local(&c, &items, "/data/y", Conflict::Cancel, 2, &Ctx::new()) {
            Err(Error::Invalid(msg)) => assert!(!msg.is_empty()),
            other => panic!("{:?}", other.map(|s| s.files)),
        }
        assert!(m.file("/data/y/a.txt").is_none());
    }
}
