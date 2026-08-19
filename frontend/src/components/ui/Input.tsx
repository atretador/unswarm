import { forwardRef, type InputHTMLAttributes } from "react";

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string;
  error?: string;
}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ label, error, className = "", id: externalId, ...props }, ref) => {
    const autoId = label?.toLowerCase().replace(/\s+/g, "-");
    const id = externalId ?? autoId;

    return (
      <div className="flex flex-col gap-1">
        {label && (
          <label
            htmlFor={id}
            className="text-xs font-medium text-[var(--color-text-muted)]"
          >
            {label}
          </label>
        )}
        <input
          ref={ref}
          id={id}
          className={`
            h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)]
            px-3 text-sm text-[var(--color-text)]
            border-[var(--color-border)] 
            placeholder:text-[var(--color-text-muted)]
            focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)]
            transition-colors duration-[var(--duration-fast)]
            ${error ? "border-[var(--color-status-error)]" : ""}
            ${className}
          `}
          {...props}
        />
        {error && (
          <span className="text-xs text-[var(--color-status-error)]">{error}</span>
        )}
      </div>
    );
  },
);

Input.displayName = "Input";
