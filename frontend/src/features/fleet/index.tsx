import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion } from "motion/react";
import { Play, Square, RotateCw, AlertTriangle } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Badge, StatusDot, Button, Skeleton, EmptyState } from "../../components/ui";
import type { Container, ContainerStatus } from "../../lib/api/types";

const STATUS_VARIANT: Record<ContainerStatus, "success" | "info" | "error" | "warning" | "default"> = {
  running: "success",
  starting: "info",
  stopped: "default",
  stopping: "warning",
  error: "error",
};

function formatUptime(s: number): string {
  if (s === 0) return "—";
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

function formatMb(mb: number): string {
  if (mb === 0) return "—";
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb} MB`;
}

function ContainerCard({
  container,
}: {
  container: Container;
}) {
  const queryClient = useQueryClient();

  const startMutation = useMutation({
    mutationFn: () => client.startContainer(container.modelId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["containers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
    },
  });

  const stopMutation = useMutation({
    mutationFn: () => client.stopContainer(container.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["containers"] }),
  });

  const restartMutation = useMutation({
    mutationFn: () => client.restartContainer(container.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["containers"] }),
  });

  const isPending = startMutation.isPending || stopMutation.isPending || restartMutation.isPending;

  return (
    <motion.div
      layout
      initial={{ opacity: 0, scale: 0.95 }}
      animate={{ opacity: 1, scale: 1 }}
      transition={{ duration: 0.2 }}
    >
      <Card padding="md" className="flex flex-col gap-3">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <StatusDot status={container.status === "stopping" ? "stopped" : container.status} size="sm" />
            <span className="font-mono text-xs text-[var(--color-text-heading)]">
              {container.modelName}
            </span>
          </div>
          <Badge variant={STATUS_VARIANT[container.status]}>{container.status}</Badge>
        </div>

        <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-[var(--color-text-muted)]">
          <span>Container</span>
          <span className="text-right font-mono">{container.id}</span>
          <span>Port</span>
          <span className="text-right font-mono">{container.port ?? "—"}</span>
          <span>Memory</span>
          <span className="text-right font-mono">{formatMb(container.memoryMb)}</span>
          <span>CPU</span>
          <span className="text-right font-mono">{container.cpuPercent > 0 ? `${container.cpuPercent}%` : "—"}</span>
          <span>Uptime</span>
          <span className="text-right font-mono">{formatUptime(container.uptime)}</span>
        </div>

        {container.errorMessage && (
          <div className="flex items-center gap-1.5 text-[10px] text-[var(--color-status-error)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] rounded-[var(--radius-md)] px-2 py-1">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{container.errorMessage}</span>
          </div>
        )}

        <div className="flex gap-2 pt-1">
          <Button
            variant="ghost"
            size="sm"
            disabled={container.status === "running" || container.status === "starting" || isPending}
            loading={startMutation.isPending}
            onClick={() => startMutation.mutate()}
          >
            <Play className="size-3" />
            Start
          </Button>
          <Button
            variant="ghost"
            size="sm"
            disabled={container.status === "stopped" || isPending}
            loading={stopMutation.isPending}
            onClick={() => stopMutation.mutate()}
          >
            <Square className="size-3" />
            Stop
          </Button>
          <Button
            variant="ghost"
            size="sm"
            disabled={isPending}
            loading={restartMutation.isPending}
            onClick={() => restartMutation.mutate()}
          >
            <RotateCw className="size-3" />
            Restart
          </Button>
        </div>
      </Card>
    </motion.div>
  );
}

export default function Fleet() {
  const { data: containers, isLoading, error, refetch, isRefetching } = useQuery({
    queryKey: ["containers"],
    queryFn: () => client.listContainers(),
  });

  if (isLoading) {
    return (
      <div className="p-6 space-y-4 max-w-5xl">
        <Skeleton className="h-6 w-32" />
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Card key={i} padding="md">
              <Skeleton className="h-4 w-40 mb-3" />
              <Skeleton className="h-20 w-full" />
            </Card>
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-5xl">
        <EmptyState
          title="Failed to load fleet"
          description={error.message}
          action={<Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>Retry</Button>}
        />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Fleet
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Docker container lifecycle: start, stop, and monitor model containers.
        </p>
      </div>

      {containers && containers.length > 0 ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {containers.map((c) => (
            <ContainerCard key={c.id} container={c} />
          ))}
        </div>
      ) : (
        <EmptyState
          title="No containers running"
          description="Start a container from the Models panel to serve a model."
        />
      )}
    </div>
  );
}
