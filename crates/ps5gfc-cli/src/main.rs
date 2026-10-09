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

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use ps5gfc_core::convert::{convert_to, AmprMode, ConvertOptions, Dest};
use ps5gfc_core::source::{open_source, Format, FsKind};
use ps5gfc_core::title::read_title;
use ps5gfc_core::util::human_bytes;
use ps5gfc_core::volume::totals;
use ps5gfc_core::Ctx;

fn usage() -> ! {
    eprintln!(
        "ps5gfc — conversor de formatos de jogos de PS5

USO
  ps5gfc info <caminho>
  ps5gfc convert <origem> <destino> --to <formato> [opcoes]
  ps5gfc convert <origem> <pasta-no-console> --to <formato> --console <ip[:porta]> [opcoes]
  ps5gfc console <ip[:porta]> <comando> [argumentos]

FORMATOS (--to)
  folder            pasta com os arquivos
  exfat             imagem .exfat
  ffpkg | ufs2      imagem .ffpkg (UFS2)
  pkg | fpkg        pacote debug .pkg (FPKG)
  ffpfs             PFS em arvore (.ffpfs)
  ffpfsc            .ffpfsc com exFAT interno (padrao)
  ffpfsc:exfat | ffpfsc:ufs2

OPCOES
  --level N         nivel zlib 1-9 (padrao 7)
  --threads N       threads (padrao: nucleos logicos - 1)
  --cluster-kib N   cluster do exFAT em KiB (padrao 64)
  --rebuild         reconstroi o sistema de arquivos mesmo se ja estiver no formato
  --verify          confere o resultado arquivo por arquivo
  --overwrite       sobrescreve a saida
  --no-times        usa data fixa nos arquivos (saida deterministica)
  --free-mib N      espaco livre extra em imagens exFAT/UFS2 (MiB)
  --ampr MODO       indice ampr_emu.index: auto (padrao) | always | never
  --priority P      prioridade das threads: low_perf (padrao) | normal | low
  --name-mode M     nome da saida: ppsa | ppsa-title (padrao) | ppsa-title-version
  --lang L          idioma das mensagens: pt-BR | en | es | fr | ru (ou variavel PS5GFC_LANG)
  --console HOST    o destino e uma pasta do console PS5: a saida e gerada e enviada ao mesmo tempo, sem
                    arquivo no PC. HOST: 192.168.0.5 (Prospero Manager, porta 7070) ou ftp://192.168.0.5:2121 (FTP)

CONSOLE (Prospero Manager rodando no PS5, ou um servidor FTP: use ftp://ip:porta no lugar de <ip>)
  ps5gfc console <ip> info                       resumo do console e dos discos
  ps5gfc console <ip> ls <caminho>               lista uma pasta
  ps5gfc console <ip> mkdir <caminho>            cria pasta(s)
  ps5gfc console <ip> rm <caminho>               apaga (em segundo plano no console)
  ps5gfc console <ip> mv <de> <para>             move/renomeia
  ps5gfc console <ip> cp <de> <para>             copia
  ps5gfc console <ip> put <arquivo|pasta>... <pasta-no-console> [--replace|--skip|--keep-both]
  ps5gfc console <ip> get <caminho>... <pasta-local> [--replace|--skip|--keep-both]
  ps5gfc console <ip> chmod <modo-octal> <caminho>  altera as permissoes
  ps5gfc console <ip> cat <caminho>              mostra um arquivo de texto"
    );
    std::process::exit(2);
}

fn parse_format(s: &str) -> Option<Format> {
    Some(match s.to_ascii_lowercase().as_str() {
        "folder" | "pasta" => Format::Folder,
        "exfat" => Format::Image(FsKind::Exfat),
        "ffpkg" | "ufs2" => Format::Image(FsKind::Ufs2),
        "pkg" | "fpkg" => Format::Pkg,
        "ffpfs" | "pfs" => Format::Image(FsKind::Pfs),
        "ffpfsc" | "ffpfsc:exfat" => Format::Ffpfsc(FsKind::Exfat),
        "ffpfsc:ufs2" | "ffpfsc:ffpkg" => Format::Ffpfsc(FsKind::Ufs2),
        "ffpfsc:pfs" => Format::Ffpfsc(FsKind::Pfs),
        _ => return None,
    })
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if let Ok(l) = std::env::var("PS5GFC_LANG") {
        ps5gfc_core::i18n::set_lang(&l);
    }
    if args.is_empty() {
        usage();
    }
    let code = match args[0].as_str() {
        "info" if args.len() >= 2 => cmd_info(Path::new(&args[1])),
        "convert" if args.len() >= 3 => cmd_convert(&args[1..]),
        "console" if args.len() >= 3 => cmd_console(&args[1..]),
        "bench" if args.len() >= 2 => cmd_bench(&args[1..]),
        "bench-comp" if args.len() >= 2 => cmd_bench_comp(&args[1..]),
        _ => usage(),
    };
    std::process::exit(code);
}

fn cmd_info(path: &Path) -> i32 {
    let ctx = Ctx::new();
    match open_source(path, &ctx) {
        Ok(s) => {
            let (bytes, files, dirs) = totals(s.volume.as_ref());
            let t = read_title(s.volume.as_ref());
            println!("Formato     : {}", s.format.label());
            println!("Arquivos    : {files} ({dirs} pastas), {}", human_bytes(bytes));
            if let Some(w) = &s.wrapper {
                println!(
                    "Conteiner   : {} -> interno '{}' {} (razao {:.1}%)",
                    human_bytes(w.file_size),
                    w.inner_name,
                    human_bytes(w.inner_size),
                    w.file_size as f64 * 100.0 / w.inner_size.max(1) as f64
                );
            }
            println!("Title ID    : {}", t.title_id.as_deref().unwrap_or("-"));
            println!("Nome        : {}", t.title_name.as_deref().unwrap_or("-"));
            println!("eboot.bin   : {}", if t.has_eboot { "sim" } else { "NAO" });
            if t.ampr {
                println!("AMPR        : sim (fakelib/libSceAmpr.sprx)");
            }
            for (k, v) in s.volume.describe() {
                println!("{k:<12}: {v}");
            }
            0
        }
        Err(e) => {
            eprintln!("error: {}", e.localized());
            1
        }
    }
}

fn cmd_convert(a: &[String]) -> i32 {
    let src = PathBuf::from(&a[0]);
    let dst = PathBuf::from(&a[1]);
    let mut target: Option<Format> = None;
    let mut console: Option<String> = None;
    let mut o = ConvertOptions::default();
    let mut it = a[2..].iter();
    while let Some(arg) = it.next() {
        let mut val = || -> String { it.next().cloned().unwrap_or_else(|| usage()) };
        match arg.as_str() {
            "--to" => target = parse_format(&val()),
            "--console" => console = Some(val()),
            "--level" => o.level = val().parse().unwrap_or_else(|_| usage()),
            "--threads" => o.threads = val().parse().unwrap_or_else(|_| usage()),
            "--cluster-kib" => o.cluster_kib = val().parse().unwrap_or_else(|_| usage()),
            "--rebuild" => o.rebuild = true,
            "--verify" => o.verify = true,
            "--overwrite" => o.overwrite = true,
            "--no-times" => o.preserve_times = false,
            "--free-mib" => o.free_mib = val().parse().unwrap_or_else(|_| usage()),
            "--name-mode" => o.name_mode = Some(ps5gfc_core::title::NameMode::parse(&val()).unwrap_or_else(|| usage())),
            "--lang" => ps5gfc_core::i18n::set_lang(&val()),
            "--priority" => o.priority = ps5gfc_core::sys::WorkerPriority::parse(&val()).unwrap_or_else(|| usage()),
            "--ampr" => {
                o.ampr = match val().as_str() {
                    "auto" => AmprMode::Auto,
                    "always" | "sempre" => AmprMode::Always,
                    "never" | "nunca" => AmprMode::Never,
                    _ => usage(),
                }
            }
            _ => usage(),
        }
    }
    let Some(target) = target else { usage() };

    let ctx = Ctx::new();
    let dest = match &console {
        Some(addr) => match ps5gfc_core::remote::Client::connect_to(addr) {
            Ok(client) => Dest::Console {
                client,
                dir: dst.to_string_lossy().replace('\\', "/"),
            },
            Err(e) => {
                eprintln!("error: {}", e.localized());
                return 1;
            }
        },
        None => Dest::Local(dst.clone()),
    };
    let r = with_progress(&ctx, || convert_to(&src, &dest, target, &o, &ctx));
    match r {
        Ok(rep) => {
            println!(
                "OK  {} -> {}  [{:?}]  entrada {}  saida {}  {:.1}s  ({}/s)",
                rep.source_format.label(),
                rep.target_format.label(),
                rep.route,
                human_bytes(rep.in_bytes),
                human_bytes(rep.out_bytes),
                rep.elapsed_ms as f64 / 1000.0,
                human_bytes((rep.in_bytes as f64 / (rep.elapsed_ms.max(1) as f64 / 1000.0)) as u64)
            );
            println!("    {}", rep.output.display());
            for w in rep.warnings {
                println!("    aviso: {w}");
            }
            0
        }
        Err(e) => {
            eprintln!("error: {}", e.localized());
            1
        }
    }
}

fn cmd_bench(a: &[String]) -> i32 {
    use ps5gfc_core::convert::build_fs_image;
    use ps5gfc_core::emit::write_image;
    use ps5gfc_core::io::ReadAt;
    use ps5gfc_core::par::ordered_map;
    use std::time::Instant;
    let src = PathBuf::from(&a[0]);
    let threads: usize = a
        .get(1)
        .and_then(|s| s.parse().ok())
        .unwrap_or_else(ps5gfc_core::util::default_threads);
    let ctx = Ctx::new();
    let t = Instant::now();
    let s = match open_source(&src, &ctx) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("error: {}", e.localized());
            return 1;
        }
    };
    println!(
        "scan: {:.2}s ({} entradas)",
        t.elapsed().as_secs_f64(),
        s.volume.entries().len()
    );
    let t = Instant::now();
    let o = ConvertOptions::default();
    let (img, _) = match build_fs_image(FsKind::Exfat, s.volume.clone(), &o) {
        Ok(x) => x,
        Err(e) => {
            eprintln!("error: {}", e.localized());
            return 1;
        }
    };
    println!(
        "plano exFAT: {:.2}s  imagem {}",
        t.elapsed().as_secs_f64(),
        human_bytes(img.len())
    );

    const CH: u64 = 2 << 20;
    let total = img.len();
    let n = total.div_ceil(CH);
    let t = Instant::now();
    let r = ordered_map(
        n,
        threads,
        threads * 2 + 2,
        &ctx.cancel,
        |i| {
            let off = i * CH;
            let len = CH.min(total - off) as usize;
            let mut b = vec![0u8; len];
            img.read_exact_at(off, &mut b)?;
            Ok(b.len())
        },
        |_, _| Ok(()),
    );
    let dt = t.elapsed().as_secs_f64();
    println!(
        "leitura pura ({threads} threads): {:.2}s  {}/s  {:?}",
        dt,
        human_bytes((total as f64 / dt) as u64),
        r.is_ok()
    );
    if let Some(out) = a.get(2) {
        let t = Instant::now();
        let r = write_image(img.as_ref(), std::path::Path::new(out), threads, &ctx);
        let dt = t.elapsed().as_secs_f64();
        println!(
            "escrita completa: {:.2}s  {}/s  {:?}",
            dt,
            human_bytes((total as f64 / dt) as u64),
            r.is_ok()
        );
    }
    0
}

