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
use std::sync::Arc;

use super::client::Client;
use super::engine::{crc_of_remote, upload_local, verify_uploaded, Uploaded};
use super::mock_ftp::MockFtp;
use super::path as rp;
use super::prospero::Conflict;
use super::upload::{upload_direct, DirectOpts, RemoteWriter, SeqSink};
use super::RemoteKind;
use crate::ctl::{Cancel, Ctx};
use crate::io::MemReader;
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

fn no_junk(m: &MockFtp) {
    let junk: Vec<String> = m.paths().into_iter().filter(|p| p.contains(".ps5gfc-part-")).collect();
    assert!(junk.is_empty(), "sobrou parcial no servidor: {junk:?}");
}

fn servers() -> Vec<MockFtp> {
    vec![MockFtp::start(), MockFtp::start_ftpsrv()]
}

fn cl(m: &MockFtp) -> Client {
    Client::Ftp(m.client())
}

fn wait(c: &Client, id: u64) -> crate::remote::prospero::Job {
    c.wait_job(id, &Cancel::new(), |_| {}).unwrap()
}

#[test]
fn connect_and_browse() {
    for m in servers() {
        m.put_file("/data/etaHEN/games/a.txt", b"hello");
        m.put_dir("/data/etaHEN/games/pasta com espaço");
        let c = cl(&m);
        let info = c.connect().unwrap();
        assert_eq!(info.protocol, "ftp");
        assert!(!info.caps.zip && info.caps.chmod && !info.caps.storage && !info.caps.server_copy);
        assert!(info.volumes.is_empty());
        assert!(info.version.contains("mock"), "{}", info.version);
        let mut l = c.list("/data/etaHEN/games").unwrap();
        l.sort_by(|a, b| a.name.cmp(&b.name));
        assert_eq!(l.len(), 2);
        assert_eq!((l[0].name.as_str(), l[0].size, l[0].is_dir), ("a.txt", 5, false));
        assert_eq!((l[1].name.as_str(), l[1].is_dir), ("pasta com espaço", true));
        assert!(c.stat("/data/etaHEN/games/a.txt").unwrap().is_some());
        assert!(c.stat("/data/nada").unwrap().is_none());
        assert!(c.stat("/nao/existe/x").unwrap().is_none());
        assert!(c.stat("/").unwrap().unwrap().is_dir);
    }
}

#[test]
fn listing_works_without_mlsd_mlst_or_epsv() {
    let m = MockFtp::start();
    m.features(false, false, false);
    m.put_file("/data/g/x y.bin", &[1, 2, 3]);
    m.put_dir("/data/g/sub");
    let c = cl(&m);
    c.connect().unwrap();
    let mut l = c.list("/data/g").unwrap();
    l.sort_by(|a, b| a.name.cmp(&b.name));
    assert_eq!(
        l.iter()
            .map(|e| (e.name.as_str(), e.is_dir, e.size))
            .collect::<Vec<_>>(),
        vec![("sub", true, 0), ("x y.bin", false, 3)]
    );
    assert!(c.stat("/data/g/x y.bin").unwrap().is_some());
    assert!(c.stat("/data/g/zzz").unwrap().is_none());

    let cmds = m.commands();
    assert!(cmds.iter().any(|c| c == "PASV"));
    assert!(cmds.iter().filter(|c| *c == "EPSV").count() <= 1, "{cmds:?}");
}

#[test]
fn login_refused_and_unreachable() {
    let m = MockFtp::start();
    m.faults(|f| f.refuse_login = true);
    match cl(&m).connect() {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Denied),
        other => panic!("{:?}", other.map(|i| i.name)),
    }
    let c = Client::connect_to("ftp://127.0.0.1:1").unwrap();
    match c.connect() {
        Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Unreachable),
        other => panic!("{:?}", other.map(|i| i.name)),
    }

    assert_eq!(
        Client::connect_to("192.168.0.9").unwrap().protocol(),
        super::Protocol::Prospero
    );
    assert_eq!(
        Client::connect_to("ftp://192.168.0.9").unwrap().protocol(),
        super::Protocol::Ftp
    );
    assert_eq!(
        Client::connect_to("ftp://192.168.0.9").unwrap().endpoint().port,
        super::ftp::DEFAULT_FTP_PORT
    );
}

