import { useEffect, useRef, type ReactNode } from "react";
import { AnimatePresence, motion } from "motion/react";

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  /** Max width of the modal panel */
  maxWidth?: string;
}

/**
 * Reusable modal wrapper extracted from the ConfirmDialog pattern.
 * Uses native `<dialog>` with showModal() for proper centering and backdrop,
 * AnimatePresence + motion for scale-fade entry, Escape/backdrop close.
 */
export function Modal({
  open,
  onClose,
  children,
  maxWidth = "max-w-md",
}: ModalProps) {
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
    const handleClose = () => onClose();
    el.addEventListener("close", handleClose);
    return () => el.removeEventListener("close", handleClose);
  }, [onClose]);

  return (
    <dialog
      ref={dialogRef}
      className="backdrop:bg-black/50 bg-transparent p-0 m-auto"
      onClick={(e) => {
        if (e.target === dialogRef.current) onClose();
      }}
    >
      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, scale: 0.95, y: 8 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.95, y: 8 }}
            transition={{ duration: 0.15, ease: "easeOut" }}
            className={`w-full ${maxWidth} rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] p-5 shadow-lg`}
          >
            {children}
          </motion.div>
        )}
      </AnimatePresence>
    </dialog>
  );
}