fn cmd_bench_comp(a: &[String]) -> i32 {
    use flate2::{Compress, Compression, FlushCompress, Status};
    use std::io::Read;
    let dir = PathBuf::from(&a[0]);
    let max_mib: usize = a.get(1).and_then(|s| s.parse().ok()).unwrap_or(256);

    let mut blocks: Vec<Vec<u8>> = Vec::new();
    let mut total = 0usize;
    let mut stack = vec![dir];
    'outer: while let Some(d) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(&d) else { continue };
        for e in rd.flatten() {
            let p = e.path();
            if p.is_dir() {
                stack.push(p);
                continue;
            }
            let Ok(mut f) = std::fs::File::open(&p) else { continue };
            let mut buf = vec![0u8; 65536];
            let mut taken = 0;
            while taken < 8 {
                let mut n = 0;
                while n < buf.len() {
                    match f.read(&mut buf[n..]) {
                        Ok(0) | Err(_) => break,
                        Ok(k) => n += k,
                    }
                }
                if n < 65536 {
                    break;
                }
                blocks.push(buf.clone());
                total += n;
                taken += 1;
                if total >= max_mib << 20 {
                    break 'outer;
                }
            }
        }
    }
    println!("amostra: {} blocos ({})", blocks.len(), human_bytes(total as u64));
    println!(
        "{:>5} {:>9} {:>11} {:>10}",
        "nivel", "razao", "MiB/s(1 thr)", "bloco(ms)"
    );
    for lvl in 1..=9u32 {
        let mut enc = Compress::new(Compression::new(lvl), true);
        let mut out_total = 0usize;
        let t = std::time::Instant::now();
        for b in &blocks {
            enc.reset();
            let mut out: Vec<u8> = Vec::with_capacity(65535);
            let ok = matches!(
                enc.compress_vec(b, &mut out, FlushCompress::Finish),
                Ok(Status::StreamEnd)
            );
            out_total += if ok && out.len() < 65536 { out.len() } else { 65536 };
        }
        let dt = t.elapsed().as_secs_f64();
        println!(
            "{:>5} {:>8.2}% {:>11.1} {:>10.3}",
            lvl,
            out_total as f64 * 100.0 / total as f64,
            total as f64 / dt / 1048576.0,
            dt * 1000.0 / blocks.len() as f64
        );
    }
    0
}