#[test]
fn folder_operations_and_sessions_are_reused() {
    for m in servers() {
        let c = cl(&m);
        c.mkdir_all("/data/a/b/c", None).unwrap();
        assert!(m.is_dir("/data/a/b/c"));
        c.mkdir("/data/a/b/c").unwrap();
        assert!(c.mkdir("/nao/existe/pai/x").is_err());
        c.rename("/data/a/b", "/data/a/z").unwrap();
        assert!(m.is_dir("/data/a/z/c") && !m.is_dir("/data/a/b"));
        c.chmod("/data/a/z", 0o700).unwrap();
        assert_eq!(m.mode("/data/a/z"), Some(0o700));
        for _ in 0..10 {
            c.list("/data").unwrap();
        }
        assert!(
            m.sessions() <= 2,
            "abriu {} sessões para operações em série",
            m.sessions()
        );
        m.faults(|f| f.no_chmod = true);
        match c.chmod("/data/a/z", 0o755) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Other),
            other => panic!("{other:?}"),
        }
    }
}

#[test]
fn a_session_closed_by_the_server_is_replaced() {
    for m in servers() {
        let c = cl(&m);
        c.list("/data").unwrap();
        m.disconnect_all();
        std::thread::sleep(std::time::Duration::from_millis(50));
        assert!(c.list("/data").is_ok());
    }
}

#[test]
fn small_files_go_through_a_partial_and_replace_atomically() {
    for m in servers() {
        let c = cl(&m);
        let bytes = data(300_000, 7);
        let cancel = Cancel::new();
        let crc = upload_direct(
            &c,
            "/data/a.bin",
            bytes.len() as u64,
            &MemReader::new(bytes.clone()),
            DirectOpts {
                overwrite: false,
                want_crc: true,
            },
            &cancel,
            &|_| {},
        )
        .unwrap();
        assert_eq!(crc, Some(crc32fast::hash(&bytes)));
        assert_eq!(m.file("/data/a.bin").unwrap(), bytes);
        no_junk(&m);

        match upload_direct(
            &c,
            "/data/a.bin",
            3,
            &MemReader::new(b"xyz".to_vec()),
            DirectOpts::default(),
            &cancel,
            &|_| {},
        ) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
            other => panic!("{other:?}"),
        }
        assert_eq!(m.file("/data/a.bin").unwrap(), bytes);

        for refuses in [false, true] {
            m.faults(|f| f.rnto_refuses_overwrite = refuses);
            let new = data(10, if refuses { 9 } else { 8 });
            upload_direct(
                &c,
                "/data/a.bin",
                10,
                &MemReader::new(new.clone()),
                DirectOpts {
                    overwrite: true,
                    want_crc: false,
                },
                &cancel,
                &|_| {},
            )
            .unwrap();
            assert_eq!(m.file("/data/a.bin").unwrap(), new);
            no_junk(&m);
        }

        upload_direct(
            &c,
            "/data/vazio",
            0,
            &MemReader::new(Vec::new()),
            DirectOpts::default(),
            &cancel,
            &|_| {},
        )
        .unwrap();
        assert_eq!(m.file("/data/vazio").unwrap(), b"");

        assert!(upload_direct(
            &c,
            "/nao/existe/f.bin",
            3,
            &MemReader::new(b"abc".to_vec()),
            DirectOpts::default(),
            &cancel,
            &|_| {}
        )
        .is_err());
        no_junk(&m);
    }
}

#[test]
fn a_dropped_connection_in_a_small_upload_is_retried_from_scratch() {
    for m in servers() {
        let c = cl(&m);
        let bytes = data(1_000_000, 3);
        m.faults(|f| f.cut_next_store_after = Some(400_000));
        let sent = AtomicU64::new(0);
        upload_direct(
            &c,
            "/data/b.bin",
            bytes.len() as u64,
            &MemReader::new(bytes.clone()),
            DirectOpts {
                overwrite: true,
                want_crc: false,
            },
            &Cancel::new(),
            &|n| {
                sent.fetch_add(n, Ordering::Relaxed);
            },
        )
        .unwrap();
        assert_eq!(m.file("/data/b.bin").unwrap(), bytes);
        no_junk(&m);
    }
}

