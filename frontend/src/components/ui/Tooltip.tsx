import { type ReactNode, useId } from "react";

export interface TooltipProps {
  content: ReactNode;
  children: ReactNode;
  side?: "top" | "bottom" | "left" | "right";
}

export function Tooltip({ content, children, side = "top" }: TooltipProps) {
  const id = useId();
  return (
    <span className="relative group inline-flex items-center">
      <span aria-describedby={id}>{children}</span>
      <span
        role="tooltip"
        id={id}
        className={`
          pointer-events-none absolute z-50 whitespace-nowrap rounded-md
          bg-[var(--color-bg-elevated)] px-2.5 py-1 text-xs text-[var(--color-text)]
          shadow-lg border border-[var(--color-border)]
          opacity-0 scale-95 transition-all duration-150
          group-hover:opacity-100 group-hover:scale-100
          ${
            side === "top"
              ? "bottom-full left-1/2 -translate-x-1/2 mb-2"
              : side === "bottom"
                ? "top-full left-1/2 -translate-x-1/2 mt-2"
                : side === "left"
                  ? "right-full top-1/2 -translate-y-1/2 mr-2"
                  : "left-full top-1/2 -translate-y-1/2 ml-2"
          }
        `}
      >
        {content}
      </span>
    </span>
  );
}
