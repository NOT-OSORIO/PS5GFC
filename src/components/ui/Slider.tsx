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

import * as SliderPrimitive from "@radix-ui/react-slider";

export function Slider({
  value,
  min,
  max,
  step = 1,
  onChange,
  disabled,
}: {
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange: (v: number) => void;
  disabled?: boolean;
}) {
  return (
    <SliderPrimitive.Root
      value={[value]}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      onValueChange={(v) => onChange(v[0])}
      className="relative flex h-5 w-full touch-none select-none items-center"
    >
      <SliderPrimitive.Track className="relative h-[4px] grow overflow-hidden rounded-full bg-surface-4">
        <SliderPrimitive.Range className="absolute h-full rounded-full bg-ember" />
      </SliderPrimitive.Track>
      <SliderPrimitive.Thumb className="block h-[14px] w-[14px] rounded-full border-2 border-ember bg-surface shadow transition-transform duration-[var(--d-fast)] hover:scale-110 focus-visible:scale-110" />
    </SliderPrimitive.Root>
  );
}
