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

import { ActionBar } from "@/features/ActionBar";
import { DropZone } from "@/features/DropZone";
import { OptionsPanel } from "@/features/OptionsPanel";
import { SourceCard } from "@/features/SourceCard";
import { TargetPicker } from "@/features/TargetPicker";
import { useStore } from "@/lib/store";

export function ConvertPage() {
  const source = useStore((s) => s.source);
  const reading = useStore((s) => s.reading);
  const target = useStore((s) => s.target);

  if (reading || !source) return <DropZone />;

  return (
    <div className="flex min-h-full flex-col px-6 pt-5">
      <div className="mx-auto w-full max-w-[1040px] flex-1 space-y-6 pb-6">
        <SourceCard info={source} />
        <TargetPicker info={source} />
        {target && <OptionsPanel info={source} />}
      </div>
      <ActionBar info={source} />
    </div>
  );
}
