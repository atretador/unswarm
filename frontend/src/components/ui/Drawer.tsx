import {
  useEffect,
  useRef,
  useId,
  useCallback,
  useState,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";

/** CSS selector for all natively focusable elements. */
const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** How long the slide/fade transition runs (ms) — keep in sync with the classes below. */
const TRANSITION_MS = 200;

/**
 * Double-requestAnimationFrame (or setTimeout fallback in environments without
 * rAF, e.g. some jsdom configs) so the browser paints the off-canvas position
 * before the transform flips and the CSS transition plays.
 */
function nextFrame(cb: () => void): void {
  if (typeof window !== "undefined" && typeof window.requestAnimationFrame === "function") {
    window.requestAnimationFrame(() => window.requestAnimationFrame(cb));
  } else {
    window.setTimeout(cb, 0);
  }
}

export interface DrawerProps {
  /** Whether the drawer is currently visible. */
  open: boolean;
  /** Called when the drawer requests to close (Escape, backdrop, or programmatic). */
  onOpenChange: (open: boolean) => void;
  /** Heading rendered in the drawer header. Also used for `aria-labelledby`. */
  title?: string;
  /** Optional muted line under the title (e.g. model metadata). */
  subtitle?: ReactNode;
  /** Scrollable drawer body content. */
  children: ReactNode;
  /** Optional pinned footer (e.g. a chat composer). */
  footer?: ReactNode;
  /** Extra classes for the panel (e.g. width overrides). */
  className?: string;
}

/**
 * Right-side slide-over drawer primitive.
 *
 * Features:
 * - Focus trap (Tab / Shift+Tab cycles within the drawer)
 * - Focus restore on close (returns focus to whichever element was focused before open)
 * - Escape to close, backdrop click to close
 * - Scroll lock on <body> while open
 * - `role="dialog"`, `aria-modal="true"`, `aria-labelledby`
 * - Portaled to `document.body` via React portal
 * - Animated slide-in from the right edge AND animated exit before unmount
 *
 * Unlike {@link Dialog}, the panel itself receives initial focus (children may
 * claim focus themselves, e.g. an auto-focused composer input).
 */
export function Drawer({
  open,
  onOpenChange,
  title,
  subtitle,
  children,
  footer,
  className,
}: DrawerProps) {
  const titleId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const wasOpenRef = useRef(false);

  // Mount/unmount with exit animation: keep rendering briefly after close.
  const [visible, setVisible] = useState(open);
  // Slide position: false = off-canvas right, true = docked.
  const [entered, setEntered] = useState(false);

  // ── Open / close lifecycle ──────────────────────────────────────
  useEffect(() => {
    if (open && !wasOpenRef.current) {
      // ── Opening ────────────────────────────────────────────────
      wasOpenRef.current = true;
      previousFocusRef.current = document.activeElement as HTMLElement;
      setVisible(true);
      // Flip transform after a paint so the CSS transition plays.
      nextFrame(() => {
        setEntered(true);
        const panel = panelRef.current;
        if (panel && !panel.contains(document.activeElement)) panel.focus();
      });
      document.body.style.overflow = "hidden";
      document.body.style.touchAction = "none";
    } else if (!open && wasOpenRef.current) {
      // ── Closing ────────────────────────────────────────────────
      wasOpenRef.current = false;
      setEntered(false);
      window.setTimeout(() => setVisible(false), TRANSITION_MS);
      document.body.style.overflow = "";
      document.body.style.touchAction = "";
      previousFocusRef.current?.focus();
    }
  }, [open]);

  // ── Escape key + focus trap ─────────────────────────────────────
  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onOpenChange(false);
        return;
      }

      if (e.key !== "Tab" || !panelRef.current) return;
      const focusable = panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];

      if (e.shiftKey) {
        if (document.activeElement === first) {
          e.preventDefault();
          last.focus();
        }
      } else if (document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    },
    [onOpenChange],
  );

  useEffect(() => {
    if (!visible) return;
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [visible, handleKeyDown]);

  if (!visible && !open) return null;

  return createPortal(
    <div
      className="fixed inset-0 z-50"
      role="dialog"
      aria-modal="true"
      aria-labelledby={title ? titleId : undefined}
    >
      {/* Backdrop */}
      <div
        aria-hidden="true"
        onClick={() => onOpenChange(false)}
        className={[
          "absolute inset-0 bg-[var(--color-bg-overlay)] backdrop-blur-[2px]",
          "transition-opacity duration-200",
          entered ? "opacity-100" : "opacity-0",
        ].join(" ")}
      />

      {/* Panel — slides in from the right edge, full height */}
      <div
        ref={panelRef}
        tabIndex={-1}
        className={[
          "absolute right-0 top-0 flex h-full w-full flex-col overflow-hidden outline-none",
          "max-w-lg border-l border-[var(--color-border)] bg-[var(--color-bg-surface)]",
          "shadow-xl transition-transform duration-200 ease-out",
          className,
        ]
          .filter(Boolean)
          .join(" ")}
        style={{ transform: entered ? "translateX(0)" : "translateX(100%)" }}
      >
        {(title || subtitle) && (
          <div className="flex items-start justify-between gap-3 border-b border-[var(--color-border-subtle)] px-5 py-4">
            <div className="min-w-0">
              {title && (
                <h3
                  id={titleId}
                  className="truncate font-heading text-sm font-semibold text-[var(--color-text-heading)]"
                >
                  {title}
                </h3>
              )}
              {subtitle && (
                <div className="mt-0.5 text-xs text-[var(--color-text-muted)]">
                  {subtitle}
                </div>
              )}
            </div>
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              aria-label="Close drawer"
              className="flex size-7 shrink-0 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
            >
              ×
            </button>
          </div>
        )}

        <div className="min-h-0 flex-1 overflow-y-auto">{children}</div>

        {footer && (
          <div className="border-t border-[var(--color-border-subtle)]">{footer}</div>
        )}
      </div>
    </div>,
    document.body,
  );
}
