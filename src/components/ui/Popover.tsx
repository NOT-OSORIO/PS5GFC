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

import { AnimatePresence, motion } from "motion/react";
import { useEffect, useRef, type ReactNode } from "react";
import { cn } from "@/lib/utils";

export function Popover({
  open,
  onClose,
  anchor,
  children,
  align = "right",
  side = "bottom",
  className,
}: {
  open: boolean;
  onClose: () => void;

  anchor: React.RefObject<HTMLElement | null>;
  children: ReactNode;
  align?: "left" | "right";

  side?: "bottom" | "top";
  className?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const up = side === "top";

  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => {
      const n = e.target as Node;
      if (ref.current?.contains(n) || anchor.current?.contains(n)) return;
      onClose();
    };
    const key = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("mousedown", down);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("mousedown", down);
      window.removeEventListener("keydown", key);
    };
  }, [open, onClose, anchor]);

  return (
    <AnimatePresence>
      {open && (
        <motion.div
          ref={ref}
          initial={{ opacity: 0, y: up ? 4 : -4, scale: 0.98 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={{ opacity: 0, y: up ? 4 : -4, scale: 0.98, transition: { duration: 0.1 } }}
          transition={{ duration: 0.14, ease: [0.2, 0.7, 0.2, 1] }}
          style={{ transformOrigin: `${up ? "bottom" : "top"} ${align}` }}
          className={cn(
            "absolute z-50 rounded-xl bg-surface-2 shadow-[var(--shadow-pop)]",
            up ? "bottom-full mb-2" : "top-full mt-2",
            align === "right" ? "right-0" : "left-0",
            className,
          )}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  );
}
