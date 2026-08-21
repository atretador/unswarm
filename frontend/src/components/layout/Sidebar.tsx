import { ChevronLeft, ChevronRight } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { Tooltip } from "../ui/Tooltip";
import { Logo } from "../ui/Logo";
import { NAV_ITEMS } from "../../lib/nav-items";

export interface SidebarProps {
  collapsed: boolean;
  onToggle: () => void;
}

export function Sidebar({ collapsed, onToggle }: SidebarProps) {
  const location = useLocation();

  return (
    <aside
      className={`
        hidden lg:flex flex-col
        border-r border-[var(--color-border)]
        bg-[var(--color-bg-surface)]
        transition-all duration-[var(--duration-slow)] ease-[var(--ease-out)]
        ${collapsed ? "w-[var(--sidebar-collapsed)]" : "w-[var(--sidebar-width)]"}
      `}
    >
      {/* Logo */}
      <div
        className={`
          flex items-center h-[var(--topbar-height)] border-b border-[var(--color-border)]
          ${collapsed ? "justify-center px-2" : "px-4"}
        `}
      >
        {!collapsed && (
          <div className="flex items-center gap-2.5 min-w-0">
            <Logo size={28} />
            <span className="font-heading text-base font-semibold text-[var(--color-text-heading)] tracking-tight">
              unswarm
            </span>
          </div>
        )}
        {collapsed && <Logo size={26} />}
      </div>

      {/* Nav */}
      <nav className="flex-1 py-2 px-1.5 space-y-0.5 overflow-y-auto">
        {NAV_ITEMS.map(({ to, icon: Icon, label }) => {
          const isActive =
            to === "/" ? location.pathname === "/" : location.pathname.startsWith(to);

          const link = (
            <NavLink
              key={to}
              to={to}
              className={`
                flex items-center gap-2.5 rounded-[var(--radius-lg)] px-2.5 py-1.5
                text-sm font-medium transition-colors duration-[var(--duration-fast)]
                ${
                  isActive
                    ? "bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
                    : "text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
                }
                ${collapsed ? "justify-center" : ""}
              `}
            >
              <Icon className="size-4 shrink-0" strokeWidth={isActive ? 2 : 1.5} />
              {!collapsed && <span>{label}</span>}
            </NavLink>
          );

          if (collapsed) {
            return (
              <Tooltip key={to} content={label} side="right">
                {link}
              </Tooltip>
            );
          }

          return link;
        })}
      </nav>

      {/* Collapse toggle */}
      <div className="p-1.5 border-t border-[var(--color-border)]">
        <button
          onClick={onToggle}
          className={`
            flex items-center justify-center w-full rounded-[var(--radius-lg)]
            py-1.5 text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)]
            transition-colors duration-[var(--duration-fast)]
            cursor-pointer
          `}
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        >
          {collapsed ? (
            <ChevronRight className="size-4" />
          ) : (
            <ChevronLeft className="size-4" />
          )}
        </button>
      </div>
    </aside>
  );
}
