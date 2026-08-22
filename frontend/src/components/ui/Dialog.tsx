import {
  useEffect,
  useRef,
  useId,
  useCallback,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";

/** CSS selector for all natively focusable elements. */
const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface DialogProps {
  /** Whether the dialog is currently visible. */
  open: boolean;
  /** Called when the dialog requests to close (Escape, backdrop, or programmatic). */
  onOpenChange: (open: boolean) => void;
  /** Optional heading rendered above the children. Also used for `aria-labelledby`. */
  title?: string;
  /** Dialog body content. */
  children: ReactNode;
  /** Extra classes for the inner panel (e.g. max-width overrides). */
  className?: string;
}

/**
 * Accessible dialog primitive.
 *
 * Features:
 * - Focus trap (Tab / Shift+Tab cycles within the dialog)
 * - Focus restore on close (returns focus to whichever element was focused before open)
 * - Escape to close
 * - Click-outside (backdrop) to close
 * - Scroll lock on <body> while open
 * - `aria-modal="true"`, `role="dialog"`, `aria-labelledby` / `aria-describedby`
 * - Portaled to `document.body` via React portal
 * - Animated entry/exit via CSS transitions
 */
export function Dialog({
  open,
  onOpenChange,
  title,
  children,
  className,
}: DialogProps) {
  const titleId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  // ── Escape key ──────────────────────────────────────────────────
  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onOpenChange(false);
        return;
      }

      // Focus trap: Tab / Shift+Tab
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

  // ── Open / close lifecycle ──────────────────────────────────────
  useEffect(() => {
    if (!open) return;

    // Save the element that was focused before the dialog opened so we can
    // restore focus when it closes.
    previousFocusRef.current = document.activeElement as HTMLElement;

    // Move focus into the dialog on the next frame so the DOM has painted.
    requestAnimationFrame(() => {
      const panel = panelRef.current;
      if (!panel) return;
      // Prefer the first focusable element inside the panel, otherwise the panel itself.
      const firstFocusable = panel.querySelector<HTMLElement>(FOCUSABLE);
      (firstFocusable ?? panel).focus();
    });

    // Lock background scroll.
    const prevOverflow = document.body.style.overflow;
    const prevTouchAction = document.body.style.touchAction;
    document.body.style.overflow = "hidden";
    document.body.style.touchAction = "none";

    document.addEventListener("keydown", handleKeyDown);

    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      document.body.style.overflow = prevOverflow;
      document.body.style.touchAction = prevTouchAction;

      // Restore focus to the element that was focused before the dialog opened.
      previousFocusRef.current?.focus();
    };
  }, [open, handleKeyDown]);

  if (!open) return null;

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-end justify-center sm:items-center sm:p-6"
      role="dialog"
      aria-modal="true"
      aria-labelledby={title ? titleId : undefined}
    >
      {/* Backdrop */}
      <div
        className="absolute inset-0 bg-[var(--color-bg-overlay)] backdrop-blur-[2px]"
        onClick={() => onOpenChange(false)}
        aria-hidden="true"
      />

      {/* Panel */}
      <div
        ref={panelRef}
        tabIndex={-1}
        className={[
          "relative z-10 flex w-full flex-col overflow-hidden",
          "rounded-t-[var(--radius-2xl)] sm:rounded-[var(--radius-2xl)]",
          "border border-[var(--color-border)] bg-[var(--color-bg-surface)]",
          "shadow-xl sm:max-w-2xl",
          "max-h-[92dvh] outline-none",
          className,
        ]
          .filter(Boolean)
          .join(" ")}
      >
        {title && (
          <div className="flex items-center justify-between gap-4 border-b border-[var(--color-border-subtle)] px-5 py-4">
            <h3
              id={titleId}
              className="font-heading text-sm font-semibold text-[var(--color-text-heading)]"
            >
              {title}
            </h3>
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              aria-label="Close dialog"
              className="flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
            >
              ×
            </button>
          </div>
        )}

        <div className="flex-1 overflow-y-auto">{children}</div>
      </div>
    </div>,
    document.body,
  );
}
