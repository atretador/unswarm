import { type ReactNode, useId } from "react";

export interface TooltipProps {
  content: ReactNode;
  children: ReactNode;
  side?: "top" | "bottom" | "left" | "right";
}

const sideClasses: Record<string, string> = {
  top: "bottom-full left-1/2 -translate-x-1/2 mb-2",
  bottom: "top-full left-1/2 -translate-x-1/2 mt-2",
  left: "right-full top-1/2 -translate-y-1/2 mr-2",
  right: "left-full top-1/2 -translate-y-1/2 ml-2",
};

export function Tooltip({ content, children, side = "top" }: TooltipProps) {
  const id = useId();
  return (
    <span className="relative group/focus group inline-flex items-center">
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
          group-focus-within/focus:opacity-100 group-focus-within/focus:scale-100
          ${sideClasses[side]}
        `}
      >
        {content}
      </span>
    </span>
  );
}