fn send_via_writer(
    c: &Client,
    path: &str,
    bytes: &[u8],
    chunk: usize,
    overwrite: bool,
    on: Arc<dyn Fn(u64) + Send + Sync>,
) -> Result<()> {
    let cancel = Cancel::new();
    let mut w = RemoteWriter::with_chunk(c, path, bytes.len() as u64, overwrite, &cancel, on, chunk)?;
    for piece in bytes.chunks(70_001) {
        w.write_all(piece)?;
    }
    w.finish()
}

#[test]
fn streamed_upload_roundtrip_and_progress() {
    for m in servers() {
        let c = cl(&m);
        let bytes = data(3 * 1024 * 1024 + 1234, 1);
        let total = Arc::new(AtomicU64::new(0));
        let t2 = total.clone();
        send_via_writer(
            &c,
            "/data/img.exfat",
            &bytes,
            1 << 20,
            false,
            Arc::new(move |n| {
                t2.fetch_add(n, Ordering::Relaxed);
            }),
        )
        .unwrap();
        assert_eq!(m.file("/data/img.exfat").unwrap(), bytes);
        assert_eq!(total.load(Ordering::Relaxed), bytes.len() as u64);
        no_junk(&m);

        assert_eq!(m.commands().iter().filter(|c| c.starts_with("STOR ")).count(), 1);

        send_via_writer(&c, "/data/one.bin", b"abc", 1 << 20, false, Arc::new(|_| {})).unwrap();
        assert_eq!(m.file("/data/one.bin").unwrap(), b"abc");
        send_via_writer(&c, "/data/empty.bin", b"", 1 << 20, false, Arc::new(|_| {})).unwrap();
        assert_eq!(m.file("/data/empty.bin").unwrap(), b"");

        match send_via_writer(
            &c,
            "/data/img.exfat",
            &bytes[..2_000_000],
            1 << 20,
            false,
            Arc::new(|_| {}),
        ) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
            other => panic!("{other:?}"),
        }
        assert_eq!(m.file("/data/img.exfat").unwrap(), bytes);
        no_junk(&m);
    }
}

#[test]
fn streamed_upload_resumes_after_a_drop_even_if_the_server_lost_the_tail() {
    for (lose, m) in [0usize, 150_000]
        .into_iter()
        .flat_map(|l| servers().into_iter().map(move |m| (l, m)))
    {
        let c = cl(&m);
        let bytes = data(3 * 1024 * 1024 + 99, 2);
        m.faults(|f| {
            f.cut_next_store_after = Some(1_700_000);
            f.lose_tail_on_cut = lose;
        });
        let total = Arc::new(AtomicU64::new(0));
        let t2 = total.clone();
        send_via_writer(
            &c,
            "/data/img.ffpkg",
            &bytes,
            512 * 1024,
            false,
            Arc::new(move |n| {
                t2.fetch_add(n, Ordering::Relaxed);
            }),
        )
        .unwrap();
        assert_eq!(m.file("/data/img.ffpkg").unwrap().len(), bytes.len(), "lose={lose}");
        assert!(
            m.file("/data/img.ffpkg").unwrap() == bytes,
            "conteúdo diferente depois da retomada (lose={lose})"
        );
        assert_eq!(
            total.load(Ordering::Relaxed),
            bytes.len() as u64,
            "o progresso contou bytes a mais ou a menos (lose={lose})"
        );
        assert!(
            m.commands().iter().any(|c| c.starts_with("APPE ")),
            "devia ter retomado com APPE (lose={lose})"
        );
        no_junk(&m);
    }
}

