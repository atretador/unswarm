import { forwardRef, type ButtonHTMLAttributes } from "react";

export interface SwitchProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "onChange"> {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  label?: string;
}

export const Switch = forwardRef<HTMLButtonElement, SwitchProps>(
  ({ checked, onCheckedChange, label, className = "", ...props }, ref) => {
    return (
      <label className="inline-flex items-center gap-2 cursor-pointer select-none">
        <button
          ref={ref}
          role="switch"
          aria-checked={checked}
          type="button"
          onClick={() => onCheckedChange(!checked)}
          className={`
            relative inline-flex h-5 w-9 shrink-0 rounded-full
            transition-colors duration-[var(--duration-fast)]
            ${
              checked
                ? "bg-[var(--color-primary)]"
                : "bg-[var(--color-border-strong)]"
            }
            focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
            cursor-pointer
            ${className}
          `}
          {...props}
        >
          <span
            className={`
              pointer-events-none inline-block size-4 rounded-full bg-white shadow-sm
              transition-transform duration-[var(--duration-fast)] ease-[var(--ease-spring)]
              ${checked ? "translate-x-4" : "translate-x-0.5"}
            `}
          />
        </button>
        {label && (
          <span className="text-sm text-[var(--color-text)]">{label}</span>
        )}
      </label>
    );
  },
);

Switch.displayName = "Switch";
