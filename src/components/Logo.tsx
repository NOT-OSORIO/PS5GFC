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

import { useId } from "react";
import { useStore } from "@/lib/store";

interface Palette {
  tile: [string, string];
  tileStroke: [string, number];
  disc: [string, string, string];
  wedge: [string, number];
  groove: [string, number];
  rim: [string, number];
  ember: [string, string, string];
  hub: [string, string];
  hole: string;
  holeStroke: [string, number];
}

const PALETTES: Record<"dark" | "light", Palette> = {
  dark: {
    tile: ["#1b2130", "#090b10"],
    tileStroke: ["#fff", 0.07],
    disc: ["#48516a", "#252b3b", "#0e1118"],
    wedge: ["#fff", 0.13],
    groove: ["#fff", 0.07],
    rim: ["#fff", 0.42],
    ember: ["#FFD87A", "#FF8C3F", "#FF3D55"],
    hub: ["#e6eaf2", "#7c8596"],
    hole: "#0b0e14",
    holeStroke: ["#fff", 0.22],
  },
  light: {
    tile: ["#ffffff", "#d6dce9"],
    tileStroke: ["#1b2130", 0.2],
    disc: ["#f6f8fc", "#cfd6e4", "#a3adc2"],
    wedge: ["#fff", 0.55],
    groove: ["#1b2130", 0.1],
    rim: ["#1b2130", 0.38],
    ember: ["#ff9d2e", "#ff6a2b", "#e8284a"],
    hub: ["#3a4259", "#171c2b"],
    hole: "#f2f4f9",
    holeStroke: ["#1b2130", 0.25],
  },
};

export function Logo({ size = 28, tile = true, theme }: { size?: number; tile?: boolean; theme?: "dark" | "light" }) {
  const id = useId();
  const current = useStore((s) => s.settings.theme);
  const c = PALETTES[theme ?? current];
  const u = (n: string) => `url(#${id}${n})`;
  return (
    <svg width={size} height={size} viewBox="0 0 512 512" aria-hidden>
      <defs>
        <linearGradient id={`${id}bg`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={c.tile[0]} />
          <stop offset="1" stopColor={c.tile[1]} />
        </linearGradient>
        <radialGradient id={`${id}disc`} cx="50%" cy="42%" r="62%">
          <stop offset="0" stopColor={c.disc[0]} />
          <stop offset=".62" stopColor={c.disc[1]} />
          <stop offset="1" stopColor={c.disc[2]} />
        </radialGradient>
        <linearGradient id={`${id}em`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={c.ember[0]} />
          <stop offset=".45" stopColor={c.ember[1]} />
          <stop offset="1" stopColor={c.ember[2]} />
        </linearGradient>
        <linearGradient id={`${id}hub`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={c.hub[0]} />
          <stop offset="1" stopColor={c.hub[1]} />
        </linearGradient>
        <clipPath id={`${id}cd`}>
          <circle cx="256" cy="256" r="190" />
        </clipPath>
      </defs>
      {tile && (
        <>
          <rect x="16" y="16" width="480" height="480" rx="112" fill={u("bg")} />
          <rect x="17" y="17" width="478" height="478" rx="111" fill="none" stroke={c.tileStroke[0]} strokeOpacity={c.tileStroke[1]} strokeWidth="2" />
        </>
      )}
      <circle cx="256" cy="256" r="190" fill={u("disc")} />
      <g clipPath={u("cd")}>
        <path d="M256 256 L305.2 72.5 A190 190 0 0 1 390.4 121.6 Z" fill={c.wedge[0]} fillOpacity={c.wedge[1]} />
        <path d="M256 256 L206.8 439.5 A190 190 0 0 1 121.6 390.4 Z" fill={c.wedge[0]} fillOpacity={c.wedge[1]} />
      </g>
      <g fill="none" stroke={c.groove[0]} strokeOpacity={c.groove[1]} strokeWidth="2">
        <circle cx="256" cy="256" r="176" />
        <circle cx="256" cy="256" r="158" />
        <circle cx="256" cy="256" r="140" />
      </g>
      <circle cx="256" cy="256" r="190" fill="none" stroke={c.rim[0]} strokeOpacity={c.rim[1]} strokeWidth="4" />
      <g fill="none" stroke={u("em")} strokeWidth="20" strokeLinecap="round">
        <path d="M158.1 210.4 A108 108 0 0 1 332.4 179.6" />
        <path d="M353.9 301.6 A108 108 0 0 1 179.6 332.4" />
      </g>
      <g fill={u("em")} stroke={u("em")} strokeWidth="6" strokeLinejoin="round">
        <path d="M348 164 L316.8 195.2 L350.8 198 Z" />
        <path d="M164 348 L195.2 316.8 L161.2 314 Z" />
      </g>
      <circle cx="256" cy="256" r="62" fill={u("hub")} />
      <circle cx="256" cy="256" r="40" fill={c.hole} />
      <circle cx="256" cy="256" r="40" fill="none" stroke={c.holeStroke[0]} strokeOpacity={c.holeStroke[1]} strokeWidth="2" />
      <circle cx="256" cy="256" r="9" fill={u("em")} />
    </svg>
  );
}
