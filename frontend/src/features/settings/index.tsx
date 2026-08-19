import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion } from "motion/react";
import { Key, Trash2, Plus, Settings as SettingsIcon } from "lucide-react";
import { client } from "../../lib/query-client";
import type { ApiKey } from "../../lib/api/types";
import { Card, Button, Skeleton, Input, Switch } from "../../components/ui";

function formatRelative(date: string | null): string {
  if (!date) return "never";
  const d = new Date(date);
  const now = new Date();
  const diff = now.getTime() - d.getTime();
  if (diff < 60000) return "just now";
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`;
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`;
  return `${Math.floor(diff / 86400000)}d ago`;
}

function ApiKeysSection() {
  const queryClient = useQueryClient();
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState("");

  const { data: keys, isLoading } = useQuery({
    queryKey: ["apiKeys"],
    queryFn: () => client.listApiKeys(),
  });

  const createMutation = useMutation({
    mutationFn: () => client.createApiKey({ name: newName, permissions: ["models:read", "proxy:access"] }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["apiKeys"] });
      setNewName("");
      setShowCreate(false);
    },
  });

  const revokeMutation = useMutation({
    mutationFn: (id: string) => client.revokeApiKey(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["apiKeys"] }),
  });

  return (
    <Card padding="lg">
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-2">
          <Key className="size-4 text-[var(--color-text-muted)]" />
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            API Keys
          </p>
        </div>
        <Button size="sm" variant="ghost" onClick={() => setShowCreate((p) => !p)}>
          <Plus className="size-3" />
          Create
        </Button>
      </div>

      {showCreate && (
        <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }}>
          <div className="flex items-end gap-2 mb-4">
            <Input
              label="Key name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="my-api-key"
            />
            <Button
              size="sm"
              onClick={() => createMutation.mutate()}
              loading={createMutation.isPending}
              disabled={!newName}
            >
              Create
            </Button>
          </div>
        </motion.div>
      )}

      {isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 2 }, (_, i) => <Skeleton key={i} className="h-10 w-full" />)}
        </div>
      ) : keys && keys.length > 0 ? (
        <div className="space-y-2">
          {keys.map((key: ApiKey) => (
            <div key={key.id} className="flex items-center justify-between px-3 py-2 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)]">
              <div className="flex items-center gap-3">
                <div>
                  <p className="text-sm font-medium text-[var(--color-text-heading)]">{key.name}</p>
                  <p className="text-[10px] text-[var(--color-text-muted)] font-mono">
                    {key.keyPrefix}... · {key.permissions.length} perms · {key.rateLimit ? `${key.rateLimit}/min` : "no limit"}
                  </p>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <span className="text-[10px] text-[var(--color-text-muted)]">used {formatRelative(key.lastUsedAt)}</span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => revokeMutation.mutate(key.id)}
                  loading={revokeMutation.isPending}
                >
                  <Trash2 className="size-3" />
                </Button>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-[var(--color-text-muted)]">No API keys yet.</p>
      )}
    </Card>
  );
}

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
          System configuration, API keys, and preferences.
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

      <ApiKeysSection />
      <SchedulerPolicySection />
    </div>
  );
}