#[test]
fn abandoned_or_cancelled_stream_cleans_the_partial() {
    for m in servers() {
        let c = cl(&m);
        let bytes = data(3 * 1024 * 1024, 4);

        {
            let cancel = Cancel::new();
            let mut w = RemoteWriter::with_chunk(
                &c,
                "/data/x.bin",
                bytes.len() as u64,
                false,
                &cancel,
                Arc::new(|_| {}),
                512 * 1024,
            )
            .unwrap();
            w.write_all(&bytes[..1_500_000]).unwrap();

            std::thread::sleep(std::time::Duration::from_millis(300));
        }
        assert!(m.file("/data/x.bin").is_none());
        no_junk(&m);

        let cancel = Cancel::new();
        let mut w = RemoteWriter::with_chunk(
            &c,
            "/data/y.bin",
            bytes.len() as u64,
            false,
            &cancel,
            Arc::new(|_| {}),
            512 * 1024,
        )
        .unwrap();
        w.write_all(&bytes[..1_200_000]).unwrap();
        cancel.cancel();
        let r = w.write_all(&bytes[1_200_000..]).and_then(|()| w.finish());
        assert!(matches!(r, Err(Error::Cancelled)), "{r:?}");
        assert!(m.file("/data/y.bin").is_none());
        no_junk(&m);
    }
}

#[test]
fn server_out_of_space_is_reported_as_no_space() {
    for m in servers() {
        let c = cl(&m);
        m.faults(|f| f.full_after = Some(100_000));
        let bytes = data(1_000_000, 5);
        match upload_direct(
            &c,
            "/data/c.bin",
            bytes.len() as u64,
            &MemReader::new(bytes),
            DirectOpts::default(),
            &Cancel::new(),
            &|_| {},
        ) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::NoSpace, "{}", e.message),
            other => panic!("{other:?}"),
        }
        no_junk(&m);
    }
}

#[test]
fn download_roundtrip_and_truncation() {
    for m in servers() {
        let c = cl(&m);
        let bytes = data(2_000_000, 6);
        m.put_file("/data/d.bin", &bytes);
        let (crc, len) = crc_of_remote(&c, "/data/d.bin", &Ctx::new()).unwrap();
        assert_eq!((crc, len), (crc32fast::hash(&bytes), bytes.len() as u64));

        m.faults(|f| f.cut_next_retr_after = Some(500_000));
        assert!(crc_of_remote(&c, "/data/d.bin", &Ctx::new()).is_err());
        assert_eq!(
            crc_of_remote(&c, "/data/d.bin", &Ctx::new()).unwrap().1,
            bytes.len() as u64
        );
        match crc_of_remote(&c, "/data/nao-existe.bin", &Ctx::new()) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::NotFound),
            other => panic!("{other:?}"),
        }

        let up = vec![Uploaded {
            path: "/data/d.bin".into(),
            size: bytes.len() as u64,
            crc: Some(crc32fast::hash(&bytes)),
        }];
        verify_uploaded(&c, &up, 2, &Ctx::new()).unwrap();
        let bad = vec![Uploaded {
            path: "/data/d.bin".into(),
            size: bytes.len() as u64,
            crc: Some(1),
        }];
        assert!(verify_uploaded(&c, &bad, 2, &Ctx::new()).is_err());
    }
}

