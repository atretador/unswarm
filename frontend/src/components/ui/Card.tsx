import type { HTMLAttributes, ReactNode } from "react";

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode;
  padding?: "none" | "sm" | "md" | "lg";
}

const padMap: Record<string, string> = {
  none: "",
  sm: "p-3",
  md: "p-4",
  lg: "p-6",
};

export function Card({
  children,
  padding = "md",
  className = "",
  ...props
}: CardProps) {
  return (
    <div
      className={`
        rounded-[var(--radius-xl)] border border-[var(--color-border)]
        bg-[var(--color-bg-surface)]
        ${padMap[padding]}
        ${className}
      `}
      {...props}
    >
      {children}
    </div>
  );
}
