import { ListOrdered } from "lucide-react";
import { Card, EmptyState, Badge, StatusDot } from "../../components/ui";

export default function Queue() {
  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Queue
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Live request queue: current slot, waiting requests, and model transitions.
        </p>
      </div>

      {/* Current slot preview */}
      <Card padding="md">
        <div className="flex items-center gap-2 mb-3">
          <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            Current slot
          </span>
          <Badge variant="success">processing</Badge>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 text-xs">
          <div>
            <p className="text-[var(--color-text-muted)] mb-0.5">Model</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              llama-3.1-70b
            </p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)] mb-0.5">Tokens</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              1,247 / 4,096
            </p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)] mb-0.5">Elapsed</p>
            <p className="font-mono text-[var(--color-text-heading)]">3.4s</p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)] mb-0.5">Wait</p>
            <p className="font-mono text-[var(--color-text-heading)]">120ms</p>
          </div>
        </div>
      </Card>

      {/* Waiting items */}
      <Card padding="none">
        <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
          <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            Waiting (2)
          </span>
        </div>
        <div className="divide-y divide-[var(--color-border-subtle)]">
          {["mistral-large-2", "llama-3.1-70b"].map((model, i) => (
            <div
              key={i}
              className="flex items-center justify-between px-4 py-2.5 text-xs"
            >
              <div className="flex items-center gap-2">
                <StatusDot status="waiting" size="sm" />
                <span className="font-mono text-[var(--color-text-heading)]">
                  {model}
                </span>
              </div>
              <span className="text-[var(--color-text-muted)]">
                wait {(i + 1) * 5}s
              </span>
            </div>
          ))}
        </div>
      </Card>

      <EmptyState
        icon={<ListOrdered className="size-12" strokeWidth={1.5} />}
        title="Live queue dashboard"
        description="Real-time queue updates, model transitions, and request tracking ship in Phase 2."
      />
    </div>
  );
}
