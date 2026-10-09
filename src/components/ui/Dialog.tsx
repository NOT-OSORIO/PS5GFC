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

import * as DialogPrimitive from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export function Modal({
  open,
  onClose,
  title,
  description,
  children,
  footer,
  width = 440,
  closable = true,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: string;
  children?: ReactNode;
  footer?: ReactNode;
  width?: number;
  closable?: boolean;
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={(v) => !v && closable && onClose()}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-[90] bg-black/55 [animation:fade-in_.14s_var(--ease)]" />
        <DialogPrimitive.Content

          onOpenAutoFocus={(e) => {
            e.preventDefault();
            const root = e.currentTarget as HTMLElement | null;
            const target = root?.querySelector<HTMLElement>("[data-autofocus]") ?? root?.querySelector<HTMLElement>("input,textarea") ?? root;
            target?.focus();
          }}
          onEscapeKeyDown={(e) => !closable && e.preventDefault()}
          onInteractOutside={(e) => !closable && e.preventDefault()}
          style={{ width }}
          className={cn(
            "fixed left-1/2 top-1/2 z-[91] max-h-[calc(100vh-48px)] max-w-[calc(100vw-48px)] -translate-x-1/2 -translate-y-1/2",
            "flex flex-col rounded-xl bg-surface-2 shadow-[var(--shadow-pop)] outline-none [animation:pop-in_.16s_var(--ease)]",
          )}
        >
          <header className="flex items-start justify-between gap-4 border-b border-line px-5 py-3.5">
            <div className="min-w-0">
              <DialogPrimitive.Title className="truncate text-[14px] font-semibold">{title}</DialogPrimitive.Title>
              {description ? (
                <DialogPrimitive.Description className="mt-0.5 text-[12px] leading-snug text-muted">{description}</DialogPrimitive.Description>
              ) : (
                <DialogPrimitive.Description className="sr-only">{title}</DialogPrimitive.Description>
              )}
            </div>
            {closable && (
              <DialogPrimitive.Close className="-mr-1.5 grid h-7 w-7 shrink-0 place-items-center rounded-md text-dim hover:bg-surface-3 hover:text-fg" aria-label="×">
                <X className="h-4 w-4" />
              </DialogPrimitive.Close>
            )}
          </header>
          <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">{children}</div>
          {footer && <footer className="flex items-center justify-end gap-2 border-t border-line px-5 py-3">{footer}</footer>}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
