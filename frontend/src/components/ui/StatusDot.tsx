export interface StatusDotProps {
  status: "running" | "starting" | "stopped" | "error" | "ready" | "validating" | "invalid" | "deprecated" | "waiting" | "processing";
  size?: "sm" | "md";
}

const STATUS_COLOR: Record<string, string> = {
  running: "bg-[var(--color-status-running)]",
  starting: "bg-[var(--color-status-starting)]",
  stopped: "bg-[var(--color-status-stopped)]",
  error: "bg-[var(--color-status-error)]",
  ready: "bg-[var(--color-status-running)]",
  validating: "bg-[var(--color-status-starting)]",
  invalid: "bg-[var(--color-status-error)]",
  deprecated: "bg-[var(--color-status-stopped)]",
  waiting: "bg-[var(--color-status-warning)]",
  processing: "bg-[var(--color-status-running)]",
};

const SIZE_MAP: Record<string, string> = {
  sm: "size-1.5",
  md: "size-2",
};

const PULSE_STATUS = new Set(["starting", "validating"]);

export function StatusDot({ status, size = "md" }: StatusDotProps) {
  const shouldPulse = PULSE_STATUS.has(status);
  return (
    <span className="relative inline-flex items-center justify-center" aria-label={status}>
      <span
        className={`inline-block rounded-full ${STATUS_COLOR[status]} ${SIZE_MAP[size]}`}
      />
      {shouldPulse && (
        <span
          className={`absolute inline-flex rounded-full ${STATUS_COLOR[status]} ${SIZE_MAP[size]} opacity-75`}
          style={{ animation: "pulse-ring 1.5s cubic-bezier(0, 0, 0.2, 1) infinite" }}
        />
      )}
    </span>
  );
}
