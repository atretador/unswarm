import { Container } from "lucide-react";
import { Card, EmptyState, StatusDot, Badge, Button } from "../../components/ui";

const MOCK_CONTAINERS = [
  {
    model: "llama-3.1-70b",
    status: "running" as const,
    port: 8081,
    memory: "37.5 GB",
    cpu: "12.4%",
    uptime: "7d 4h",
  },
  {
    model: "mistral-large-2",
    status: "starting" as const,
    port: null,
    memory: "—",
    cpu: "—",
    uptime: "—",
  },
  {
    model: "gemma-2-27b",
    status: "stopped" as const,
    port: null,
    memory: "—",
    cpu: "—",
    uptime: "—",
  },
];

export default function Fleet() {
  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
            Fleet
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Docker container lifecycle: start, stop, and monitor model containers.
          </p>
        </div>
      </div>

      {/* Container cards */}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {MOCK_CONTAINERS.map((c) => (
          <Card key={c.model} padding="md" className="flex flex-col gap-3">
            <div className="flex items-center justify-between">
              <span className="font-mono text-xs text-[var(--color-text-heading)]">
                {c.model}
              </span>
              <span className="inline-flex items-center gap-1.5">
                <StatusDot status={c.status} size="sm" />
                <Badge
                  variant={
                    c.status === "running"
                      ? "success"
                      : c.status === "starting"
                        ? "info"
                        : "default"
                  }
                >
                  {c.status}
                </Badge>
              </span>
            </div>
            <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-[var(--color-text-muted)]">
              <span>Port</span>
              <span className="text-right font-mono">{c.port ?? "—"}</span>
              <span>Memory</span>
              <span className="text-right font-mono">{c.memory}</span>
              <span>CPU</span>
              <span className="text-right font-mono">{c.cpu}</span>
              <span>Uptime</span>
              <span className="text-right font-mono">{c.uptime}</span>
            </div>
            <div className="flex gap-2 pt-1">
              <Button
                variant="ghost"
                size="sm"
                disabled={c.status === "running"}
              >
                Start
              </Button>
              <Button
                variant="ghost"
                size="sm"
                disabled={c.status === "stopped"}
              >
                Stop
              </Button>
              <Button variant="ghost" size="sm">
                Restart
              </Button>
            </div>
          </Card>
        ))}
      </div>

      <EmptyState
        icon={<Container className="size-12" strokeWidth={1.5} />}
        title="Full fleet management"
        description="Real-time container status, lifecycle controls, and resource monitoring ship in Phase 2."
      />
    </div>
  );
}
