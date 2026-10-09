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

import { Loader2, PlugZap, Unplug, WifiOff } from "lucide-react";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { Switch } from "@/components/ui/Switch";
import { Tip } from "@/components/ui/Tip";
import { DEFAULT_PORT } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { useStore, useT } from "@/lib/store";
import { cn } from "@/lib/utils";

function useTick(ms: number) {
  const [, set] = useState(0);
  useEffect(() => {
    const h = window.setInterval(() => set((n) => n + 1), ms);
    return () => window.clearInterval(h);
  }, [ms]);
}

export function ConnectionBar() {
  const t = useT();
  useTick(1000);
  const status = useConsole((s) => s.status);
  const info = useConsole((s) => s.info);
  const error = useConsole((s) => s.connError);
  const syncedAt = useConsole((s) => s.syncedAt);
  const connect = useConsole((s) => s.connect);
  const disconnect = useConsole((s) => s.disconnect);
  const live = useStore((s) => s.settings.console.live);
  const patch = useStore((s) => s.patchConsole);
  const lost = status === "lost";
  const secs = syncedAt ? Math.max(0, Math.round((Date.now() - syncedAt) / 1000)) : null;

  if (lost || status === "connecting") {
    return (
      <div className="flex items-center gap-3 border-b border-warn/30 bg-warn/10 px-5 py-2.5 text-[12.5px]">
        {status === "connecting" ? <Loader2 className="spin h-4 w-4 text-warn" /> : <WifiOff className="h-4 w-4 text-warn" />}
        <div className="min-w-0 flex-1">
          <span className="font-semibold text-warn">{t("con.lost")}</span>
          <span className="ml-2 truncate text-muted">{error ?? t("con.retrying")}</span>
        </div>
        <Button size="sm" variant="secondary" icon={<PlugZap className="h-3.5 w-3.5" />} disabled={status === "connecting"} onClick={() => void connect(undefined, true)}>
          {t("con.reconnect")}
        </Button>
        <Button size="sm" variant="ghost" onClick={disconnect}>
          {t("con.disconnect")}
        </Button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-3 border-b border-line bg-surface px-5 py-2.5">
      <span className={cn("h-2 w-2 shrink-0 rounded-full bg-ok", live && "pulse-dot")} />
      <span className="mono text-[13px] font-semibold">{info ? `${info.host}${info.port === DEFAULT_PORT[info.protocol] ? "" : `:${info.port}`}` : "—"}</span>
      <div className="flex min-w-0 flex-wrap items-center gap-1.5">
        {info?.system.model && <Chip>{info.system.model}</Chip>}
        {info?.system.firmware && <Chip className="mono">FW {info.system.firmware}</Chip>}
        {info && !info.privileged && (
          <Tip content={t("con.not_privileged_tip")}>
            <span>
              <Chip color="var(--warn)">{t("con.not_privileged")}</Chip>
            </span>
          </Tip>
        )}
        {info && (
          <Tip content={t(info.protocol === "ftp" ? "con.ftp_tip" : "con.prospero_tip")}>
            <span>
              <Chip color="var(--ember)">
                {info.name} {info.version}
              </Chip>
            </span>
          </Tip>
        )}
        {info?.system.user_name && <span className="truncate text-[12px] text-dim">{info.system.user_name}</span>}
      </div>
      <div className="ml-auto flex items-center gap-3.5">
        <Tip content={t("con.live_tip")} side="bottom">
          <label className="flex cursor-pointer items-center gap-2 text-[12px] text-muted">
            <Switch checked={live} onCheckedChange={(v) => patch({ live: v })} />
            <span>{t("con.live")}</span>
            {secs != null && <span className="mono w-[58px] text-[11px] text-dim">{secs < 2 ? t("con.now") : t("con.ago", { n: secs })}</span>}
          </label>
        </Tip>
        <Tip content={t("con.disconnect")} side="bottom">
          <Button size="sm" variant="ghost" iconOnly icon={<Unplug className="h-4 w-4" />} onClick={disconnect} aria-label={t("con.disconnect")} />
        </Tip>
      </div>
    </div>
  );
}