#[test]
fn background_jobs_delete_copy_and_move() {
    for m in servers() {
        let c = cl(&m);
        m.put_file("/data/src/a.bin", &data(1000, 1));
        m.put_file("/data/src/sub/b.bin", &data(2000, 2));
        m.put_dir("/data/src/vazia");

        let j = wait(&c, c.copy_to("/data/src", "/data/copia", Conflict::Cancel).unwrap());
        assert!(j.finished() && !j.failed(), "{}", j.error);
        assert_eq!(m.file("/data/copia/sub/b.bin").unwrap(), data(2000, 2));
        assert!(m.is_dir("/data/copia/vazia"));
        assert_eq!(j.total, 3000);
        assert_eq!(j.completed, 3000);

        let j = wait(&c, c.copy_to("/data/src", "/data/copia", Conflict::Cancel).unwrap());
        assert!(j.failed() && j.error_code == "exists", "{}/{}", j.state, j.error_code);
        let j = wait(&c, c.copy_to("/data/src", "/data/copia", Conflict::Skip).unwrap());
        assert!(j.state == "done");
        let j = wait(&c, c.copy_to("/data/src", "/data/copia", Conflict::KeepBoth).unwrap());
        assert!(j.state == "done" && m.is_dir("/data/copia (1)/sub"), "{}", j.error);

        m.put_file("/data/dest.bin", b"velho");
        let j = wait(
            &c,
            c.copy_to("/data/src/a.bin", "/data/dest.bin", Conflict::Replace)
                .unwrap(),
        );
        assert!(j.state == "done");
        assert_eq!(m.file("/data/dest.bin").unwrap(), data(1000, 1));

        assert!(wait(
            &c,
            c.copy_to("/data/src", "/data/src/dentro", Conflict::Cancel).unwrap()
        )
        .failed());
        no_junk(&m);

        let before = m.commands().iter().filter(|c| c.starts_with("RETR ")).count();
        let j = wait(&c, c.move_to("/data/copia", "/data/movida", Conflict::Cancel).unwrap());
        assert!(j.state == "done", "{}", j.error);
        assert!(m.is_dir("/data/movida/sub") && !m.is_dir("/data/copia"));
        assert_eq!(m.commands().iter().filter(|c| c.starts_with("RETR ")).count(), before);

        m.put_file("/data/alvo/velho.txt", b"v");
        let j = wait(&c, c.move_to("/data/src", "/data/alvo", Conflict::Replace).unwrap());
        assert!(j.state == "done", "{}", j.error);
        assert!(
            m.file("/data/alvo/velho.txt").is_some() && m.file("/data/alvo/a.bin").is_some() && !m.is_dir("/data/src")
        );

        let j = wait(&c, c.delete("/data/alvo").unwrap());
        assert!(j.state == "done" && j.completed >= 4, "{}", j.error);
        assert!(!m.is_dir("/data/alvo"));

        assert!(wait(&c, c.delete("/data/nada").unwrap()).state == "done");
        assert!(c.delete("/").is_err());

        assert!(!c.jobs().unwrap().is_empty());
        c.clear_jobs().unwrap();
        assert!(c.jobs().unwrap().is_empty());
    }
}

#[test]
fn a_background_job_can_be_cancelled() {
    for m in servers() {
        let c = cl(&m);
        for i in 0..60 {
            m.put_file(&format!("/data/muitos/f{i}.bin"), &data(50_000, i as u8));
        }
        let id = c.copy_to("/data/muitos", "/data/c2", Conflict::Cancel).unwrap();
        c.cancel_job(id).unwrap();
        let j = wait(&c, id);
        assert!(j.state == "canceled" || j.state == "done", "{}", j.state);
        no_junk(&m);
    }
}

#[test]
fn conflicts_unique_names_and_dir_size() {
    for m in servers() {
        let c = cl(&m);
        m.put_file("/data/a.bin", b"1");
        m.put_file("/data/a (1).bin", b"1");
        m.put_file("/data/pasta/x.bin", &data(100, 1));
        m.put_file("/data/pasta/sub/y.bin", &data(23, 1));
        assert_eq!(
            c.conflicts(&[
                "/data/a.bin".into(),
                "/data/b.bin".into(),
                "/nao/existe/z".into(),
                "/data/pasta".into()
            ])
            .unwrap(),
            vec!["/data/a.bin".to_string(), "/data/pasta".to_string()]
        );
        assert_eq!(
            c.unique_names(&["/data/a.bin".into(), "/data/b.bin".into(), "/data/b.bin".into()])
                .unwrap(),
            vec![
                "/data/a (2).bin".to_string(),
                "/data/b.bin".to_string(),
                "/data/b (1).bin".to_string()
            ]
        );

        m.put_dir("/data/v1.2/arq");
        assert_eq!(
            c.unique_names(&["/data/v1.2/arq".into()]).unwrap(),
            vec!["/data/v1.2/arq (1)".to_string()]
        );
        assert_eq!(c.dir_size("/data/pasta").unwrap(), 123);

        let n = m
            .commands()
            .iter()
            .filter(|c| c.starts_with("MLSD") || c.starts_with("LIST") || c.starts_with("CWD"))
            .count();
        let many: Vec<String> = (0..50).map(|i| format!("/data/novo/f{i}")).collect();
        assert!(c.conflicts(&many).unwrap().is_empty());
        assert_eq!(
            m.commands()
                .iter()
                .filter(|c| c.starts_with("MLSD") || c.starts_with("LIST") || c.starts_with("CWD"))
                .count(),
            n + 1
        );
    }
}

