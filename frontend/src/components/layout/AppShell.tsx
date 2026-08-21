import { useState, useCallback } from "react";
import { Link, useLocation } from "react-router-dom";
import { AnimatePresence, motion } from "motion/react";
import { AlertTriangle } from "lucide-react";
import { Sidebar } from "./Sidebar";
import { Topbar, MobileDrawer } from "./Topbar";
import { useAuth } from "../../lib/auth-context";

const PAGE_TITLES: Record<string, string> = {
  "/": "Dashboard",
  "/models": "Models",
  "/fleet": "Fleet",
  "/benchmarks": "Benchmarks",
  "/queue": "Queue",
  "/logs": "Logs",
  "/settings": "Settings",
  "/profile": "Profile",
};

export function AppShell({ children }: { children: React.ReactNode }) {
  const location = useLocation();
  const { user } = useAuth();
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);

  const toggleSidebar = useCallback(
    () => setSidebarCollapsed((p) => !p),
    [],
  );
  const toggleMobile = useCallback(
    () => setMobileOpen((p) => !p),
    [],
  );

  // Determine page title
  const basePath = "/" + (location.pathname.split("/")[1] ?? "");
  const title = PAGE_TITLES[basePath] ?? "unswarm";

  return (
    <div className="flex h-screen overflow-hidden">
      {/* Desktop sidebar */}
      <Sidebar collapsed={sidebarCollapsed} onToggle={toggleSidebar} />

      {/* Mobile drawer */}
      <MobileDrawer open={mobileOpen} onClose={() => setMobileOpen(false)} />

      {/* Main area */}
      <div className="flex flex-col flex-1 min-w-0">
        <Topbar
          title={title}
          mobileOpen={mobileOpen}
          onMobileToggle={toggleMobile}
        />

        {/* Temp password banner */}
        {user?.isTempPassword && (
          <div
            className="
              flex items-center gap-2 px-4 py-2
              bg-[var(--color-status-warning)]/10 border-b border-[var(--color-status-warning)]/30
              text-sm text-[var(--color-status-warning)]
            "
          >
            <AlertTriangle className="size-4 shrink-0" />
            <span>
              You&apos;re using a temporary password.{" "}
              <Link
                to="/profile"
                className="font-medium underline underline-offset-2 hover:text-[var(--color-text-heading)] transition-colors"
              >
                Change it in Profile
              </Link>
            </span>
          </div>
        )}

        <main className="flex-1 overflow-y-auto">
          <AnimatePresence mode="wait">
            <motion.div
              key={location.pathname}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -4 }}
              transition={{
                duration: 0.2,
                ease: [0.16, 1, 0.3, 1],
              }}
              className="h-full"
            >
              {children}
            </motion.div>
          </AnimatePresence>
        </main>
      </div>
    </div>
  );
}
