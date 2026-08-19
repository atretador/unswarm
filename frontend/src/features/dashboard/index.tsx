import { LayoutDashboard } from "lucide-react";
import { Card, EmptyState, Skeleton } from "../../components/ui";

// Pre-computed bar heights for placeholder chart (avoids Math.random in render)
const BAR_HEIGHTS = [
  42, 35, 52, 58, 45, 30, 48, 62, 68, 55, 40, 38,
  44, 60, 65, 70, 58, 36, 42, 32, 28, 46, 52, 61,
];

export default function Dashboard() {
  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Stat card row — skeleton placeholders */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        {["Total requests", "Active models", "Queue depth", "Uptime"].map(
          (label) => (
            <Card key={label} padding="md">
              <p className="text-xs text-[var(--color-text-muted)] mb-1">
                {label}
              </p>
              <Skeleton className="h-7 w-20" />
            </Card>
          ),
        )}
      </div>

      <EmptyState
        icon={<LayoutDashboard className="size-12" strokeWidth={1.5} />}
        title="Dashboard"
        description="Live stats, charts, and system overview will ship in Phase 2."
      />

      {/* Chart placeholder */}
      <Card padding="lg">
        <p className="text-xs font-medium text-[var(--color-text-muted)] mb-4">
          Requests per hour
        </p>
        <div className="flex items-end gap-1 h-32">
          {BAR_HEIGHTS.map((h, i) => (
            <div
              key={i}
              className="flex-1 rounded-t bg-[var(--color-primary)] opacity-20"
              style={{ height: `${h}%` }}
            />
          ))}
        </div>
        <p className="text-[10px] text-[var(--color-text-muted)] mt-2 text-center">
          Chart rendering requires recharts (Phase 2)
        </p>
      </Card>
    </div>
  );
}
