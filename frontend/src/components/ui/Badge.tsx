import type { ReactNode } from "react";

export interface BadgeProps {
  children: ReactNode;
  variant?: "default" | "success" | "warning" | "error" | "info" | "outline";
  size?: "sm" | "md";
  className?: string;
}

const variantClasses: Record<string, string> = {
  default: "bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]",
  success:
    "bg-[color-mix(in_srgb,var(--color-status-running)_15%,transparent)] text-[var(--color-status-running)]",
  warning:
    "bg-[color-mix(in_srgb,var(--color-status-warning)_15%,transparent)] text-[var(--color-status-warning)]",
  error:
    "bg-[color-mix(in_srgb,var(--color-status-error)_15%,transparent)] text-[var(--color-status-error)]",
  info: "bg-[var(--color-primary-soft)] text-[var(--color-primary)]",
  outline:
    "bg-transparent text-[var(--color-text-muted)] border border-[var(--color-border)]",
};

const sizeClasses: Record<string, string> = {
  sm: "h-5 px-1.5 text-[10px] gap-1 rounded-[var(--radius-sm)]",
  md: "h-6 px-2 text-xs gap-1.5 rounded-[var(--radius-md)]",
};

export function Badge({
  children,
  variant = "default",
  size = "sm",
  className = "",
}: BadgeProps) {
  return (
    <span
      className={`
        inline-flex items-center font-medium leading-none whitespace-nowrap
        ${variantClasses[variant]}
        ${sizeClasses[size]}
        ${className}
      `}
    >
      {children}
    </span>
  );
}
