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

import * as SwitchPrimitive from "@radix-ui/react-switch";
import { cn } from "@/lib/utils";

export function Switch({
  checked,
  onCheckedChange,
  disabled,
  id,
}: {
  checked: boolean;
  onCheckedChange: (v: boolean) => void;
  disabled?: boolean;
  id?: string;
}) {
  return (
    <SwitchPrimitive.Root
      id={id}
      checked={checked}
      disabled={disabled}
      onCheckedChange={onCheckedChange}
      className={cn(
        "relative h-[20px] w-[36px] shrink-0 cursor-pointer rounded-full border border-line-strong bg-surface-4",
        "data-[state=checked]:border-ember data-[state=checked]:bg-ember",
        "disabled:cursor-not-allowed disabled:opacity-40",
      )}
    >
      <SwitchPrimitive.Thumb className="block h-[14px] w-[14px] translate-x-[3px] rounded-full bg-fg transition-transform duration-[var(--d-med)] ease-[var(--ease)] will-change-transform data-[state=checked]:translate-x-[17px] data-[state=checked]:bg-[var(--on-ember)]" />
    </SwitchPrimitive.Root>
  );
}
