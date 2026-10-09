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

import { ArrowUpRight, Copy, Heart } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Logo } from "@/components/Logo";
import { Button } from "@/components/ui/Button";
import { Chip } from "@/components/ui/Chip";
import { Modal } from "@/components/ui/Dialog";
import { Tip } from "@/components/ui/Tip";
import { api } from "@/lib/api";
import { CREDITS } from "@/lib/credits";
import { DONATE_ADDRESSES } from "@/lib/donate";
import { useStore, useT } from "@/lib/store";

function SupportDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const t = useT();
  return (
    <Modal open={open} onClose={onClose} title={t("about.support_title")} description={t("about.support_desc")} width={420}>
      <ul className="space-y-2">
        {DONATE_ADDRESSES.map((d) => (
          <li key={d.id} className="rounded-lg border border-line bg-surface px-3 py-2.5">
            <div className="flex items-baseline justify-between gap-2">
              <span className="text-[12.5px] font-semibold">{d.label}</span>
              <span className="mono text-[10.5px] text-dim">{d.network}</span>
            </div>
            <div className="mt-1.5 flex items-center gap-2">
              <span className="mono min-w-0 flex-1 truncate text-[12px] text-muted" title={d.address}>
                {d.address}
              </span>
              <Tip content={t("about.copy_address")}>
                <Button
                  size="sm"
                  variant="ghost"
                  iconOnly
                  icon={<Copy className="h-3.5 w-3.5" />}
                  aria-label={t("about.copy_address")}
                  onClick={() => void navigator.clipboard?.writeText(d.address).then(() => toast(t("about.address_copied")))}
                />
              </Tip>
            </div>
          </li>
        ))}
      </ul>
    </Modal>
  );
}

export function AboutPage() {
  const t = useT();
  const app = useStore((s) => s.app);
  const [support, setSupport] = useState(false);
  return (
    <div className="flex min-h-full flex-col items-center justify-center px-6 py-12">
      <div className="flex max-w-[520px] flex-col items-center text-center">
        <Logo size={112} />
        <div className="mt-6 flex items-center gap-2.5">
          <h2 className="text-[22px] font-bold tracking-[0.03em]">PS5GFC</h2>
          <Chip className="mono">
            {t("about.version")} {app?.version ?? "—"}
          </Chip>
          <Tip content={t("about.support")}>
            <button
              type="button"
              onClick={() => setSupport(true)}
              aria-label={t("about.support")}
              className="grid h-6 w-6 place-items-center rounded-md text-dim transition-colors duration-[var(--d-fast)] hover:text-ember"
            >
              <Heart className="h-4 w-4" />
            </button>
          </Tip>
        </div>
        <p className="mt-1 text-[12.5px] text-muted">PS5 Game Format Converter</p>
        <p className="mt-5 text-[12px] text-dim">
          {t("about.made_by")} <span className="font-semibold tracking-wide text-fg">OSØRIO</span>
        </p>
      </div>

      <SupportDialog open={support} onClose={() => setSupport(false)} />

      <section className="mt-10 w-full max-w-[680px]">
        <h3 className="eyebrow">{t("about.thanks_title")}</h3>
        <div className="mt-3 rounded-xl border border-line bg-surface px-6 py-5 text-center">
          <Heart className="mx-auto h-4 w-4 text-ember" />
          <p className="mt-3 whitespace-pre-line text-[13px] leading-relaxed text-muted">{t("about.thanks_body")}</p>
        </div>
      </section>

      <section className="mt-10 w-full max-w-[680px]">
        <h3 className="eyebrow">{t("about.credits")}</h3>
        <ul className="mt-3 space-y-2">
          {CREDITS.map((c) => (
            <li key={c.url}>
              <Tip content={t("about.open_repo")} side="top">
                <button
                  type="button"
                  onClick={() => void api.openUrl(c.url)}
                  className="group flex w-full items-center gap-4 rounded-xl border border-line bg-surface px-4 py-3 text-left hover:border-line-strong hover:bg-surface-2"
                >
                  <span className="min-w-0 sm:w-[210px] sm:shrink-0">
                    <span className="block truncate text-[13.5px] font-semibold">{c.author}</span>
                    <span className="mono block truncate text-[11.5px] text-dim">{c.project}</span>
                  </span>
                  <span className="min-w-0 flex-1 text-[12.5px] leading-snug text-muted">{t(c.role)}</span>
                  <ArrowUpRight className="h-4 w-4 shrink-0 text-dim group-hover:text-fg" />
                </button>
              </Tip>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
