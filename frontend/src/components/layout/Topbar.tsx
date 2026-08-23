import { useEffect, useRef, useCallback, useState } from "react";
import { Menu, X, LogOut, Settings, User, ChevronDown } from "lucide-react";
import { Link, NavLink, useLocation, useNavigate } from "react-router-dom";
import { ThemeToggle } from "./ThemeToggle";
import { StatusDot } from "../ui/StatusDot";
import { Logo } from "../ui/Logo";
import { NAV_ITEMS } from "../../lib/nav-items";
import { useAuth } from "../../lib/auth-context";

export interface TopbarProps {
  title: string;
  mobileOpen: boolean;
  onMobileToggle: () => void;
}

function UserAvatar({ username }: { username?: string }) {
  const letter = username?.charAt(0)?.toUpperCase() ?? "?";
  return (
    <div className="flex items-center justify-center size-7 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] font-heading text-xs font-bold select-none">
      {letter}
    </div>
  );
}

export function Topbar({ title, mobileOpen, onMobileToggle }: TopbarProps) {
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close dropdown on outside click
  useEffect(() => {
    if (!menuOpen) return;
    function handleClick(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    }
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [menuOpen]);

  // Close on Escape
  useEffect(() => {
    if (!menuOpen) return;
    function handleKey(e: KeyboardEvent) {
      if (e.key === "Escape") setMenuOpen(false);
    }
    document.addEventListener("keydown", handleKey);
    return () => document.removeEventListener("keydown", handleKey);
  }, [menuOpen]);

  async function handleSignOut() {
    setMenuOpen(false);
    await logout();
    navigate("/login", { replace: true });
  }

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

        {/* User chip + menu */}
        {user && (
          <div className="flex items-center gap-1">
            {/* Clickable user chip → navigates to /profile */}
            <Link
              to="/profile"
              className="
                flex items-center gap-2
                rounded-full transition-all duration-[var(--duration-fast)]
                hover:ring-2 hover:ring-[var(--color-focus-ring)]
                focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
              "
              aria-label="Go to profile"
            >
              <UserAvatar username={user.username} />
              <span className="hidden sm:inline text-sm font-medium text-[var(--color-text-heading)] truncate max-w-[8rem]">
                {user.username}
              </span>
            </Link>

            {/* Chevron → opens dropdown with Settings + Sign out */}
            <div className="relative" ref={menuRef}>
              <button
                onClick={() => setMenuOpen((p) => !p)}
                className="
                  flex items-center justify-center size-7
                  rounded-[var(--radius-md)]
                  text-[var(--color-text-muted)]
                  hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
                  transition-colors duration-[var(--duration-fast)]
                  cursor-pointer
                "
                aria-label="User menu"
                aria-expanded={menuOpen}
              >
                <ChevronDown className="size-3.5" />
              </button>

              {menuOpen && (
                <div
                  className="
                    absolute right-0 top-full mt-1.5 w-48 z-50
                    rounded-[var(--radius-lg)] border border-[var(--color-border)]
                    bg-[var(--color-bg-surface)] shadow-lg
                    py-1
                  "
                  style={{ animation: "fadeInDown 120ms ease-out" }}
                >
                  <button
                    onClick={() => {
                      setMenuOpen(false);
                      navigate("/settings");
                    }}
                    className="
                      flex items-center gap-2 w-full px-3 py-2
                      text-sm text-[var(--color-text-muted)]
                      hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
                      transition-colors duration-[var(--duration-fast)]
                      cursor-pointer
                    "
                  >
                    <Settings className="size-3.5" />
                    Settings
                  </button>

                  <button
                    onClick={handleSignOut}
                    className="
                      flex items-center gap-2 w-full px-3 py-2
                      text-sm text-[var(--color-status-error)]
                      hover:bg-[var(--color-status-error)]/10
                      transition-colors duration-[var(--duration-fast)]
                      cursor-pointer
                    "
                  >
                    <LogOut className="size-3.5" />
                    Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        )}
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
  const navigate = useNavigate();
  const { user, logout } = useAuth();
  const drawerRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  // Focus trap + close on Escape
  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onClose();
        return;
      }
      if (e.key !== "Tab" || !drawerRef.current) return;

      const focusable = drawerRef.current.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      if (focusable.length === 0) return;

      const first = focusable[0];
      const last = focusable[focusable.length - 1];

      if (e.shiftKey) {
        if (document.activeElement === first) {
          e.preventDefault();
          last.focus();
        }
      } else {
        if (document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }
    },
    [onClose],
  );

  // When opening: store previous focus, focus close button, add keydown listener
  // When closing: restore focus
  useEffect(() => {
    if (open) {
      previousFocusRef.current = document.activeElement as HTMLElement;
      closeButtonRef.current?.focus();
      document.addEventListener("keydown", handleKeyDown);
    } else {
      previousFocusRef.current?.focus();
    }
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [open, handleKeyDown]);

  async function handleSignOut() {
    await logout();
    onClose();
    navigate("/login", { replace: true });
  }

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
        ref={drawerRef}
        role="dialog"
        aria-modal="true"
        aria-label="Navigation menu"
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
          <div className="flex items-center gap-2.5 min-w-0">
            <Logo size={28} />
            <span className="font-heading text-sm font-semibold text-[var(--color-text-heading)]">
              unswarm
            </span>
          </div>
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
          {NAV_ITEMS.map(({ to, icon: Icon, label }) => {
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
                <Icon className="size-4 shrink-0" />
                {label}
              </NavLink>
            );
          })}
        </nav>

        {/* User section */}
        {user && (
          <div className="border-t border-[var(--color-border)] p-3">
            <div className="flex items-center gap-2.5 mb-2.5">
              <div className="flex items-center justify-center size-7 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] font-heading text-xs font-bold select-none">
                {user.username?.charAt(0)?.toUpperCase() ?? "?"}
              </div>
              <span className="text-sm font-medium text-[var(--color-text-heading)] truncate">
                {user.username}
              </span>
            </div>
            <div className="space-y-0.5">
              <button
                onClick={() => {
                  onClose();
                  navigate("/profile");
                }}
                className="
                  flex items-center gap-2 w-full rounded-[var(--radius-lg)] px-3 py-1.5
                  text-sm text-[var(--color-text-muted)]
                  hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
                  transition-colors duration-[var(--duration-fast)]
                  cursor-pointer
                "
              >
                <User className="size-3.5" />
                Profile
              </button>
              <button
                onClick={handleSignOut}
                className="
                  flex items-center gap-2 w-full rounded-[var(--radius-lg)] px-3 py-1.5
                  text-sm text-[var(--color-status-error)]
                  hover:bg-[var(--color-status-error)]/10
                  transition-colors duration-[var(--duration-fast)]
                  cursor-pointer
                "
              >
                <LogOut className="size-3.5" />
                Sign out
              </button>
            </div>
          </div>
        )}
      </div>
    </>
  );
}
