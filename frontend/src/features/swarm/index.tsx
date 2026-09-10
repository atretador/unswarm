import {
  useState,
} from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSearchParams } from "react-router-dom";
import {
  AlertTriangle,
  KeyRound,
  Plus,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { Dialog } from "../../components/ui/Dialog";
import {
  Button,
  Card,
  Skeleton,
  EmptyState,
  Switch,
} from "../../components/ui";
import type {
  RegisteredRuntime,
} from "../../lib/api/types";
import { AgentSection } from "./AgentSection";
import { ManageRuntimesModal } from "./ManageRuntimesModal";

export type { RuntimeSignal } from "./helpers";

// ─── Add agent modal ──────────────────────────────────────────────

function AddAgentModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title="Add an agent">
      <div className="space-y-5 p-5">
        <p className="text-sm leading-relaxed text-[var(--color-text-muted)]">
          Agents run on machines where you want to serve models. Install the agent binary
          on the target machine and point it at this backend — it registers itself and
          appears here automatically.
        </p>

        <div className="space-y-2.5">
          <p className="text-xs font-medium uppercase tracking-wider text-[var(--color-text-muted)]">
            1. Run the agent
          </p>
          <div className="rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-muted)] p-3">
            <code className="block break-all font-mono text-xs text-[var(--color-text-heading)]">
              unswarm-agent --config agent.yaml
            </code>
          </div>
        </div>

        <div className="space-y-2.5">
          <p className="text-xs font-medium uppercase tracking-wider text-[var(--color-text-muted)]">
            2. Point it at this backend
          </p>
          <div className="space-y-1.5 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-muted)] p-3">
            <p className="font-mono text-xs text-[var(--color-text-muted)]">
              backend_url: <span className="text-[var(--color-text-heading)]">ws://&lt;backend-host&gt;:5014</span>
            </p>
            <p className="font-mono text-xs text-[var(--color-text-muted)]">
              agent_name: <span className="text-[var(--color-text-heading)]">machine-b</span>
            </p>
            <p className="font-mono text-xs text-[var(--color-text-muted)]">
              docker_socket: <span className="text-[var(--color-text-heading)]">unix:///var/run/docker.sock</span>
            </p>
          </div>
        </div>

        <div className="flex items-start gap-2 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-primary-soft)] p-3">
          <KeyRound className="mt-0.5 size-3.5 shrink-0 text-[var(--color-primary)]" />
          <p className="text-xs leading-relaxed text-[var(--color-text)]">
            If the backend has an <span className="font-medium">API key</span> configured,
            set <code className="font-mono">api_key</code> in agent.yaml to match — otherwise
            the agent is rejected on connect.
          </p>
        </div>

        <div className="flex justify-end">
          <Button size="sm" onClick={onClose}>
            Got it
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

// ─── Concurrency matrix modal ─────────────────────────────────────

function ConcurrencyModal({
  agentName,
  open,
  onClose,
  registered,
}: {
  agentName: string;
  open: boolean;
  onClose: () => void;
  registered: RegisteredRuntime[];
}) {
  const queryClient = useQueryClient();
  const agentRcs = registered.filter((rc) => rc.agent === agentName);
  const [toggleError, setToggleError] = useState<string | null>(null);

  // Track pending toggle keys to disable the whole matrix while in-flight.
  const [pendingKey, setPendingKey] = useState<string | null>(null);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
    queryClient.invalidateQueries({ queryKey: ["agents"] });
  };

  /** Check if runtime A's list contains B (by displayName or image, case-insensitive). */
  const isCompatible = (a: RegisteredRuntime, b: RegisteredRuntime): boolean => {
    const lowerList = a.canRunAlongWith.map((n) => n.toLowerCase());
    return lowerList.includes(b.displayName.toLowerCase()) || lowerList.includes(b.image.toLowerCase());
  };

  const toggleCell = async (a: RegisteredRuntime, b: RegisteredRuntime) => {
    setToggleError(null);
    const currentlyOn = isCompatible(a, b);
    const key = `${a.id}:${b.id}`;
    setPendingKey(key);
    try {
      await client.toggleRuntimeConcurrency({
        runtimeAId: a.id,
        runtimeBId: b.id,
        canRunAlongWith: !currentlyOn,
      });
      invalidate();
    } catch (err) {
      setToggleError(err instanceof Error ? err.message : "Failed to update concurrency");
    } finally {
      setPendingKey(null);
    }
  };

  const busy = pendingKey !== null;

  /** Build the label lines for an axis header. */
  function axisLabel(rc: RegisteredRuntime) {
    const models = rc.discoveredModels.map((m) => m.name);
    const sub = models.length > 0
      ? models.join(" · ")
      : rc.runtimeKind === "script"
        ? (rc.launcherPath ?? "script")
        : rc.image;
    return { primary: rc.displayName, secondary: sub };
  }

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title={`Concurrency on ${agentName}`}>
      <div className="p-5">
        {agentRcs.length === 0 ? (
          <p className="text-xs text-[var(--color-text-muted)]">
            No runtimes registered on this agent yet.
          </p>
        ) : (
          <>
            {/* Legend */}
            <p className="mb-3 text-[11px] leading-relaxed text-[var(--color-text-muted)]">
              Toggle which runtimes may share resources on this agent. Each row/column is
              a registered runtime — turning a cell ON allows both to run at the same time.
              An empty row (all OFF) means the runtime runs alone.
            </p>

            {/* Matrix scroll wrapper */}
            <div className="overflow-x-auto rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)]">
              <table className="w-full border-collapse text-[11px]">
                <thead>
                  <tr>
                    {/* Empty top-left corner */}
                    <th className="sticky left-0 z-10 border-b border-r border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-2 py-2" />
                    {agentRcs.map((colRc) => {
                      const { primary, secondary } = axisLabel(colRc);
                      return (
                        <th
                          key={colRc.id}
                          className="min-w-[72px] border-b border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-2 py-2 text-center"
                        >
                          <p className="truncate font-mono text-[10px] font-medium text-[var(--color-text-heading)]" title={primary}>
                            {primary}
                          </p>
                          <p className="mt-0.5 truncate text-[9px] text-[var(--color-text-muted)]" title={secondary}>
                            {secondary}
                          </p>
                        </th>
                      );
                    })}
                  </tr>
                </thead>
                <tbody>
                  {agentRcs.map((rowRc) => {
                    const { primary, secondary } = axisLabel(rowRc);
                    return (
                      <tr key={rowRc.id}>
                        {/* Row header (sticky first column) */}
                        <th className="sticky left-0 z-10 border-r border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)] px-2 py-2 text-left">
                          <p className="truncate font-mono text-[10px] font-medium text-[var(--color-text-heading)]" title={primary}>
                            {primary}
                          </p>
                          <p className="mt-0.5 truncate text-[9px] text-[var(--color-text-muted)]" title={secondary}>
                            {secondary}
                          </p>
                        </th>
                        {agentRcs.map((colRc) => {
                          const isDiag = rowRc.id === colRc.id;
                          const checked = isDiag || isCompatible(rowRc, colRc);
                          const cellDisabled = busy && pendingKey === `${rowRc.id}:${colRc.id}`;

                          if (isDiag) {
                            return (
                              <td
                                key={colRc.id}
                                className="border-b border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-2 py-2 text-center opacity-40"
                              >
                                <span className="inline-block size-4 rounded-full bg-[var(--color-border-strong)]" />
                              </td>
                            );
                          }

                          return (
                            <td
                              key={colRc.id}
                              className="border-b border-[var(--color-border-subtle)] px-2 py-2 text-center"
                            >
                              <Switch
                                checked={checked}
                                disabled={cellDisabled}
                                onCheckedChange={() => toggleCell(rowRc, colRc)}
                                aria-label={`${rowRc.displayName} with ${colRc.displayName}`}
                              />
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            {/* Runs-alone hint */}
            {agentRcs.some((rc) => rc.canRunAlongWith.length === 0) && (
              <p className="mt-3 text-[10px] text-[var(--color-text-muted)]">
                Runtimes with all cells off run independently and will not share resources.
              </p>
            )}
          </>
        )}

        {toggleError && (
          <div className="mt-3 flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{toggleError}</span>
          </div>
        )}
      </div>
    </Dialog>
  );
}


// ─── Main Swarm page ──────────────────────────────────────────────

export default function Swarm() {
  const [manageAgent, setManageAgent] = useState<string | null>(null);
  const [concurrencyAgent, setConcurrencyAgent] = useState<string | null>(null);
  const [showAddAgent, setShowAddAgent] = useState(false);
  const [expandedAgents, setExpandedAgents] = useState<Set<string>>(new Set(["host"]));
  const [searchParams] = useSearchParams();
  const focusContainerId = searchParams.get("focus");

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const anyExpanded = expandedAgents.size > 0;
  const pollInterval = anyExpanded
    ? (settings?.telemetryPollInterval ?? 10) * 1000
    : 30_000;

  const {
    data: agents,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["agents"],
    queryFn: () => client.listAgents(),
    refetchInterval: pollInterval,
  });

  const { data: registeredContainers } = useQuery({
    queryKey: ["registered-containers"],
    queryFn: () => client.listRegisteredRuntimes(),
  });

  if (isLoading) {
    return (
      <div className="max-w-5xl space-y-4 p-6">
        <Skeleton className="h-7 w-40" />
        <Skeleton className="h-4 w-72" />
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Card key={i} padding="md">
              <Skeleton className="mb-3 h-4 w-40" />
              <Skeleton className="h-20 w-full" />
            </Card>
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="max-w-5xl p-6">
        <EmptyState
          title="Failed to load swarm"
          description={error.message}
          action={
            <Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  // "host" agent always first, then remote agents alphabetically.
  const sortedAgents = [...(agents ?? [])].sort((a, b) => {
    if (a.name === "host") return -1;
    if (b.name === "host") return 1;
    return a.name.localeCompare(b.name);
  });

  const hasAgents = sortedAgents.length > 0;

  return (
    <div className="max-w-5xl space-y-6 p-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Swarm</h2>
          <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
            Manage registered runtimes and run model discovery across agents.
          </p>
        </div>
        <Button size="sm" variant="secondary" onClick={() => setShowAddAgent(true)}>
          <Plus className="size-3.5" />
          Add agent
        </Button>
      </div>

      {!hasAgents ? (
        <EmptyState
          title="No agents connected"
          description="Run the agent binary on a machine with Docker to add it to the swarm."
          action={
            <Button size="sm" onClick={() => setShowAddAgent(true)}>
              <Plus className="size-3.5" />
              Add agent
            </Button>
          }
        />
      ) : (
        <div className="divide-y divide-[var(--color-border-subtle)]">
          {sortedAgents.map((agent) => (
            <AgentSection
              key={agent.name}
              agent={agent}
              registeredContainers={registeredContainers ?? []}
              defaultExpanded={agent.name === "host"}
              focusContainerId={focusContainerId}
              onManage={(name) => setManageAgent(name)}
              onConcurrency={(name) => setConcurrencyAgent(name)}
              settings={settings}
              onExpandedChange={(expanded) => {
                setExpandedAgents(prev => {
                  const next = new Set(prev);
                  if (expanded) next.add(agent.name);
                  else next.delete(agent.name);
                  return next;
                });
              }}
            />
          ))}
        </div>
      )}

      {/* Modals */}
      <ManageRuntimesModal
        agentName={manageAgent ?? ""}
        open={manageAgent !== null}
        onClose={() => setManageAgent(null)}
        registered={registeredContainers ?? []}
      />
      <ConcurrencyModal
        agentName={concurrencyAgent ?? ""}
        open={concurrencyAgent !== null}
        onClose={() => setConcurrencyAgent(null)}
        registered={registeredContainers ?? []}
      />
      <AddAgentModal open={showAddAgent} onClose={() => setShowAddAgent(false)} />
    </div>
  );
}
