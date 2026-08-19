import { Box } from "lucide-react";
import { Card, EmptyState, Badge, StatusDot, Skeleton } from "../../components/ui";

const MOCK_ROWS = [
  { name: "llama-3.1-70b", family: "Llama", status: "ready" as const, speed: "42.3 tok/s" },
  { name: "mistral-large-2", family: "Mistral", status: "ready" as const, speed: "28.7 tok/s" },
  { name: "codestral-22b", family: "Mistral", status: "validating" as const, speed: "—" },
  { name: "gemma-2-27b", family: "Gemma", status: "ready" as const, speed: "55.0 tok/s" },
];

export default function Models() {
  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
            Model Registry
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Manage registered models, validation status, and benchmarks.
          </p>
        </div>
      </div>

      {/* Table preview with mock data */}
      <Card padding="none">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-[var(--color-border)]">
              <th className="text-left text-xs font-medium text-[var(--color-text-muted)] px-4 py-2.5">
                Model
              </th>
              <th className="text-left text-xs font-medium text-[var(--color-text-muted)] px-4 py-2.5">
                Family
              </th>
              <th className="text-left text-xs font-medium text-[var(--color-text-muted)] px-4 py-2.5">
                Status
              </th>
              <th className="text-left text-xs font-medium text-[var(--color-text-muted)] px-4 py-2.5">
                Speed
              </th>
            </tr>
          </thead>
          <tbody>
            {MOCK_ROWS.map((row) => (
              <tr
                key={row.name}
                className="border-b border-[var(--color-border-subtle)] last:border-0"
              >
                <td className="px-4 py-2.5 font-mono text-xs text-[var(--color-text-heading)]">
                  {row.name}
                </td>
                <td className="px-4 py-2.5 text-xs text-[var(--color-text-muted)]">
                  {row.family}
                </td>
                <td className="px-4 py-2.5">
                  <span className="inline-flex items-center gap-1.5">
                    <StatusDot status={row.status} size="sm" />
                    <Badge
                      variant={
                        row.status === "ready"
                          ? "success"
                          : row.status === "validating"
                            ? "info"
                            : "default"
                      }
                    >
                      {row.status}
                    </Badge>
                  </span>
                </td>
                <td className="px-4 py-2.5 font-mono text-xs text-[var(--color-text-muted)]">
                  {row.speed}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>

      <EmptyState
        icon={<Box className="size-12" strokeWidth={1.5} />}
        title="Full model CRUD"
        description="Create, edit, validate, and benchmark models in Phase 2."
      />

      {/* Loading skeleton demo */}
      <Card padding="md">
        <p className="text-xs font-medium text-[var(--color-text-muted)] mb-3">
          Model detail skeleton
        </p>
        <div className="space-y-2">
          <Skeleton className="h-4 w-3/4" />
          <Skeleton className="h-3 w-1/2" />
          <Skeleton className="h-3 w-2/3" />
        </div>
      </Card>
    </div>
  );
}
