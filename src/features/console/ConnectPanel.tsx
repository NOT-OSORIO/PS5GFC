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

import { ExternalLink, Gamepad2, Loader2, Radar } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { Chip } from "@/components/ui/Chip";
import { Input } from "@/components/ui/Input";
import { Segmented } from "@/components/ui/Segmented";
import { api } from "@/lib/api";
import { DEFAULT_PORT, type Protocol } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { cn } from "@/lib/utils";

const PROSPERO_URL = "https://github.com/notmaj0r/ProsperoMgr";
const FTPSRV_URL = "https://github.com/ps5-payload-dev/ftpsrv";

export function parseAddress(text: string, fallbackPort: number): { host: string; port: number } {
  const s = text
    .trim()
    .replace(/^[a-z]+:\/\//i, "")
    .replace(/^[^@/]*@/, "")
    .replace(/[/?#].*$/, "");
  const m = /^(.*):(\d{1,5})$/.exec(s);
  if (m && !m[1].includes(":")) {
    const port = Number(m[2]);
    if (port > 0 && port < 65536) return { host: m[1], port };
  }
  return { host: s, port: fallbackPort };
}

function clampPort(n: number): boolean {
  return Number.isInteger(n) && n > 0 && n < 65536;
}

export function ConnectPanel() {
  const t = useT();
  const cfg = useStore((s) => s.settings.console);
  const patch = useStore((s) => s.patchConsole);
  const status = useConsole((s) => s.status);
  const error = useConsole((s) => s.connError);
  const found = useConsole((s) => s.found);
  const scanning = useConsole((s) => s.scanning);
  const connect = useConsole((s) => s.connect);
  const scan = useConsole((s) => s.scan);
  const [proto, setProto] = useState<Protocol>(cfg.protocol);
  const portOf = (p: Protocol) => (p === "ftp" ? cfg.ftpPort : cfg.port);
  const [host, setHost] = useState(cfg.host);
  const [portText, setPortText] = useState(String(portOf(cfg.protocol)));
  const connecting = status === "connecting";
  const port = Number(portText);
  const portOk = clampPort(port);

  useEffect(() => {
    if (!cfg.host) void scan();

  }, []);

  function pickProtocol(p: Protocol) {
    if (p === proto) return;
    setProto(p);
    setPortText(String(portOf(p)));
  }

  function normalizeHost() {
    const parsed = parseAddress(host, port || DEFAULT_PORT[proto]);
    if (parsed.host !== host) setHost(parsed.host);
    if (parsed.port !== port) setPortText(String(parsed.port));
  }

  function submit() {
    const h = host.trim();
    if (!h || !portOk) return;
    void connect({ protocol: proto, host: h, port, user: "", pass: "" });
  }

  return (
    <div className="grid h-full place-items-center overflow-y-auto px-6 py-8">
      <div className="w-full max-w-[520px]">
        <div className="mb-5 flex items-center gap-3.5">
          <span className="grid h-12 w-12 place-items-center rounded-xl border border-line bg-surface text-ember">
            <Gamepad2 className="h-6 w-6" strokeWidth={1.8} />
          </span>
          <div>
            <h2 className="text-[17px] font-semibold leading-tight">{t("con.connect_title")}</h2>
            <p className="mt-0.5 text-[12.5px] text-muted">{t("con.connect_sub")}</p>
          </div>
        </div>

        <section className="card p-5">
          <label className="eyebrow mb-2 block">{t("con.protocol")}</label>
          <Segmented
            value={proto}
            onChange={pickProtocol}
            disabled={connecting}
            options={[
              { value: "prospero", label: "Prospero Manager", tip: t("con.proto_prospero_tip") },
              { value: "ftp", label: "FTP", tip: t("con.proto_ftp_tip") },
            ]}
          />

          <form
            className="mt-4 flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              submit();
            }}
          >
            <div className="min-w-0 flex-1">
              <label className="eyebrow mb-1.5 block" htmlFor="console-host">
                {t("con.host")}
              </label>
              <Input id="console-host" value={host} onChange={(e) => setHost(e.target.value)} onBlur={normalizeHost} placeholder="192.168.0.10" disabled={connecting} autoFocus className="mono" />
            </div>
            <div className="w-[90px] shrink-0">
              <label className="eyebrow mb-1.5 block" htmlFor="console-port">
                {t("con.port")}
              </label>
              <Input
                id="console-port"
                inputMode="numeric"
                value={portText}
                onChange={(e) => setPortText(e.target.value.replace(/\D/g, "").slice(0, 5))}
                placeholder={String(DEFAULT_PORT[proto])}
                disabled={connecting}
                invalid={portText !== "" && !portOk}
                className="mono"
              />
            </div>
            <Button type="submit" variant="primary" disabled={connecting || !host.trim() || !portOk} icon={connecting ? <Loader2 className="spin h-4 w-4" /> : undefined}>
              {connecting ? t("con.connecting") : t("con.connect")}
            </Button>
          </form>
          <p className="mt-2 text-[11.5px] text-dim">{t(proto === "ftp" ? "con.address_hint_ftp" : "con.address_hint")}</p>

          {error && <p className="mt-3 rounded-lg border border-bad/30 bg-bad/10 px-3 py-2 text-[12px] leading-snug text-bad">{error}</p>}

          <div className="mt-4 flex items-center justify-between border-t border-line pt-4">
            <Checkbox checked={cfg.auto} onChange={(v) => patch({ auto: v })} label={t("con.auto")} />
            <Button variant="ghost" size="sm" onClick={() => void scan()} disabled={scanning || connecting} icon={scanning ? <Loader2 className="spin h-3.5 w-3.5" /> : <Radar className="h-3.5 w-3.5" />}>
              {scanning ? t("con.scanning") : t("con.scan")}
            </Button>
          </div>

          {(found.length > 0 || (!scanning && cfg.host === "" && found.length === 0 && status === "disconnected")) && (
            <div className="mt-3">
              {found.length > 0 ? (
                <ul className="space-y-1.5">
                  {found.map((f) => (
                    <li key={`${f.host}:${f.port}`}>
                      <button
                        type="button"
                        disabled={connecting}
                        onClick={() => {
                          setProto(f.protocol);
                          setHost(f.host);
                          setPortText(String(f.port));
                          void connect({ protocol: f.protocol, host: f.host, port: f.port, user: "", pass: "" });
                        }}
                        className={cn("flex w-full items-center justify-between gap-3 rounded-lg border border-line bg-surface-2 px-3 py-2 text-left hover:border-line-strong hover:bg-surface-3 disabled:opacity-50")}
                      >
                        <span className="flex items-center gap-2">
                          <span className="mono text-[13px] font-semibold">{f.host}</span>
                          <Chip className="!text-[10.5px]">{f.protocol === "ftp" ? `FTP :${f.port}` : "Prospero Manager"}</Chip>
                        </span>
                        <span className="truncate text-[11.5px] text-muted">
                          {f.protocol === "ftp" ? f.version : `${f.name} ${f.version} · ${f.model || "PS5"}`}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-[12px] text-dim">{t("con.scan_none")}</p>
              )}
            </div>
          )}
        </section>

        <div className="mt-4 rounded-xl border border-line bg-surface px-4 py-3 text-[12px] leading-relaxed text-muted">
          <p>{t(proto === "ftp" ? "con.help_ftp" : "con.help")}</p>
          <button type="button" onClick={() => void api.openUrl(proto === "ftp" ? FTPSRV_URL : PROSPERO_URL)} className="mt-1.5 inline-flex items-center gap-1.5 text-ember hover:underline">
            {proto === "ftp" ? "ftpsrv" : "Prospero Manager"} <ExternalLink className="h-3 w-3" />
          </button>
        </div>
      </div>
    </div>
  );
}
