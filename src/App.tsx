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

import { motion } from "motion/react";
import { useEffect } from "react";
import { Toaster } from "sonner";
import { Sidebar } from "@/components/Sidebar";
import { useConsole } from "@/lib/consoleStore";
import { TitleBar } from "@/components/TitleBar";
import { TopActions } from "@/components/TopActions";
import { TipProvider } from "@/components/ui/Tip";
import { useStore, useT } from "@/lib/store";
import { AboutPage } from "@/pages/AboutPage";
import { ConsolePage } from "@/pages/ConsolePage";
import { ConvertPage } from "@/pages/ConvertPage";
import { QueuePage } from "@/pages/QueuePage";

export default function App() {
  const t = useT();
  const page = useStore((s) => s.page);
  const theme = useStore((s) => s.settings.theme);
  const hydrate = useStore((s) => s.hydrate);
  const ready = useStore((s) => s.ready);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  useEffect(() => {
    if (!ready) return;
    void useConsole.getState().initEvents();
    const c = useStore.getState().settings.console;
    if (c.auto && c.host) void useConsole.getState().connect();
  }, [ready]);

  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (!(e.target instanceof HTMLInputElement)) e.preventDefault();
    };
    window.addEventListener("contextmenu", h);
    return () => window.removeEventListener("contextmenu", h);
  }, []);

  return (
    <TipProvider>
      <div className="flex h-full flex-col">
        <TitleBar />
        <div className="flex min-h-0 flex-1">
          <Sidebar />
          <div className="flex min-w-0 flex-1 flex-col">
            <header className="relative z-30 flex h-14 shrink-0 items-center justify-between border-b border-line bg-bg px-6">
              <h1 className="text-[15px] font-semibold">{t(page === "convert" ? "nav.convert" : page === "queue" ? "nav.queue" : page === "console" ? "con.title" : "about.title")}</h1>
              <TopActions />
            </header>
            <main className="min-h-0 flex-1 overflow-y-auto">

              <motion.div key={page} initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.16, ease: [0.2, 0.7, 0.2, 1] }} className="h-full">
                {page === "convert" && <ConvertPage />}
                {page === "queue" && <QueuePage />}
                {page === "console" && <ConsolePage />}
                {page === "about" && <AboutPage />}
              </motion.div>
            </main>
          </div>
        </div>
      </div>
      <Toaster position="bottom-right" theme={theme} closeButton />
    </TipProvider>
  );
}
