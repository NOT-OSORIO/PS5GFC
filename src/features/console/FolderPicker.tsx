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

import { ArrowUp, ChevronRight, Folder, FolderPlus, HardDrive, Loader2, Usb } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/Button";
import { Modal } from "@/components/ui/Dialog";
import { Input } from "@/components/ui/Input";
import { api } from "@/lib/api";
import { asApiError, px, sortEntries, type RemoteEntry, type StorageVolume } from "@/lib/console";
import { useConsole } from "@/lib/consoleStore";
import { useT } from "@/lib/store";
import { cn } from "@/lib/utils";

const NO_VOLUMES: StorageVolume[] = [];

export function FolderPickerModal({
  open,
  title,
  start,
  confirm,
  onPick,
}: {
  open: boolean;
  title: string;
  start: string;
  confirm: string;

  onPick: (path: string | null) => void;
}) {
  const t = useT();
  const volumes = useConsole((s) => s.info?.volumes ?? NO_VOLUMES);
  const [path, setPath] = useState(px.norm(start));
  const [dirs, setDirs] = useState<RemoteEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState<string | null>(null);
  const seq = useRef(0);

  const load = useCallback(async (p: string) => {
    const mine = ++seq.current;
    setLoading(true);
    setError(null);
    try {
      const list = await api.consoleList(p);
      if (mine !== seq.current) return;
      setDirs(sortEntries(list.filter((e) => e.is_dir), { key: "name", dir: 1 }));
      setPath(p);
    } catch (e) {
      if (mine !== seq.current) return;
      const err = asApiError(e);

      if (err.kind === "not_found" && p !== "/") void load(px.parent(p));
      else setError(err.message);
    } finally {
      if (mine === seq.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (open) {
      setCreating(null);
      void load(px.norm(start));
    }

  }, [open]);

  async function create() {
    const name = (creating ?? "").trim();
    if (!px.validName(name)) return;
    try {
      await api.consoleMkdir(px.join(path, name));
      setCreating(null);
      await load(px.join(path, name));
    } catch (e) {
      setError(asApiError(e).message);
    }
  }

  const parts = px.parts(path);

  return (
    <Modal
      open={open}
      onClose={() => onPick(null)}
      title={title}
      width={520}
      footer={
        <>
          <span className="mono mr-auto min-w-0 truncate text-[11.5px] text-muted">{path}</span>
          <Button variant="ghost" onClick={() => onPick(null)}>
            {t("read.cancel")}
          </Button>
          <Button variant="primary" onClick={() => onPick(path)}>
            {confirm}
          </Button>
        </>
      }
    >
      <div className="-mt-1 mb-3 flex flex-wrap gap-1.5">
        {volumes.map((v) => (
          <button
            key={v.path}
            type="button"
            onClick={() => void load(v.path)}
            className={cn("flex items-center gap-1.5 rounded-lg border border-line px-2.5 py-1 text-[11.5px] hover:border-line-strong hover:bg-surface-3", px.isWithin(path, v.path) && "bg-surface-3")}
          >
            {v.kind === "usb" ? <Usb className="h-3.5 w-3.5" /> : <HardDrive className="h-3.5 w-3.5" />}
            <span className="mono">{v.path}</span>
          </button>
        ))}
      </div>

      <div className="mb-2 flex items-center gap-1.5">
        <Button size="sm" variant="ghost" iconOnly icon={<ArrowUp className="h-4 w-4" />} disabled={path === "/"} onClick={() => void load(px.parent(path))} aria-label={t("con.up")} />
        <div className="flex min-w-0 flex-1 items-center overflow-hidden whitespace-nowrap rounded-lg border border-line bg-surface px-1.5">
          <button type="button" onClick={() => void load("/")} className="mono rounded-md px-1.5 py-1 text-[12px] text-muted hover:bg-surface-3">
            /
          </button>
          {parts.map((p, i) => (
            <span key={i} className="flex items-center">
              {i > 0 && <ChevronRight className="h-3 w-3 text-dim" />}
              <button type="button" onClick={() => void load("/" + parts.slice(0, i + 1).join("/"))} className="mono rounded-md px-1.5 py-1 text-[12px] text-muted hover:bg-surface-3 hover:text-fg">
                {p}
              </button>
            </span>
          ))}
        </div>
        <Button size="sm" variant="secondary" icon={<FolderPlus className="h-3.5 w-3.5" />} onClick={() => setCreating(t("con.new_folder_name"))}>
          {t("con.create")}
        </Button>
      </div>

      <div className="h-[260px] overflow-y-auto rounded-lg border border-line bg-surface">
        {creating !== null && (
          <div className="flex items-center gap-2 border-b border-line p-2">
            <Folder className="h-4 w-4 shrink-0 text-ember" />
            <Input
              autoFocus
              value={creating}
              onChange={(e) => setCreating(e.target.value)}
              onFocus={(e) => e.currentTarget.select()}
              onKeyDown={(e) => {
                if (e.key === "Enter") void create();
                if (e.key === "Escape") setCreating(null);
              }}
              invalid={creating.trim() !== "" && !px.validName(creating.trim())}
              className="h-8"
              aria-label={t("con.name")}
            />
            <Button size="sm" variant="primary" disabled={!px.validName(creating.trim())} onClick={() => void create()}>
              {t("con.create")}
            </Button>
          </div>
        )}
        {loading && (
          <div className="grid h-full place-items-center text-dim">
            <Loader2 className="spin h-5 w-5" />
          </div>
        )}
        {!loading && error && <p className="p-4 text-[12.5px] leading-snug text-bad">{error}</p>}
        {!loading && !error && dirs.length === 0 && <p className="p-4 text-center text-[12.5px] text-dim">{t("con.no_subfolders")}</p>}
        {!loading &&
          !error &&
          dirs.map((d) => (
            <button
              key={d.name}
              type="button"
              onDoubleClick={() => void load(px.join(path, d.name))}
              onClick={() => void load(px.join(path, d.name))}
              className="flex w-full items-center gap-2.5 px-3 py-2 text-left text-[12.5px] hover:bg-surface-2"
            >
              <Folder className="h-4 w-4 shrink-0" style={{ color: "var(--fmt-folder)" }} strokeWidth={1.9} />
              <span className="truncate">{d.name}</span>
            </button>
          ))}
      </div>
    </Modal>
  );
}
