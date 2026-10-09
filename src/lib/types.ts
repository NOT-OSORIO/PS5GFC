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

import type { ConsoleSettings } from "./console";
import type { Lang } from "./i18n";

export type FsKind = "exfat" | "ufs2" | "pfs";

export type Format =
  | { kind: "folder" }
  | { kind: "image"; inner: FsKind }
  | { kind: "ffpfsc"; inner: FsKind }
  | { kind: "pkg" };

export type Severity = "info" | "warn" | "error";
export interface Issue {
  severity: Severity;

  code: string;
  args: Record<string, string>;

  msg: string;
}

export type Route = "extract" | "reuse_image" | "build";

export interface TitleInfo {
  title_id: string | null;
  content_id: string | null;
  title_name: string | null;
  content_version: string | null;
  has_eboot: boolean;
  ampr: boolean;

  ampr_packs: boolean;
  playgo: boolean;
}

export interface WrapperInfo {
  file_size: number;
  inner_name: string;
  inner_size: number;
}

export interface TargetInfo {
  format: Format;
  route: Route;
  issues: Issue[];
}

export interface SourceInfo {
  path: string;
  format: Format;
  files: number;
  dirs: number;
  bytes: number;
  title: TitleInfo;
  icon: string | null;
  wrapper: WrapperInfo | null;
  details: [string, string][];
  issues: Issue[];
  targets: TargetInfo[];
}

export interface ReadProgress {
  stage: Stage;
  files: number;
  total: number;
  current: string;
  elapsed_ms: number;
}

export type AmprMode = "auto" | "always" | "never";

export type NameMode = "ppsa" | "ppsa_title" | "ppsa_title_version";

export type WorkerPriority = "normal" | "low" | "low_perf";

export interface ConvertOptions {
  threads: number;
  level: number;
  threshold_gain_pct: number;
  skip_incompressible: boolean;
  rebuild: boolean;
  cluster_kib: number;
  preserve_times: boolean;
  free_mib: number;
  overwrite: boolean;
  verify: boolean;
  ampr: AmprMode;
  priority: WorkerPriority;

  out_name?: string | null;
  name_mode: NameMode;
}

export type Stage = "idle" | "scanning" | "planning" | "processing" | "finalizing" | "verifying" | "done";

export interface Snapshot {
  stage: Stage;
  done: number;
  total: number;
  out_bytes: number;
  files_done: number;
  files_total: number;
  current: string;
  elapsed_ms: number;
  speed_bps: number;
  eta_secs: number | null;
  ratio: number | null;
}

export type JobStatus = "queued" | "running" | "done" | "failed" | "cancelled";

export interface ConsoleSpec {
  host: string;
  port: number;
  protocol?: "prospero" | "ftp";

  user?: string;

  pass?: string;
}

export interface ConvertReport {
  output: string;

  console?: boolean;
  route: Route;
  source_format: Format;
  target_format: Format;
  in_bytes: number;
  out_bytes: number;
  files: number;
  elapsed_ms: number;
  warnings: string[];
}

export interface JobView {
  id: number;
  source: string;
  target: Format;
  dest: string;
  console?: ConsoleSpec | null;
  label: string | null;
  icon: string | null;
  status: JobStatus;
  report: ConvertReport | null;
  error: string | null;
  created_ms: number;
  started_ms: number | null;
  finished_ms: number | null;
  snapshot: Snapshot | null;
  options: { threads: number; level: number; rebuild: boolean; verify: boolean };
}

export interface LogEntry {
  level: "info" | "warn" | "error";
  msg: string;
  t_ms: number;
}

export interface JobSpec {
  source: string;
  target: Format;
  dest: string;
  options: ConvertOptions;
  label: string | null;
  icon: string | null;

  console?: ConsoleSpec | null;
}

export interface AppInfo {
  version: string;
  logical_cpus: number;
  default_threads: number;
  ram_bytes: number | null;
  data_dir: string;
}

export interface PathInfo {
  exists: boolean;
  is_dir: boolean;
  name: string;
}

export type SectionId = "perf" | "fs" | "comp" | "out";

export interface Profile {

  std: Record<SectionId, boolean>;

  custom: Partial<ConvertOptions> & { outputDir?: string | null; inner?: FsKind; dest?: "pc" | "console" };
}

export interface NotifyPrefs {
  system: boolean;
  toast: boolean;
  openFolder: boolean;
}

export interface Settings {
  theme: "dark" | "light";
  lang: Lang;
  notify: NotifyPrefs;
  uiZoom: number;

  nameMode: NameMode;

  profiles: Record<string, Profile>;

  console: ConsoleSettings;
}

export type NoticeKind = "done" | "failed" | "cancelled";

export interface Notice {
  id: number;
  kind: NoticeKind;
  name: string;
  detail: string;
  ts: number;
  read: boolean;

  path?: string;

  console?: boolean;
}
