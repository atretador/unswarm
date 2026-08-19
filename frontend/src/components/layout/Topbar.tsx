import { useEffect, useRef } from "react";
import { Menu, X } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { ThemeToggle } from "./ThemeToggle";
import { StatusDot } from "../ui/StatusDot";
import { NAV_ITEMS } from "../../lib/nav-items";

export interface TopbarProps {
  title: string;
  mobileOpen: boolean;
  onMobileToggle: () => void;
}

export function Topbar({ title, mobileOpen, onMobileToggle }: TopbarProps) {
  return (
    <header
      className="
        flex items-center h-[var(--topbar-height)] px-4
        border-b border-[var(--color-border)]
        bg-[var(--color-bg-surface)]
        sticky top-0 z-30
      "
    >
      {/* Mobile hamburger */}
      <button
        onClick={onMobileToggle}
        className="
          lg:hidden flex items-center justify-center size-7 mr-3
          rounded-[var(--radius-md)] text-[var(--color-text-muted)]
          hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
          transition-colors duration-[var(--duration-fast)]
          cursor-pointer
        "
        aria-label={mobileOpen ? "Close navigation" : "Open navigation"}
      >
        {mobileOpen ? <X className="size-4" /> : <Menu className="size-4" />}
      </button>

      {/* Page title */}
      <h1 className="font-heading text-sm font-semibold text-[var(--color-text-heading)]">
        {title}
      </h1>

      {/* Right side */}
      <div className="ml-auto flex items-center gap-3">
        {/* Live status indicator */}
        <div className="flex items-center gap-1.5 text-xs text-[var(--color-text-muted)]">
          <StatusDot status="running" size="sm" />
          <span className="hidden sm:inline">Proxy active</span>
        </div>

        <ThemeToggle />
      </div>
    </header>
  );
}

export interface MobileDrawerProps {
  open: boolean;
  onClose: () => void;
}

export function MobileDrawer({ open, onClose }: MobileDrawerProps) {
  const location = useLocation();
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  // Close on Escape + focus the close button when the drawer opens
  useEffect(() => {
    if (!open) return;
    closeButtonRef.current?.focus();
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <>
      {/* Overlay */}
      <div
        className="fixed inset-0 z-40 bg-[var(--color-bg-overlay)] lg:hidden"
        onClick={onClose}
        aria-hidden="true"
      />

      {/* Drawer */}
      <div
        className="
          fixed inset-y-0 left-0 z-50 w-64
          bg-[var(--color-bg-surface)] border-r border-[var(--color-border)]
          flex flex-col
          lg:hidden
        "
        style={{ animation: "slideInLeft 200ms ease-out" }}
      >
        {/* Header */}
        <div className="flex items-center justify-between h-[var(--topbar-height)] px-4 border-b border-[var(--color-border)]">
          <span className="font-heading text-sm font-semibold text-[var(--color-text-heading)]">
            unswarm
          </span>
          <button
            ref={closeButtonRef}
            onClick={onClose}
            className="
              flex items-center justify-center size-7 rounded-[var(--radius-md)]
              text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)]
              transition-colors cursor-pointer
            "
            aria-label="Close navigation"
          >
            <X className="size-4" />
          </button>
        </div>

        {/* Nav items */}
        <nav className="flex-1 py-2 px-1.5 space-y-0.5 overflow-y-auto">
          {NAV_ITEMS.map(({ to, label }) => {
            const isActive =
              to === "/"
                ? location.pathname === "/"
                : location.pathname.startsWith(to);

            return (
              <NavLink
                key={to}
                to={to}
                onClick={onClose}
                className={`
                  flex items-center gap-2.5 rounded-[var(--radius-lg)] px-3 py-2
                  text-sm font-medium transition-colors duration-[var(--duration-fast)]
                  ${
                    isActive
                      ? "bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
                      : "text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
                  }
                `}
              >
                {label}
              </NavLink>
            );
          })}
        </nav>
      </div>
    </>
  );
}
