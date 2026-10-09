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

import { useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { Modal } from "@/components/ui/Dialog";
import { Input } from "@/components/ui/Input";
import { Segmented } from "@/components/ui/Segmented";
import { api } from "@/lib/api";
import { asApiError, formatModified, isSystemPath, modeString, octal, px, type Policy } from "@/lib/console";
import { useConsole, type Dialog } from "@/lib/consoleStore";
import { useT } from "@/lib/store";
import { bytes, cn, num } from "@/lib/utils";
import { FolderPickerModal } from "./FolderPicker";

type Of<K extends Dialog["kind"]> = Extract<Dialog, { kind: K }>;

function NameList({ names, max = 6 }: { names: string[]; max?: number }) {
  const t = useT();
  return (
    <ul className="space-y-1">
      {names.slice(0, max).map((n) => (
        <li key={n} className="mono truncate rounded-md bg-surface px-2.5 py-1 text-[12px]">
          {n}
        </li>
      ))}
      {names.length > max && <li className="px-1 text-[11.5px] text-dim">{t("con.and_more", { n: names.length - max })}</li>}
    </ul>
  );
}

function ConflictDialog({ d }: { d: Of<"conflict"> }) {
  const t = useT();
  const done = (p: Policy | null) => d.resolve(p);
  return (
    <Modal
      open
      onClose={() => done(null)}
      title={t("con.cf_title", { n: d.names.length })}
      description={t(d.what === "download" ? "con.cf_desc_pc" : "con.cf_desc")}
      width={460}
      footer={
        <>
          <Button variant="ghost" onClick={() => done(null)}>
            {t("read.cancel")}
          </Button>
          <Button variant="secondary" onClick={() => done("skip")}>
            {t("con.cf_skip")}
          </Button>
          <Button variant="secondary" onClick={() => done("keep_both")}>
            {t("con.cf_keep")}
          </Button>
          <Button variant="primary" onClick={() => done("replace")}>
            {t("con.cf_replace")}
          </Button>
        </>
      }
    >
      <NameList names={d.names} />
    </Modal>
  );
}

function DeleteDialog({ d }: { d: Of<"confirm_delete"> }) {
  const t = useT();
  const system = d.paths.some(isSystemPath);
  return (
    <Modal
      open
      onClose={() => d.resolve(false)}
      title={t("con.del_title", { n: d.paths.length })}
      description={d.dirs > 0 ? t("con.del_desc_dirs") : t("con.del_desc")}
      width={440}
      footer={
        <>
          <Button variant="ghost" onClick={() => d.resolve(false)}>
            {t("read.cancel")}
          </Button>
          <Button variant="danger" data-autofocus onClick={() => d.resolve(true)}>
            {t("con.delete")}
          </Button>
        </>
      }
    >
      <NameList names={d.paths.map(px.base)} />
      {system && <p className="mt-3 rounded-lg border border-bad/30 bg-bad/10 px-3 py-2 text-[12px] leading-snug text-bad">{t("con.del_system")}</p>}
    </Modal>
  );
}

function NameDialog({ d }: { d: Of<"name"> }) {
  const t = useT();
  const [value, setValue] = useState(d.initial);
  const ref = useRef<HTMLInputElement>(null);
  const name = value.trim();
  const problem = name === "" ? null : !px.validName(name) ? t("con.name_invalid") : d.taken.includes(name) ? t("con.name_taken") : null;
  const ok = name !== "" && !problem;

  function selectName(el: HTMLInputElement) {
    const dot = d.select === "stem" ? d.initial.lastIndexOf(".") : -1;
    el.setSelectionRange(0, dot > 0 ? dot : d.initial.length);
  }

  return (
    <Modal
      open
      onClose={() => d.resolve(null)}
      title={d.title}
      width={420}
      footer={
        <>
          <Button variant="ghost" onClick={() => d.resolve(null)}>
            {t("read.cancel")}
          </Button>
          <Button variant="primary" disabled={!ok} onClick={() => d.resolve(name)}>
            {d.confirm}
          </Button>
        </>
      }
    >
      <label className="eyebrow mb-2 block">{d.label}</label>
      <Input
        ref={ref}
        data-autofocus
        onFocus={(e) => selectName(e.currentTarget)}
        value={value}
        invalid={!!problem}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && ok && d.resolve(name)}
        className="mono"
      />
      <p className={cn("mt-2 h-4 text-[11.5px]", problem ? "text-bad" : "text-transparent")}>{problem ?? "."}</p>
    </Modal>
  );
}

