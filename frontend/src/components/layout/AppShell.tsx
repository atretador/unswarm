import { useState, useCallback } from "react";
import { Link, Outlet, useLocation } from "react-router-dom";
import { motion } from "motion/react";
import { AlertTriangle } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Sidebar } from "./Sidebar";
import { Topbar, MobileDrawer } from "./Topbar";
import { ErrorBoundary } from "../ErrorBoundary";
import { useAuth } from "../../lib/auth-context";

/** Maps route base paths to translation keys in the 'settings' namespace. */
const PAGE_TITLE_KEYS: Record<string, string> = {
  "/": "dashboard",
  "/models": "models",
  "/swarm": "swarm",
  "/fleet": "swarm",
  "/benchmarks": "benchmarks",
  "/queue": "queue",
  "/logs": "logs",
  "/settings": "title",
  "/profile": "profile",
};

export function AppShell() {
  const location = useLocation();
  const { user } = useAuth();
  const { t } = useTranslation("settings");
  const { t: tCommon } = useTranslation("common");
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
  const titleKey = PAGE_TITLE_KEYS[basePath];
  const title = titleKey ? t(titleKey) : "unswarm";

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
              {tCommon("tempPasswordBanner")}{" "}
              <Link
                to="/profile"
                className="font-medium underline underline-offset-2 hover:text-[var(--color-text-heading)] transition-colors"
              >
                {tCommon("changeItInProfile")}
              </Link>
            </span>
          </div>
        )}

        <main className="flex-1 overflow-y-auto">
          <motion.div
            key={location.pathname}
            initial={{ opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{
              duration: 0.2,
              ease: [0.16, 1, 0.3, 1],
            }}
            className="h-full"
          >
            <ErrorBoundary>
              <Outlet />
            </ErrorBoundary>
          </motion.div>
        </main>
      </div>
    </div>
  );
}