fn with_progress<T>(ctx: &Ctx, f: impl FnOnce() -> T) -> T {
    let done = Arc::new(AtomicBool::new(false));
    let ticker = {
        let ctx = ctx.clone();
        let done = done.clone();
        std::thread::spawn(move || {
            while !done.load(Ordering::Relaxed) {
                std::thread::sleep(Duration::from_millis(500));
                for l in ctx.progress.drain_logs() {
                    eprintln!("\r\x1b[K[{:>6.1}s] {}", l.t_ms as f64 / 1000.0, l.msg);
                }
                let s = ctx.progress.snapshot();
                if s.total > 0 {
                    eprint!(
                        "\r\x1b[K{:?} {:5.1}%  {} / {}  {}/s  ETA {}  saida {}  razao {}",
                        s.stage,
                        s.done as f64 * 100.0 / s.total as f64,
                        human_bytes(s.done),
                        human_bytes(s.total),
                        human_bytes(s.speed_bps as u64),
                        s.eta_secs.map(|e| format!("{:.0}s", e)).unwrap_or_else(|| "--".into()),
                        human_bytes(s.out_bytes),
                        s.ratio
                            .map(|r| format!("{:.1}%", r * 100.0))
                            .unwrap_or_else(|| "--".into()),
                    );
                }
            }
        })
    };
    let r = f();
    done.store(true, Ordering::Relaxed);
    let _ = ticker.join();
    eprintln!();
    for l in ctx.progress.drain_logs() {
        eprintln!("[{:>6.1}s] {}", l.t_ms as f64 / 1000.0, l.msg);
    }
    r
}