const BITS: { label: "con.perm_r" | "con.perm_w" | "con.perm_x"; shift: number }[] = [
  { label: "con.perm_r", shift: 2 },
  { label: "con.perm_w", shift: 1 },
  { label: "con.perm_x", shift: 0 },
];
const WHO: { label: "con.perm_owner" | "con.perm_group" | "con.perm_other"; shift: number }[] = [
  { label: "con.perm_owner", shift: 6 },
  { label: "con.perm_group", shift: 3 },
  { label: "con.perm_other", shift: 0 },
];

function PropertiesDialog({ d }: { d: Of<"properties"> }) {
  const t = useT();
  const close = useConsole((s) => s.closeDialog);
  const chmod = useConsole((s) => s.chmod);
  const list = d.entries;
  const first = list[0];
  const [mode, setMode] = useState((first?.mode ?? 0o755) & 0o777);
  const [size, setSize] = useState<number | null>(null);
  const [sizing, setSizing] = useState(false);
  const [recursive, setRecursive] = useState(false);

  const filesBytes = useMemo(() => list.filter((e) => !e.is_dir).reduce((a, e) => a + e.size, 0), [list]);
  const dirs = list.filter((e) => e.is_dir);
  const mixed = list.some((e) => (e.mode & 0o777) !== (first?.mode & 0o777));

  useEffect(() => {
    let live = true;
    if (!dirs.length) {
      setSize(filesBytes);
      return;
    }
    setSizing(true);
    void Promise.all(dirs.map((e) => api.consoleDirSize(px.join(d.dir, e.name)).catch(() => 0)))
      .then((v) => live && setSize(filesBytes + v.reduce((a, b) => a + b, 0)))
      .finally(() => live && setSizing(false));
    return () => {
      live = false;
    };

  }, []);

  const rows: [string, React.ReactNode][] = [];
  if (list.length === 1 && first) {
    rows.push([t("con.col_name"), first.name]);
    rows.push([t("con.location"), <span className="mono break-all">{px.join(d.dir, first.name)}</span>]);
    rows.push([t("con.kind"), first.is_dir ? t("fmt.folder") : (first.name.includes(".") ? first.name.slice(first.name.lastIndexOf(".")) : t("con.file"))]);
    rows.push([t("con.col_modified"), formatModified(first.modified)]);
  } else {
    rows.push([t("con.items"), `${t("con.n_items", { n: list.length })} (${dirs.length ? `${t("con.n_folders", { n: dirs.length })}, ` : ""}${t("con.n_files", { n: list.length - dirs.length })})`]);
    rows.push([t("con.location"), <span className="mono break-all">{d.dir}</span>]);
  }
  rows.push([t("con.col_size"), sizing ? "…" : size == null ? "—" : `${bytes(size)} (${num(size)} B)`]);

  return (
    <Modal
      open
      onClose={close}
      title={list.length === 1 && first ? first.name : t("con.props_n", { n: list.length })}
      width={460}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            {t("con.close")}
          </Button>
          <Button
            variant="primary"
            disabled={!list.length}
            onClick={() => {
              void chmod(
                list.map((e) => e.name),
                mode,
                recursive,
              );
              close();
            }}
          >
            {t("con.apply_perm")}
          </Button>
        </>
      }
    >
      <dl className="space-y-2 text-[12.5px]">
        {rows.map(([k, v], i) => (
          <div key={i} className="flex gap-4">
            <dt className="w-24 shrink-0 text-muted">{k}</dt>
            <dd className="min-w-0 flex-1">{v}</dd>
          </div>
        ))}
      </dl>

      <div className="mt-4 rounded-lg border border-line bg-surface p-3">
        <div className="mb-2 flex items-center justify-between">
          <span className="eyebrow">{t("con.permissions")}</span>
          <span className="mono text-[12px]">
            {modeString(mode)} · {octal(mode)}
            {mixed && list.length > 1 && <span className="ml-2 text-warn">{t("con.perm_mixed")}</span>}
          </span>
        </div>
        <table className="w-full text-[12px]">
          <thead>
            <tr className="text-dim">
              <th className="w-24 text-left font-medium" />
              {BITS.map((b) => (
                <th key={b.shift} className="font-medium">
                  {t(b.label)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {WHO.map((w) => (
              <tr key={w.shift} className="h-8">
                <td className="text-muted">{t(w.label)}</td>
                {BITS.map((b) => {
                  const bit = 1 << (w.shift + b.shift);
                  return (
                    <td key={b.shift} className="text-center">
                      <Checkbox checked={(mode & bit) !== 0} onChange={(v) => setMode(v ? mode | bit : mode & ~bit)} label="" className="mx-auto" />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
        <div className="mt-2 flex gap-1.5">
          {[0o755, 0o777, 0o644].map((m) => (
            <Button key={m} size="sm" variant="secondary" onClick={() => setMode(m)}>
              <span className="mono">{octal(m)}</span>
            </Button>
          ))}
        </div>
        {dirs.length > 0 && (
          <div className="mt-3 border-t border-line pt-3">
            <Checkbox checked={recursive} onChange={setRecursive} label={t("con.perm_recursive")} />
            <p className="mt-1 text-[11.5px] text-dim">{t("con.perm_recursive_hint")}</p>
          </div>
        )}
      </div>
    </Modal>
  );
}

function ZipDialog({ d }: { d: Of<"zip"> }) {
  const t = useT();
  const close = useConsole((s) => s.closeDialog);
  const zip = useConsole((s) => s.zip);
  const entries = useConsole((s) => s.entries);
  const [name, setName] = useState(d.names.length === 1 ? px.splitExt(d.names[0])[0] : t("con.zip_default"));
  const [comp, setComp] = useState("smart");
  const full = name.trim().toLowerCase().endsWith(".zip") ? name.trim() : `${name.trim()}.zip`;
  const taken = entries.some((e) => e.name === full);
  const ok = px.validName(full) && name.trim() !== "" && !taken;
  return (
    <Modal
      open
      onClose={close}
      title={t("con.zip_title")}
      width={440}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            {t("read.cancel")}
          </Button>
          <Button
            variant="primary"
            disabled={!ok}
            onClick={() => {
              void zip(d.names, full, comp);
              close();
            }}
          >
            {t("con.zip")}
          </Button>
        </>
      }
    >
      <label className="eyebrow mb-2 block">{t("con.name")}</label>
      <Input data-autofocus value={name} invalid={taken} onChange={(e) => setName(e.target.value)} onFocus={(e) => e.currentTarget.select()} className="mono" />
      <p className={cn("mt-1.5 text-[11.5px]", taken ? "text-bad" : "text-dim")}>{taken ? t("con.name_taken") : `${full} · ${t("con.n_items", { n: d.names.length })}`}</p>
      <label className="eyebrow mb-2 mt-4 block">{t("con.compression")}</label>
      <Segmented
        value={comp}
        onChange={setComp}
        options={[
          { value: "smart", label: t("con.comp_smart"), tip: t("con.comp_smart_tip") },
          { value: "store", label: t("con.comp_store"), tip: t("con.comp_store_tip") },
          { value: "fast", label: t("con.comp_fast") },
          { value: "balanced", label: t("con.comp_balanced") },
        ]}
      />
    </Modal>
  );
}

function UnzipDialog({ d }: { d: Of<"unzip"> }) {
  const t = useT();
  const close = useConsole((s) => s.closeDialog);
  const unzip = useConsole((s) => s.unzip);
  const path = useConsole((s) => s.path);
  const [dest, setDest] = useState(px.join(path, px.splitExt(d.name)[0]));
  const [policy, setPolicy] = useState<Policy>("skip");
  const [del, setDel] = useState(false);
  const [pass, setPass] = useState("");
  const [pick, setPick] = useState(false);
  return (
    <>
      <Modal
        open
        onClose={close}
        title={t("con.unzip_title", { name: d.name })}
        width={480}
        footer={
          <>
            <Button variant="ghost" onClick={close}>
              {t("read.cancel")}
            </Button>
            <Button
              variant="primary"
              disabled={!dest.trim().startsWith("/")}
              onClick={() => {
                void unzip(d.name, px.norm(dest), policy, del, pass || null);
                close();
              }}
            >
              {t("con.unzip")}
            </Button>
          </>
        }
      >
        <label className="eyebrow mb-2 block">{t("con.unzip_to")}</label>
        <div className="flex gap-2">
          <Input value={dest} onChange={(e) => setDest(e.target.value)} className="mono" />
          <Button variant="secondary" onClick={() => setPick(true)}>
            {t("f.change")}
          </Button>
        </div>
        <label className="eyebrow mb-2 mt-4 block">{t("con.cf_existing")}</label>
        <Segmented
          value={policy}
          onChange={setPolicy}
          options={[
            { value: "skip", label: t("con.cf_skip") },
            { value: "replace", label: t("con.cf_replace") },
            { value: "keep_both", label: t("con.cf_keep") },
          ]}
        />
        <label className="eyebrow mb-2 mt-4 block">{t("con.password")}</label>
        <Input type="password" value={pass} onChange={(e) => setPass(e.target.value)} placeholder={t("con.password_hint")} />
        <div className="mt-4">
          <Checkbox checked={del} onChange={setDel} label={t("con.delete_after")} />
        </div>
      </Modal>
      <FolderPickerModal
        open={pick}
        title={t("con.unzip_to")}
        start={px.parent(dest) || path}
        confirm={t("con.choose_folder")}
        onPick={(p) => {
          setPick(false);
          if (p) setDest(px.join(p, px.splitExt(d.name)[0]));
        }}
      />
    </>
  );
}

function PickerDialog({ d }: { d: Of<"picker"> }) {
  return <FolderPickerModal open title={d.title} start={d.start} confirm={d.confirm} onPick={d.resolve} />;
}

function TextDialog({ d }: { d: Of<"text"> }) {
  const t = useT();
  const close = useConsole((s) => s.closeDialog);
  const refresh = useConsole((s) => s.refresh);
  const [text, setText] = useState<string | null>(null);
  const [orig, setOrig] = useState("");
  const [meta, setMeta] = useState({ version: "", newline: "lf", bom: false });
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmClose, setConfirmClose] = useState(false);
  const dirty = text !== null && text !== orig;

  useEffect(() => {
    let live = true;
    api
      .consoleReadText(d.path)
      .then((r) => {
        if (!live) return;
        setText(r.text);
        setOrig(r.text);
        setMeta({ version: r.version, newline: r.newline, bom: r.bom });
      })
      .catch((e) => live && setError(asApiError(e).message));
    return () => {
      live = false;
    };
  }, [d.path]);

  async function save() {
    if (text === null || saving) return;
    setSaving(true);
    setError(null);
    try {
      const version = await api.consoleWriteText(d.path, text, meta.version, meta.newline, meta.bom);
      setOrig(text);
      setMeta((m) => ({ ...m, version }));
      toast.success(t("con.saved"));
      void refresh(true);
    } catch (e) {
      setError(asApiError(e).message);
    } finally {
      setSaving(false);
    }
  }

  function tryClose() {
    if (dirty && !confirmClose) setConfirmClose(true);
    else close();
  }

  return (
    <Modal
      open
      onClose={tryClose}
      title={px.base(d.path)}
      description={d.path}
      width={760}
      footer={
        <>
          {error && <span className="mr-auto min-w-0 truncate text-[12px] text-bad">{error}</span>}
          {!error && dirty && <span className="mr-auto text-[12px] text-warn">{confirmClose ? t("con.discard_ask") : t("con.unsaved")}</span>}
          <Button variant={confirmClose ? "danger" : "ghost"} onClick={tryClose}>
            {confirmClose ? t("con.discard") : t("con.close")}
          </Button>
          <Button variant="primary" disabled={!dirty || saving} onClick={() => void save()}>
            {t("con.save")}
          </Button>
        </>
      }
    >
      {text === null ? (
        <div className="grid h-[320px] place-items-center text-[12.5px] text-dim">{error ?? t("con.loading")}</div>
      ) : (
        <textarea
          data-autofocus
          value={text}
          spellCheck={false}
          onChange={(e) => {
            setText(e.target.value);
            setConfirmClose(false);
          }}
          onKeyDown={(e) => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
              e.preventDefault();
              void save();
            }
          }}
          wrap="off"
          style={{ outline: "none" }}
          className="mono h-[380px] w-full resize-none rounded-lg border border-line bg-surface p-3 text-[12px] leading-relaxed focus:border-ember"
        />
      )}
    </Modal>
  );
}

export function ConsoleDialogs() {
  const d = useConsole((s) => s.dialog);
  if (!d) return null;
  switch (d.kind) {
    case "conflict":
      return <ConflictDialog d={d} />;
    case "confirm_delete":
      return <DeleteDialog d={d} />;
    case "name":
      return <NameDialog key={d.initial} d={d} />;
    case "properties":
      return <PropertiesDialog d={d} />;
    case "zip":
      return <ZipDialog d={d} />;
    case "unzip":
      return <UnzipDialog d={d} />;
    case "picker":
      return <PickerDialog d={d} />;
    case "text":
      return <TextDialog d={d} />;
  }
}
