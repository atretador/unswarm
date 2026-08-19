export interface StatusDotProps {
  status: "running" | "starting" | "stopped" | "created" | "restarting" | "dead" | "error" | "ready" | "validating" | "invalid" | "deprecated" | "waiting" | "processing" | "registered" | "healthy" | "discovering" | "connected" | "stale" | "disconnected";
  size?: "sm" | "md";
}

const STATUS_COLOR: Record<string, string> = {
  running: "bg-[var(--color-status-running)]",
  starting: "bg-[var(--color-status-starting)]",
  created: "bg-[var(--color-status-starting)]",
  restarting: "bg-[var(--color-status-warning)]",
  stopped: "bg-[var(--color-status-stopped)]",
  dead: "bg-[var(--color-status-error)]",
  error: "bg-[var(--color-status-error)]",
  ready: "bg-[var(--color-status-running)]",
  validating: "bg-[var(--color-status-starting)]",
  invalid: "bg-[var(--color-status-error)]",
  deprecated: "bg-[var(--color-status-stopped)]",
  waiting: "bg-[var(--color-status-warning)]",
  processing: "bg-[var(--color-status-running)]",
  registered: "bg-[var(--color-status-starting)]",
  healthy: "bg-[var(--color-status-running)]",
  discovering: "bg-[var(--color-status-starting)]",
  connected: "bg-[var(--color-status-running)]",
  stale: "bg-[var(--color-status-warning)]",
  disconnected: "bg-[var(--color-status-error)]",
};

const SIZE_MAP: Record<string, string> = {
  sm: "size-1.5",
  md: "size-2",
};

const PULSE_STATUS = new Set(["starting", "validating", "registered", "discovering", "restarting", "created"]);

export function StatusDot({ status, size = "md" }: StatusDotProps) {
  const shouldPulse = PULSE_STATUS.has(status);
  return (
    <span className="relative inline-flex items-center justify-center" role="status" aria-label={status}>
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