fn cmd_console(a: &[String]) -> i32 {
    use ps5gfc_core::remote::download::download_paths;
    use ps5gfc_core::remote::engine::{conns_for, upload_local};
    use ps5gfc_core::remote::prospero::Conflict;
    use ps5gfc_core::remote::Client;

    let client = match Client::connect_to(&a[0]) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("error: {}", e.localized());
            return 1;
        }
    };
    let mut policy = Conflict::Cancel;
    let rest: Vec<&String> = a[2..]
        .iter()
        .filter(|x| match x.as_str() {
            "--replace" => {
                policy = Conflict::Replace;
                false
            }
            "--skip" => {
                policy = Conflict::Skip;
                false
            }
            "--keep-both" => {
                policy = Conflict::KeepBoth;
                false
            }
            _ => true,
        })
        .collect();
    let need = |n: usize| {
        if rest.len() < n {
            usage();
        }
    };
    let fail = |e: ps5gfc_core::Error| -> i32 {
        eprintln!("error: {}", e.localized());
        1
    };
    let threads = ps5gfc_core::util::default_threads();
    let ctx = Ctx::new();
    match a[1].as_str() {
        "info" => match client.connect() {
            Ok(i) => {
                println!("{} {}  (privilegiado: {})", i.name, i.version, i.privileged);
                if !i.system.platform.is_empty() {
                    println!(
                        "Console     : {} {}  firmware {}  usuario {}",
                        i.system.platform, i.system.model, i.system.firmware, i.system.user_name
                    );
                    println!("Ligado ha   : {}s", i.uptime_seconds);
                }
                println!("Protocolo   : {}", i.protocol);
                for v in i.volumes {
                    println!(
                        "{:<14} {:<12} {:>12} livres de {:>12}",
                        v.label,
                        v.path,
                        human_bytes(v.free),
                        human_bytes(v.total)
                    );
                }
                0
            }
            Err(e) => fail(e),
        },
        "ls" => {
            need(1);
            match client.list(rest[0]) {
                Ok(mut l) => {
                    l.sort_by(|x, y| {
                        y.is_dir
                            .cmp(&x.is_dir)
                            .then(x.name.to_lowercase().cmp(&y.name.to_lowercase()))
                    });
                    for e in l {
                        println!(
                            "{} {:>4o} {:>12} {} {}",
                            if e.is_dir { 'd' } else { '-' },
                            e.mode & 0o7777,
                            if e.is_dir { "-".into() } else { human_bytes(e.size) },
                            e.modified,
                            e.name
                        );
                    }
                    0
                }
                Err(e) => fail(e),
            }
        }
        "mkdir" => {
            need(1);
            client.mkdir_all(rest[0], None).map(|_| 0).unwrap_or_else(fail)
        }
        "rm" => {
            need(1);
            client
                .delete_and_wait(rest[0], &ctx.cancel)
                .map(|_| 0)
                .unwrap_or_else(fail)
        }
        "mv" | "cp" => {
            need(2);
            let id = if a[1] == "mv" {
                client.move_to(rest[0], rest[1], policy)
            } else {
                client.copy_to(rest[0], rest[1], policy)
            };
            match id.and_then(|id| {
                client.wait_job(id, &ctx.cancel, |j| {
                    eprint!(
                        "\r\x1b[K{} {}/{}",
                        j.state,
                        human_bytes(j.completed),
                        human_bytes(j.total)
                    )
                })
            }) {
                Ok(j) if j.failed() => {
                    eprintln!("\nerro no console: {} {}", j.error_code, j.error);
                    1
                }
                Ok(_) => {
                    eprintln!();
                    0
                }
                Err(e) => fail(e),
            }
        }
        "put" => {
            need(2);
            let dest = rest[rest.len() - 1].clone();
            let paths: Vec<PathBuf> = rest[..rest.len() - 1]
                .iter()
                .map(|p| PathBuf::from(p.as_str()))
                .collect();
            match with_progress(&ctx, || {
                upload_local(&client, &paths, &dest, policy, conns_for(threads), &ctx)
            }) {
                Ok(s) => {
                    println!(
                        "OK  {} arquivos, {} pastas, {} ({} pulados)",
                        s.files,
                        s.dirs,
                        human_bytes(s.bytes),
                        s.skipped
                    );
                    0
                }
                Err(e) => fail(e),
            }
        }
        "get" => {
            need(2);
            let dest = PathBuf::from(rest[rest.len() - 1].as_str());
            let paths: Vec<String> = rest[..rest.len() - 1].iter().map(|p| p.to_string()).collect();
            match with_progress(&ctx, || download_paths(&client, &paths, &dest, policy, 3, &ctx)) {
                Ok(s) => {
                    println!(
                        "OK  {} arquivos, {} pastas, {} ({} pulados)",
                        s.files,
                        s.dirs,
                        human_bytes(s.bytes),
                        s.skipped
                    );
                    0
                }
                Err(e) => fail(e),
            }
        }
        "chmod" => {
            need(2);
            match u32::from_str_radix(rest[0], 8) {
                Ok(mode) => client.chmod(rest[1], mode).map(|_| 0).unwrap_or_else(fail),
                Err(_) => usage(),
            }
        }
        "cat" => {
            need(1);
            match client.read_text(rest[0]) {
                Ok(t) => {
                    print!("{}", t.text);
                    0
                }
                Err(e) => fail(e),
            }
        }
        _ => usage(),
    }
}
