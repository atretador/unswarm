import { useEffect, useRef } from "react";
import { AnimatePresence, motion } from "motion/react";
import { Button } from "./Button";

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  description?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  variant?: "danger" | "primary";
  loading?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  variant = "danger",
  loading = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const el = dialogRef.current;
    if (!el) return;
    if (open && !el.open) {
      el.showModal();
    } else if (!open && el.open) {
      el.close();
    }
  }, [open]);

  // Sync native cancel (Escape / backdrop click) back to parent
  useEffect(() => {
    const el = dialogRef.current;
    if (!el) return;
    const handleClose = () => onCancel();
    el.addEventListener("close", handleClose);
    return () => el.removeEventListener("close", handleClose);
  }, [onCancel]);

  return (
    <dialog
      ref={dialogRef}
      className="backdrop:bg-black/50 bg-transparent p-0 m-auto"
      onClick={(e) => {
        if (e.target === dialogRef.current) onCancel();
      }}
    >
      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, scale: 0.95, y: 8 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.95, y: 8 }}
            transition={{ duration: 0.15, ease: "easeOut" }}
            className="w-full max-w-sm rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] p-5 shadow-lg"
          >
            <h3 className="text-sm font-semibold text-[var(--color-text-heading)]">
              {title}
            </h3>
            {description && (
              <p className="mt-1.5 text-xs text-[var(--color-text-muted)]">
                {description}
              </p>
            )}
            <div className="mt-5 flex justify-end gap-2">
              <Button variant="secondary" size="sm" onClick={onCancel} disabled={loading}>
                {cancelLabel}
              </Button>
              <Button variant={variant} size="sm" onClick={onConfirm} loading={loading}>
                {confirmLabel}
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </dialog>
  );
}
