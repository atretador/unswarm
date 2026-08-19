import { forwardRef, type ButtonHTMLAttributes } from "react";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "primary" | "secondary" | "ghost" | "danger";
  size?: "sm" | "md" | "lg";
  loading?: boolean;
}

const variantClasses: Record<string, string> = {
  primary: `
    bg-[var(--color-primary)] text-[var(--color-text-inverse)]
    hover:bg-[var(--color-primary-hover)]
    active:brightness-90
  `,
  secondary: `
    bg-[var(--color-bg-muted)] text-[var(--color-text)]
    border border-[var(--color-border)]
    hover:bg-[var(--color-bg-elevated)] hover:border-[var(--color-border-strong)]
    active:brightness-95
  `,
  ghost: `
    bg-transparent text-[var(--color-text-muted)]
    hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
    active:bg-[var(--color-bg-elevated)]
  `,
  danger: `
    bg-[var(--color-status-error)] text-white
    hover:brightness-110
    active:brightness-90
  `,
};

const sizeClasses: Record<string, string> = {
  sm: "h-7 px-2.5 text-xs gap-1.5 rounded-[var(--radius-md)]",
  md: "h-8 px-3 text-sm gap-2 rounded-[var(--radius-lg)]",
  lg: "h-10 px-4 text-base gap-2.5 rounded-[var(--radius-lg)]",
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  (
    {
      variant = "primary",
      size = "md",
      loading = false,
      disabled,
      className = "",
      children,
      ...props
    },
    ref,
  ) => {
    return (
      <button
        ref={ref}
        disabled={disabled || loading}
        className={`
          inline-flex items-center justify-center font-medium
          transition-all duration-[var(--duration-fast)] ease-[var(--ease-out)]
          cursor-pointer select-none
          focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
          disabled:opacity-50 disabled:cursor-not-allowed
          ${variantClasses[variant]}
          ${sizeClasses[size]}
          ${className}
        `}
        {...props}
      >
        {loading && (
          <svg
            className="animate-spin size-3.5"
            viewBox="0 0 24 24"
            fill="none"
          >
            <circle
              className="opacity-25"
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              strokeWidth="4"
            />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
        )}
        {children}
      </button>
    );
  },
);

Button.displayName = "Button";