#[test]
fn text_files_roundtrip_with_version_check() {
    for m in servers() {
        let c = cl(&m);
        m.put_file(
            "/data/t.txt",
            b"\xEF\xBB\xBFlinha 1\r\nlinha 2\r\nacentua\xC3\xA7\xC3\xA3o\r\n",
        );
        let t = c.read_text("/data/t.txt").unwrap();
        assert_eq!((t.newline.as_str(), t.bom), ("crlf", true));
        assert_eq!(t.text, "linha 1\nlinha 2\nacentuação\n");
        let v2 = c
            .write_text("/data/t.txt", "novo\ntexto\n", &t.version, &t.newline, t.bom)
            .unwrap();
        assert_eq!(m.file("/data/t.txt").unwrap(), b"\xEF\xBB\xBFnovo\r\ntexto\r\n");
        no_junk(&m);

        assert!(c.write_text("/data/t.txt", "x", &t.version, "lf", false).is_err());
        let t2 = c.read_text("/data/t.txt").unwrap();
        assert_eq!(t2.version, v2);
        c.write_text("/data/t.txt", "ok", &t2.version, "lf", false).unwrap();
        assert_eq!(m.file("/data/t.txt").unwrap(), b"ok");

        c.write_text("/data/novo.txt", "a\nb", "", "lf", false).unwrap();
        assert_eq!(m.file("/data/novo.txt").unwrap(), b"a\nb");

        m.put_file("/data/bin.dat", &[0, 1, 2, 3]);
        assert!(c.read_text("/data/bin.dat").is_err());
        m.put_file("/data/lat1.txt", &[0xE9, 0x20]);
        assert!(c.read_text("/data/lat1.txt").is_err());
    }
}

#[test]
fn zip_is_not_available_over_ftp() {
    let m = MockFtp::start();
    let c = cl(&m);
    assert!(c.zip(&["/data".into()], "/data", "x", "smart").is_err());
    assert!(c.unzip("/data/x.zip", None, Conflict::Cancel, false, None).is_err());
}

