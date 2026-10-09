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

import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";
import { currentLang, locale, tr } from "./i18n";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

const UNITS = ["B", "KiB", "MiB", "GiB", "TiB"];

export function num(n: number): string {
  return Math.round(n).toLocaleString(locale(currentLang()));
}

function fixed(v: number, d: number): string {
  return v.toLocaleString(locale(currentLang()), { minimumFractionDigits: d, maximumFractionDigits: d });
}

export function bytes(n: number | null | undefined, digits = 2): string {
  if (n == null || !isFinite(n)) return "—";
  let v = Math.abs(n);
  let i = 0;
  while (v >= 1024 && i < UNITS.length - 1) {
    v /= 1024;
    i++;
  }
  const d = i === 0 ? 0 : v >= 100 ? 0 : v >= 10 ? 1 : digits;
  return `${n < 0 ? "-" : ""}${fixed(v, d)} ${UNITS[i]}`;
}

export function rate(bps: number | null | undefined): string {
  if (!bps || bps < 1) return "—";
  return `${bytes(bps)}/s`;
}

export function duration(secs: number | null | undefined): string {
  if (secs == null || !isFinite(secs)) return "—";
  const s = Math.max(0, Math.round(secs));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const r = s % 60;
  const [uh, um, us] = [tr("u.h"), tr("u.min"), tr("u.s")];
  if (h > 0) return `${h}${uh} ${String(m).padStart(2, "0")}${um}`;
  if (m > 0) return `${m}${um} ${String(r).padStart(2, "0")}${us}`;
  return `${r}${us}`;
}

export function pct(done: number, total: number): number {
  if (!total) return 0;
  return Math.min(100, (done / total) * 100);
}

export function pctText(p: number): string {
  return fixed(p, p >= 99.95 ? 0 : 1);
}

export function cleanPath(p: string | null | undefined): string {
  return (p ?? "").replace(/^\\\\\?\\/, "");
}

export function sizeDelta(inBytes: number, outBytes: number): { text: string; bigger: boolean } | null {
  if (!inBytes) return null;
  const d = outBytes / inBytes - 1;
  if (Math.abs(d) < 0.005) return null;
  const p = Math.abs(d) * 100;
  return { text: `${d < 0 ? "−" : "+"}${fixed(p, p < 10 ? 1 : 0)}%`, bigger: d > 0 };
}

export function baseName(p: string): string {
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] ?? p;
}

export function timeOfDay(ms: number): string {
  return new Date(ms).toLocaleTimeString(locale(currentLang()), { hour: "2-digit", minute: "2-digit" });
}

export function dateTime(ms: number): string {
  return new Date(ms).toLocaleString(locale(currentLang()), { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
