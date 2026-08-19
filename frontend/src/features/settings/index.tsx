import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Settings as SettingsIcon } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Skeleton, Input, Switch } from "../../components/ui";

function SchedulerPolicySection() {
  const queryClient = useQueryClient();

  const { data: settings, isLoading } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const updateMutation = useMutation({
    mutationFn: (patch: Record<string, boolean | number>) => client.updateSettings(patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings"] }),
  });

  if (isLoading || !settings) {
    return (
      <Card padding="lg">
        <Skeleton className="h-4 w-40 mb-4" />
        <div className="space-y-3">
          {Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-8 w-full" />)}
        </div>
      </Card>
    );
  }

  const toggles: Array<{
    key: string;
    label: string;
    desc: string;
    checked: boolean;
    field: string;
  }> = [
    {
      key: "auto-shutdown",
      label: "Auto-shutdown idle",
      desc: "Automatically stop containers after idle timeout",
      checked: settings.autoShutdownIdle,
      field: "autoShutdownIdle",
    },
    {
      key: "batch-drain",
      label: "Batch drain",
      desc: "Drain queue items in batches for efficiency",
      checked: settings.batchDrain,
      field: "batchDrain",
    },
    {
      key: "lazy-stop",
      label: "Lazy stop",
      desc: "Wait for in-flight requests before stopping",
      checked: settings.lazyStop,
      field: "lazyStop",
    },
    {
      key: "enable-benchmarking",
      label: "Enable benchmarking",
      desc: "Auto-benchmark models after validation",
      checked: settings.enableBenchmarking,
      field: "enableBenchmarking",
    },
  ];

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-4">
        <SettingsIcon className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          Scheduler Policy
        </p>
      </div>

      <div className="space-y-4">
        {toggles.map((t) => (
          <div key={t.key} className="flex items-center justify-between">
            <div>
              <p className="text-sm text-[var(--color-text)]">{t.label}</p>
              <p className="text-[10px] text-[var(--color-text-muted)]">{t.desc}</p>
            </div>
            <Switch
              checked={t.checked}
              onCheckedChange={(v) => updateMutation.mutate({ [t.field]: v })}
            />
          </div>
        ))}

        <div className="pt-2 border-t border-[var(--color-border-subtle)]">
          <Input
            label="Max queue depth"
            type="number"
            value={String(settings.maxQueueDepth)}
            onChange={(e) => updateMutation.mutate({ maxQueueDepth: Number(e.target.value) || 0 })}
          />
        </div>
      </div>
    </Card>
  );
}

export default function Settings() {
  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Settings
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          System configuration and preferences.
        </p>
      </div>

      {/* Theme note */}
      <Card padding="md">
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-2">
          Theme
        </p>
        <p className="text-sm text-[var(--color-text)]">
          Theme is controlled from the topbar toggle (light / dark / system).
        </p>
      </Card>

      <SchedulerPolicySection />
    </div>
  );
}
