import { forwardRef, type SelectHTMLAttributes } from "react";

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label?: string;
  options: { value: string; label: string }[];
}

export const Select = forwardRef<HTMLSelectElement, SelectProps>(
  ({ label, options, className = "", id: externalId, ...props }, ref) => {
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
        <select
          ref={ref}
          id={id}
          className={`
            h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)]
            px-3 pr-8 text-sm text-[var(--color-text)]
            border-[var(--color-border)]
            focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)]
            transition-colors duration-[var(--duration-fast)]
            appearance-none cursor-pointer
            bg-[url('data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20width%3D%2216%22%20height%3D%2216%22%20viewBox%3D%220%200%2024%2024%22%20fill%3D%22none%22%20stroke%3D%22%236b7280%22%20stroke-width%3D%222%22%3E%3Cpath%20d%3D%22m6%209%206%206%206-6%22%2F%3E%3C%2Fsvg%3E')]
            bg-[position:right_0.5rem_center] bg-no-repeat
            ${className}
          `}
          {...props}
        >
          {options.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </select>
      </div>
    );
  },
);

Select.displayName = "Select";
