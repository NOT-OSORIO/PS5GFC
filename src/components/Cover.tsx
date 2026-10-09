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

import { Gamepad2 } from "lucide-react";
import { useState } from "react";

export function Cover({ icon, size = 56, radius = 12 }: { icon: string | null; size?: number; radius?: number }) {
  const [failed, setFailed] = useState(false);
  return (
    <div className="shrink-0 overflow-hidden border border-line-strong bg-surface-3" style={{ width: size, height: size, borderRadius: radius }}>
      {icon && !failed ? (
        <img src={icon} alt="" className="h-full w-full object-cover" draggable={false} onError={() => setFailed(true)} />
      ) : (
        <div className="grid h-full w-full place-items-center text-dim">
          <Gamepad2 style={{ width: size * 0.45, height: size * 0.45 }} />
        </div>
      )}
    </div>
  );
}