#[test]
fn upload_local_policies_over_ftp() {
    for m in servers() {
        let c = cl(&m);
        let src = tempfile::tempdir().unwrap();
        let make = |tag: u8| {
            std::fs::create_dir_all(src.path().join("pasta/sub")).unwrap();
            std::fs::write(src.path().join("a.txt"), data(1000, tag)).unwrap();
            std::fs::write(src.path().join("pasta/b.bin"), data(70_000, tag)).unwrap();
            std::fs::write(src.path().join("pasta/sub/c.bin"), data(5, tag)).unwrap();
            vec![src.path().join("a.txt"), src.path().join("pasta")]
        };
        let items = make(1);
        let st = upload_local(&c, &items, "/data/dst", Conflict::Cancel, 3, &Ctx::new()).unwrap();
        assert_eq!((st.files, st.dirs, st.skipped), (3, 2, 0));
        assert_eq!(m.file("/data/dst/pasta/sub/c.bin").unwrap(), data(5, 1));
        let items2 = make(2);
        match upload_local(&c, &items2, "/data/dst", Conflict::Cancel, 3, &Ctx::new()) {
            Err(Error::Remote(e)) => assert_eq!(e.kind, RemoteKind::Exists),
            other => panic!("{:?}", other.map(|s| s.files)),
        }
        let st = upload_local(&c, &items2, "/data/dst", Conflict::Skip, 3, &Ctx::new()).unwrap();
        assert_eq!((st.files, st.skipped), (0, 3));
        let st = upload_local(&c, &items2, "/data/dst", Conflict::Replace, 3, &Ctx::new()).unwrap();
        assert_eq!(st.files, 3);
        assert_eq!(m.file("/data/dst/a.txt").unwrap(), data(1000, 2));
        let items3 = make(3);
        let st = upload_local(&c, &items3, "/data/dst", Conflict::KeepBoth, 3, &Ctx::new()).unwrap();
        assert_eq!(st.files, 3);
        assert_eq!(m.file("/data/dst/a (1).txt").unwrap(), data(1000, 3));
        assert_eq!(m.file("/data/dst/pasta (1)/sub/c.bin").unwrap(), data(5, 3));
        assert_eq!(m.file("/data/dst/a.txt").unwrap(), data(1000, 2));
        no_junk(&m);
    }
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

    fn console(m: &MockFtp, dir: &str) -> Dest {
        Dest::Console {
            client: cl(m),
            dir: dir.into(),
        }
    }

    #[test]
    fn image_to_console_folder_over_ftp() {
        for m in servers() {
            let src = tempfile::tempdir().unwrap();
            make_game(src.path());
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
    }

    fn remote_equals_local(target: Format) {
        for m in servers() {
            remote_equals_local_on(target, m);
        }
    }

    fn remote_equals_local_on(target: Format, m: MockFtp) {
        let src = tempfile::tempdir().unwrap();
        make_game(src.path());
        let out_local = tempfile::tempdir().unwrap();
        let local = convert(src.path(), out_local.path(), target, &opts(), &Ctx::new()).unwrap();
        let local_bytes = std::fs::read(&local.output).unwrap();

        let rep = convert_to(
            src.path(),
            &console(&m, "/data/etaHEN/games"),
            target,
            &opts(),
            &Ctx::new(),
        )
        .unwrap();
        assert!(rep.console);
        let remote_path = rep.output.to_string_lossy().into_owned();
        assert!(
            remote_path.starts_with("/data/etaHEN/games/PPSA99999."),
            "{remote_path}"
        );
        let remote_bytes = m.file(&remote_path).expect("arquivo no servidor");
        assert_eq!(remote_bytes.len(), local_bytes.len());
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
        assert_eq!(rp::parent(&remote_path), "/data/etaHEN/games");
    }

    #[test]
    fn folder_to_exfat_over_ftp_matches_local() {
        remote_equals_local(Format::Image(FsKind::Exfat));
    }

    #[test]
    fn folder_to_ffpfsc_over_ftp_is_a_valid_container() {
        remote_equals_local(Format::Ffpfsc(FsKind::Exfat));
    }

    #[test]
    fn cancelling_a_conversion_over_ftp_leaves_nothing_behind() {
        for m in servers() {
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
                    let started = m_ref
                        .paths()
                        .iter()
                        .any(|p| p.contains(".ps5gfc-part-") && m_ref.file(p).map(|d| !d.is_empty()).unwrap_or(false));
                    if started {
                        break;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(1));
                }
                cancel.cancel();
                h.join().unwrap()
            });
            assert!(matches!(res, Err(Error::Cancelled)), "{:?}", res.map(|r| r.out_bytes));
            no_junk(&m);
            assert!(m.file("/data/g/PPSA99999.exfat").is_none());
        }
    }
}

#[test]
fn ftpsrv_listing_uses_list_with_a_path_and_ignores_dot_entries() {
    let m = MockFtp::start_ftpsrv();
    m.put_file("/data/g/um.bin", b"1234");
    m.put_file("/data/outra/dois.bin", b"12");
    m.put_dir("/data/g/sub");
    let c = cl(&m);
    let info = c.connect().unwrap();
    assert!(info.version.starts_with("v0.0-mock"), "{}", info.version);

    let mut l = c.list("/data/g").unwrap();
    l.sort_by(|a, b| a.name.cmp(&b.name));
    assert_eq!(
        l.iter().map(|e| (e.name.as_str(), e.is_dir)).collect::<Vec<_>>(),
        vec![("sub", true), ("um.bin", false)]
    );
    assert_eq!(l[0].mode, 0o755);
    assert!(l[1].mode != 0 && l[1].size == 4);
    let cmds = m.commands();
    assert!(!cmds.iter().any(|c| c.starts_with("MLSD")), "{cmds:?}");
    assert!(cmds.iter().any(|c| c == "LIST /data/g"));

    assert_eq!(cmds.iter().filter(|c| *c == "EPSV").count(), 1, "{cmds:?}");
    assert!(cmds.iter().any(|c| c == "PASV"));
}

#[test]
fn ftpsrv_self_files_keep_their_exact_bytes_and_sizes() {
    let m = MockFtp::start_ftpsrv();
    let c = cl(&m);

    let mut selfy = super::mock_ftp::SELF_MAGIC.to_vec();
    selfy.extend(data(500_000, 9));
    m.put_file("/data/g/eboot.bin", &selfy);
    c.connect().unwrap();
    assert!(
        m.commands().iter().any(|x| x == "SELF"),
        "devia desligar a conversão SELF→ELF"
    );
    let l = c.list("/data/g").unwrap();
    assert_eq!(l[0].size, selfy.len() as u64);

    let cancel = Cancel::new();
    let crc = upload_direct(
        &c,
        "/data/g/novo.self",
        selfy.len() as u64,
        &MemReader::new(selfy.clone()),
        DirectOpts {
            overwrite: false,
            want_crc: true,
        },
        &cancel,
        &|_| {},
    )
    .unwrap();
    assert_eq!(m.file("/data/g/novo.self").unwrap(), selfy);
    let big = {
        let mut v = super::mock_ftp::SELF_MAGIC.to_vec();
        v.extend(data(2_500_000, 3));
        v
    };
    send_via_writer(&c, "/data/g/grande.sprx", &big, 1 << 20, false, Arc::new(|_| {})).unwrap();
    assert_eq!(m.file("/data/g/grande.sprx").unwrap(), big);
    let up = vec![
        Uploaded {
            path: "/data/g/novo.self".into(),
            size: selfy.len() as u64,
            crc,
        },
        Uploaded {
            path: "/data/g/grande.sprx".into(),
            size: big.len() as u64,
            crc: Some(crc32fast::hash(&big)),
        },
    ];
    verify_uploaded(&c, &up, 2, &Ctx::new()).unwrap();

    let (got, len) = crc_of_remote(&c, "/data/g/eboot.bin", &Ctx::new()).unwrap();
    assert_eq!((got, len), (crc32fast::hash(&selfy), selfy.len() as u64));
    no_junk(&m);
}

#[test]
fn ftpsrv_changes_reply_226_and_chmod_goes_through_site() {
    let m = MockFtp::start_ftpsrv();
    let c = cl(&m);
    c.mkdir("/data/a").unwrap();
    c.mkdir("/data/a").unwrap();
    m.put_file("/data/a/f.txt", b"x");
    c.rename("/data/a/f.txt", "/data/a/g.txt").unwrap();
    c.chmod("/data/a/g.txt", 0o600).unwrap();
    assert_eq!(m.mode("/data/a/g.txt"), Some(0o600));
    let j = wait(&c, c.delete("/data/a").unwrap());
    assert!(j.state == "done", "{}", j.error);
    assert!(!m.is_dir("/data/a"));

    assert!(!m.commands().iter().any(|x| x.starts_with("PASS")));
}

#[test]
fn chmod_tree_walks_every_file_and_subfolder() {
    for m in servers() {
        let c = cl(&m);
        c.mkdir_all("/data/game/sce_sys", None).unwrap();
        c.mkdir_all("/data/game/data/deep", None).unwrap();
        m.put_file("/data/game/eboot.bin", b"x");
        m.put_file("/data/game/sce_sys/param.json", b"{}");
        m.put_file("/data/game/data/deep/blob.bin", b"y");

        let touched = c.chmod_tree("/data/game", 0o700).unwrap();
        assert_eq!(touched, 6, "eboot.bin, sce_sys, sce_sys/param.json, data, data/deep, data/deep/blob.bin");

        for p in [
            "/data/game/eboot.bin",
            "/data/game/sce_sys",
            "/data/game/sce_sys/param.json",
            "/data/game/data",
            "/data/game/data/deep",
            "/data/game/data/deep/blob.bin",
        ] {
            assert_eq!(m.mode(p), Some(0o700), "{p} nao recebeu o novo modo");
        }
        // The top-level folder itself is left untouched by chmod_tree; the caller chmods it separately.
        assert_ne!(m.mode("/data/game"), Some(0o700));
    }
}
