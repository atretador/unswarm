import {
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { useSearchParams } from "react-router-dom";
import {
  AlertTriangle,
  Box,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Cpu,
  FileCode,
  Grid2x2,
  Gauge,
  Hash,
  KeyRound,
  MemoryStick,
  Monitor,
  PackageOpen,
  Pencil,
  Play,
  Plus,
  RefreshCw,
  RotateCw,
  Search,
  Server,
  Square,
  Terminal,
  Trash2,
  X,
  Zap,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { Dialog } from "../../components/ui/Dialog";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Skeleton,
  EmptyState,
  Input,
  Switch,
  Tooltip,
} from "../../components/ui";
import type {
  Agent,
  AgentAvailableScript,
  AgentScriptStatus,
  Container,
  ContainerRegistrationStatus,
  Model,
  RegisteredRuntime,
  Settings,
  UpdateRuntimePayload,
} from "../../lib/api/types";

// ─── Status semantics ─────────────────────────────────────────────

const REG_STATUS_VARIANT: Record<ContainerRegistrationStatus, "success" | "info" | "error" | "warning" | "default"> = {
  registered: "info",
  starting: "info",
  healthy: "success",
  discovering: "info",
  ready: "success",
  error: "error",
};

type AgentConnectivity = "connected" | "stale" | "disconnected";

const AGENT_STATUS_VARIANT: Record<AgentConnectivity, "success" | "warning" | "error"> = {
  connected: "success",
  stale: "warning",
  disconnected: "error",
};

const REG_TRANSITIONAL = new Set<ContainerRegistrationStatus>([
  "registered",
  "starting",
  "healthy",
  "discovering",
]);

// ─── Runtime container status (docker telemetry) ─────────────────

/** Runtime statuses that map to the yellow (transitional) dot. */
const RUNTIME_TRANSITIONAL = new Set(["starting", "stopping", "created", "restarting"]);

/** Runtime statuses that map to the red (down) dot. */
const RUNTIME_DOWN = new Set(["stopped", "exited", "dead", "error"]);

export type RuntimeSignal = "running" | "transitional" | "down" | "unknown";

function runtimeSignal(status: string | null | undefined): RuntimeSignal {
  const s = (status ?? "").toLowerCase();
  if (s === "running") return "running";
  if (RUNTIME_TRANSITIONAL.has(s)) return "transitional";
  if (RUNTIME_DOWN.has(s)) return "down";
  return "unknown";
}

const RUNTIME_LABEL: Record<RuntimeSignal, string> = {
  running: "Running",
  transitional: "Starting…",
  down: "Stopped",
  unknown: "Unknown",
};

/** Find the runtime telemetry status for a registered container on its agent. */
function runtimeStatusFor(
  agentContainers: Agent["containers"],
  rc: RegisteredRuntime,
): string | null {
  const id = rc.runtimeContainerId?.toLowerCase();
  const image = rc.image.toLowerCase();
  const match = agentContainers.find(
    (t) =>
      (id !== undefined && t.containerId.toLowerCase() === id) ||
      (t.modelName ?? "").toLowerCase() === image,
  );
  return match?.status ?? null;
}

/** Find the runtime telemetry status for a registered script on its agent (matched by path). */
function runtimeStatusForScript(
  agentScripts: AgentScriptStatus[],
  rc: RegisteredRuntime,
): string | null {
  const path = rc.launcherPath?.toLowerCase();
  if (!path) return null;
  const match = agentScripts.find((s) => s.path.toLowerCase() === path);
  return match?.status ?? null;
}

// ─── Formatting helpers ───────────────────────────────────────────

function formatMb(mb: number): string {
  if (!mb) return "—";
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb} MB`;
}

function formatOsPlatform(platform: string): string {
  const lower = platform.toLowerCase();
  if (lower.includes("linux")) return "Linux";
  if (lower.includes("windows")) return "Windows";
  if (lower.includes("darwin") || lower.includes("mac")) return "macOS";
  return platform;
}

function OsIcon({ platform, className }: { platform: string; className?: string }) {
  const lower = platform.toLowerCase();
  if (lower.includes("linux")) {
    // Penguin
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="8" r="5" />
        <path d="M7 13.5c0 2.5 2 5 5 5s5-2.5 5-5" />
        <circle cx="10" cy="7" r="1" fill="currentColor" stroke="none" />
        <circle cx="14" cy="7" r="1" fill="currentColor" stroke="none" />
        <path d="M11 9.5h2" />
        <path d="M8.5 2.5L7 5" />
        <path d="M15.5 2.5L17 5" />
      </svg>
    );
  }
  if (lower.includes("windows")) {
    // Windows logo
    return (
      <svg className={className} viewBox="0 0 24 24" fill="currentColor">
        <path d="M3 5.5L10.5 4.5V11.5H3V5.5Z" />
        <path d="M11.5 4.3L21 3V11.5H11.5V4.3Z" />
        <path d="M3 12.5H10.5V19.5L3 18.5V12.5Z" />
        <path d="M11.5 12.5H21V21L11.5 19.7V12.5Z" />
      </svg>
    );
  }
  if (lower.includes("darwin") || lower.includes("mac")) {
    // Apple logo
    return (
      <svg className={className} viewBox="0 0 24 24" fill="currentColor">
        <path d="M18.71 19.5C17.88 20.74 17 21.95 15.66 21.97C14.32 22 13.89 21.18 12.37 21.18C10.84 21.18 10.37 21.95 9.1 22C7.79 22.05 6.8 20.68 5.96 19.47C4.25 16.56 2.93 11.3 4.7 7.72C5.57 5.94 7.36 4.86 9.28 4.84C10.56 4.81 11.78 5.72 12.57 5.72C13.36 5.72 14.85 4.62 16.4 4.8C17.06 4.83 18.87 5.06 20.01 6.77C19.88 6.84 17.75 8.07 17.78 10.62C17.81 13.67 20.47 14.7 20.5 14.71C20.47 14.79 20.07 16.19 18.71 19.5ZM13 3.5C13.73 2.67 14.94 2.04 15.94 2C16.07 3.17 15.6 4.35 14.9 5.19C14.21 6.04 13.07 6.7 11.95 6.61C11.8 5.46 12.36 4.26 13 3.5Z" />
      </svg>
    );
  }
  return <Monitor className={className} />;
}

function agentConnectivity(agent: Agent): AgentConnectivity {
  // The backend's isConnected flag is authoritative for the connected state —
  // lastSeen may be null on a freshly-connected agent.
  if (agent.isConnected) return "connected";
  if (!agent.lastSeen) return "disconnected";
  const ageMs = Date.now() - new Date(agent.lastSeen).getTime();
  if (ageMs < 120_000) return "stale";
  return "disconnected";
}

function relativeTime(iso: string | null): string {
  if (!iso) return "—";
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) return "just now";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`;
  return new Date(iso).toLocaleDateString();
}

/** A container counts as "already registered" when name/id matches image or runtimeContainerId (case-insensitive, matching backend OrdinalIgnoreCase). */
function isContainerRegistered(rcs: RegisteredRuntime[], agentName: string, c: Container): boolean {
  const name = c.modelName.toLowerCase();
  const id = c.id.toLowerCase();
  return rcs.some(
    (rc) =>
      rc.agent.toLowerCase() === agentName.toLowerCase() &&
      (rc.image.toLowerCase() === name ||
        rc.image.toLowerCase() === id ||
        rc.runtimeContainerId?.toLowerCase() === id),
  );
}

/** Slugify a container name into a sane display name. */
function displayNameFromContainer(c: Container): string {
  return (
    c.modelName
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 32) || "container"
  );
}

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

// ─── Manage runtimes modal ──────────────────────────────────────

const PAGE_SIZE = 9;

type ManageTab = "containers" | "scripts";

// ─── Edit runtime dialog ────────────────────────────────────────

function EditRuntimeDialog({
  runtime,
  open,
  onClose,
}: {
  runtime: RegisteredRuntime | null;
  open: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [displayName, setDisplayName] = useState(runtime?.displayName ?? "");

  // Sync displayName when dialog opens or runtime changes
  const prevOpenRef = useRef(false);
  useEffect(() => {
    if (open && !prevOpenRef.current) {
      setDisplayName(runtime?.displayName ?? "");
    }
    prevOpenRef.current = open;
  }, [open, runtime]);

  const updateMutation = useMutation({
    mutationFn: (payload: UpdateRuntimePayload) =>
      client.updateRuntime(runtime!.id, payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      onClose();
    },
  });

  // Reset mutation state when dialog opens
  useEffect(() => {
    if (open) updateMutation.reset();
  }, [open]);

  const handleSave = () => {
    if (!runtime || !displayName.trim()) return;
    updateMutation.mutate({ displayName: displayName.trim() });
  };

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title="Edit runtime">
      <div className="space-y-4 p-5">
        <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
          Update the display name for{" "}
          <span className="font-mono text-[var(--color-text-heading)]">{runtime?.displayName}</span>.
        </p>

        <Input
          label="Display name"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          placeholder="my-runtime"
          aria-label="Display name"
          autoFocus
        />

        {updateMutation.isError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{updateMutation.error.message}</span>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            size="sm"
            loading={updateMutation.isPending}
            disabled={!displayName.trim() || displayName.trim() === runtime?.displayName}
            onClick={handleSave}
          >
            Save
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

function ManageRuntimesModal({
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
  const [activeTab, setActiveTab] = useState<ManageTab>("containers");
  const queryClient = useQueryClient();
  // Remount the body whenever the modal opens (or targets a different agent)
  // so filter/page/selection always start fresh.
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title={`Manage runtimes on ${agentName}`}>
      {/* Tab bar */}
      <div className="flex items-center justify-between border-b border-[var(--color-border-subtle)]">
        <div className="flex" role="tablist" aria-label="Runtimes">
          <button
            type="button"
            role="tab"
            id="fleet-tab-containers"
            aria-selected={activeTab === "containers"}
            aria-controls="fleet-panel-containers"
            onClick={() => setActiveTab("containers")}
            className={`
              flex items-center gap-1.5 px-4 py-2.5 text-xs font-medium transition-colors
              ${
                activeTab === "containers"
                  ? "border-b-2 border-[var(--color-primary)] text-[var(--color-primary)]"
                  : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
              }
            `}
          >
            <PackageOpen className="size-3" />
            Containers
          </button>
          <button
            type="button"
            role="tab"
            id="fleet-tab-scripts"
            aria-selected={activeTab === "scripts"}
            aria-controls="fleet-panel-scripts"
            onClick={() => setActiveTab("scripts")}
            className={`
              flex items-center gap-1.5 px-4 py-2.5 text-xs font-medium transition-colors
              ${
                activeTab === "scripts"
                  ? "border-b-2 border-[var(--color-primary)] text-[var(--color-primary)]"
                  : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
              }
            `}
          >
            <Terminal className="size-3" />
            Scripts
          </button>
        </div>
        <button
          type="button"
          onClick={() =>
            queryClient.invalidateQueries({ queryKey: ["agent-containers", agentName] })
          }
          aria-label="Refresh containers"
          className="mr-2 flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
        >
          <RefreshCw className="size-3.5" />
        </button>
      </div>

      {activeTab === "containers" ? (
        <div role="tabpanel" id="fleet-panel-containers" aria-labelledby="fleet-tab-containers">
          <ManageContainersBody
            key={open ? `containers:${agentName}:${open}` : "closed"}
            agentName={agentName}
            onClose={onClose}
            registered={registered}
          />
        </div>
      ) : (
        <div role="tabpanel" id="fleet-panel-scripts" aria-labelledby="fleet-tab-scripts">
          <ManageScriptsBody
            key={open ? `scripts:${agentName}:${open}` : "closed"}
            agentName={agentName}
            onClose={onClose}
            registered={registered}
          />
        </div>
      )}
    </Dialog>
  );
}

function ManageContainersBody({
  agentName,
  onClose,
  registered,
}: {
  agentName: string;
  onClose: () => void;
  registered: RegisteredRuntime[];
}) {
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState("");
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [port, setPort] = useState("8080");

  const { data: containers, isLoading, error } = useQuery({
    queryKey: ["agent-containers", agentName],
    queryFn: () => client.listAgentContainers(agentName),
    staleTime: 15_000,
  });

  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; image: string; port: number }) =>
      client.registerRuntime({
        displayName: payload.displayName,
        image: payload.image,
        containerPort: payload.port,
        agent: agentName,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      onClose();
    },
  });

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase();
    if (!q) return containers ?? [];
    return (containers ?? []).filter(
      (c) => c.modelName.toLowerCase().includes(q) || c.id.toLowerCase().includes(q),
    );
  }, [containers, filter]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
  const safePage = Math.min(page, totalPages);
  const pageItems = useMemo(
    () => filtered.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE),
    [filtered, safePage],
  );

  const selected = (containers ?? []).find((c) => c.id === selectedId) ?? null;

  const pick = (c: Container) => {
    if (selectedId === c.id) {
      setSelectedId(null);
      return;
    }
    setSelectedId(c.id);
    setDisplayName(displayNameFromContainer(c));
    // Prefill from the container's detected port; fall back to the platform
    // default when telemetry reports none (e.g. a stopped container).
    setPort(c.port != null ? String(c.port) : "8080");
  };

  const confirmRegister = () => {
    if (!selected || !displayName.trim()) return;
    registerMutation.mutate({
      displayName: displayName.trim(),
      image: selected.modelName || selected.id,
      port: parseInt(port, 10) || 8080,
    });
  };

  const go = (p: number) => setPage(Math.min(Math.max(1, p), totalPages));

  return (
    <div className="space-y-4 p-5">
      <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
        Running containers on{" "}
        <span className="font-mono text-[var(--color-text-heading)]">{agentName}</span>.
        Pick one to register — model discovery runs automatically once it's live.
      </p>

        {/* Filter */}
        <div className="relative">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-text-muted)]" />
          <input
            type="search"
            value={filter}
            onChange={(e) => {
              setFilter(e.target.value);
              setPage(1);
            }}
            placeholder="Filter by container name or id…"
            aria-label="Filter containers"
            className={`
              h-8 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)]
              bg-[var(--color-bg-surface)] pl-8 pr-3 text-sm text-[var(--color-text)]
              placeholder:text-[var(--color-text-muted)]
              focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)]
              transition-colors duration-[var(--duration-fast)]
            `}
          />
        </div>

        {/* Body */}
        {isLoading ? (
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
            {Array.from({ length: 6 }, (_, i) => (
              <Skeleton key={i} className="h-28 w-full" />
            ))}
          </div>
        ) : error ? (
          <EmptyState
            title="Failed to load containers"
            description={error.message}
            action={
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  queryClient.invalidateQueries({ queryKey: ["agent-containers", agentName] })
                }
              >
                Retry
              </Button>
            }
          />
        ) : filtered.length === 0 ? (
          <EmptyState
            icon={<Box className="size-12" strokeWidth={1.5} />}
            title={containers?.length ? "No matches" : "No running containers"}
            description={
              containers?.length
                ? `Nothing on this agent matches "${filter}".`
                : "Start a container on the agent machine and it will show up here."
            }
          />
        ) : (
          <>
            <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
              {pageItems.map((c) => {
                const already = isContainerRegistered(registered, agentName, c);
                const selectedCard = selectedId === c.id;
                return (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => !already && pick(c)}
                    disabled={already}
                    aria-pressed={selectedCard}
                    className={`
                      group relative flex flex-col gap-2 overflow-hidden rounded-[var(--radius-xl)] border p-3 text-left
                      transition-all duration-[var(--duration-fast)]
                      focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
                      ${
                        selectedCard
                          ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)] cursor-pointer"
                          : already
                            ? "cursor-not-allowed border-[var(--color-border)] bg-[var(--color-bg-muted)] opacity-55"
                            : "cursor-pointer border-[var(--color-border)] bg-[var(--color-bg-surface)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-elevated)]"
                      }
                    `}
                  >
                    <span className="truncate font-mono text-xs text-[var(--color-text-heading)]" title={c.modelName}>
                      {c.modelName}
                    </span>
                    <div className="grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-[var(--color-text-muted)]">
                      <span className="flex items-center gap-1 capitalize">
                        <StatusDot
                          status={c.status === "stopping" ? "stopped" : c.status}
                          size="sm"
                        />
                        {c.status}
                      </span>
                      <span className="truncate text-right font-mono">{c.port ?? "—"}</span>
                      <span className="flex items-center gap-1">
                        <MemoryStick className="size-2.5" />
                        {formatMb(c.memoryMb)}
                      </span>
                      <span className="text-right font-mono">
                        {c.cpuPercent > 0 ? `${c.cpuPercent}%` : "—"}
                      </span>
                    </div>
                    <div className="mt-auto pt-1">
                      {already ? (
                        <Badge variant="success" className="gap-1">
                          <PackageOpen className="size-2.5" />
                          registered
                        </Badge>
                      ) : selectedCard ? (
                        <Badge variant="info">
                          selected
                        </Badge>
                      ) : (
                        <Badge
                          variant="outline"
                          className="opacity-0 transition-opacity group-hover:opacity-100"
                        >
                          register
                        </Badge>
                      )}
                    </div>
                  </button>
                );
              })}
            </div>

            {/* Pagination */}
            {totalPages > 1 && (
              <div className="flex items-center justify-between pt-1">
                <span className="text-[10px] text-[var(--color-text-muted)]">
                  {filtered.length} container{filtered.length !== 1 ? "s" : ""} · page{" "}
                  {safePage} of {totalPages}
                </span>
                <div className="flex items-center gap-1">
                  <button
                    type="button"
                    onClick={() => go(safePage - 1)}
                    disabled={safePage <= 1}
                    aria-label="Previous page"
                    className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    <ChevronLeft className="size-3.5" />
                  </button>
                  {Array.from({ length: totalPages }, (_, i) => i + 1).map((p) => (
                    <button
                      key={p}
                      type="button"
                      onClick={() => go(p)}
                      aria-label={`Page ${p}`}
                      aria-current={p === safePage ? "page" : undefined}
                      className={`
                        flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)]
                        font-mono text-[10px] transition-colors
                        ${
                          p === safePage
                            ? "bg-[var(--color-primary)] text-[var(--color-text-inverse)]"
                            : "text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
                        }
                      `}
                    >
                      {p}
                    </button>
                  ))}
                  <button
                    type="button"
                    onClick={() => go(safePage + 1)}
                    disabled={safePage >= totalPages}
                    aria-label="Next page"
                    className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    <ChevronRight className="size-3.5" />
                  </button>
                </div>
              </div>
            )}
          </>
        )}

        {/* Inline confirm: display name + container + port */}
        <AnimatePresence>
          {selected && (
            <motion.div
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: 8 }}
              transition={{ duration: 0.18 }}
              className="space-y-3 rounded-[var(--radius-xl)] border border-[var(--color-primary)] bg-[var(--color-bg-muted)] p-3.5"
            >
              <div className="flex min-w-0 items-center justify-between gap-2">
                <p className="truncate text-xs font-medium text-[var(--color-text-heading)]">
                  Register {selected.modelName}
                </p>
                <button
                  type="button"
                  onClick={() => setSelectedId(null)}
                  aria-label="Cancel selection"
                  className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:bg-[var(--color-bg-elevated)]"
                >
                  <X className="size-3.5" />
                </button>
              </div>
              <div className="space-y-2.5">
                <Input
                  label="Display name"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  placeholder="my-model-server"
                  aria-label="Display name"
                />
                <div className="flex flex-col gap-1">
                  <span className="text-xs font-medium text-[var(--color-text-muted)]">Container</span>
                  <code className="h-8 truncate rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-1.5 font-mono text-xs text-[var(--color-text)]">
                    {selected.modelName}
                  </code>
                </div>
                <Input
                  label="Port"
                  type="number"
                  value={port}
                  onChange={(e) => setPort(e.target.value)}
                  placeholder="8080"
                  aria-label="Port"
                />
              </div>
              <div className="flex justify-end gap-2 pt-1">
                <Button variant="ghost" size="sm" onClick={() => setSelectedId(null)}>
                  Cancel
                </Button>
                <Button
                  size="sm"
                  loading={registerMutation.isPending}
                  disabled={!displayName.trim()}
                  onClick={confirmRegister}
                >
                  <PackageOpen className="size-3" />
                  Register on {agentName}
                </Button>
              </div>
            </motion.div>
          )}
        </AnimatePresence>

        {registerMutation.isError && (
          <p className="text-xs text-[var(--color-status-error)]">
            {registerMutation.error.message}
          </p>
        )}
    </div>
  );
}

// ─── Manage scripts body ─────────────────────────────────────────

/** Derive a display name from a script file path (e.g. /opt/scripts/run_vllm.sh → run_vllm). */
function displayNameFromScript(path: string): string {
  const basename = path.split("/").pop() ?? path;
  return basename
    .replace(/\.sh$/i, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 32) || "script";
}

function ManageScriptsBody({
  agentName,
  onClose,
  registered,
}: {
  agentName: string;
  onClose: () => void;
  registered: RegisteredRuntime[];
}) {
  const queryClient = useQueryClient();
  const isHost = agentName === "host";

  // Host: manual entry form state
  const [hostDisplayName, setHostDisplayName] = useState("");
  const [hostPath, setHostPath] = useState("");
  const [hostPort, setHostPort] = useState("8080");

  // Remote: selection state
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [remoteDisplayName, setRemoteDisplayName] = useState("");
  const [remotePort, setRemotePort] = useState("8080");

  const { data: availableScripts, isLoading, error } = useQuery({
    queryKey: ["agent-available-scripts", agentName],
    queryFn: () => client.listAvailableScripts(agentName),
    staleTime: 15_000,
    enabled: !isHost,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
    queryClient.invalidateQueries({ queryKey: ["models"] });
    onClose();
  };

  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; launcherPath: string; port: number }) =>
      client.registerRuntime({
        displayName: payload.displayName,
        image: payload.displayName,
        containerPort: payload.port,
        agent: agentName,
        runtimeKind: "script",
        launcherPath: payload.launcherPath,
      }),
    onSuccess: invalidate,
  });

  const handleHostRegister = () => {
    if (!hostPath.trim() || !hostDisplayName.trim()) return;
    registerMutation.mutate({
      displayName: hostDisplayName.trim(),
      launcherPath: hostPath.trim(),
      port: parseInt(hostPort, 10) || 8080,
    });
  };

  const handleRemoteRegister = () => {
    if (!selectedPath || !remoteDisplayName.trim()) return;
    registerMutation.mutate({
      displayName: remoteDisplayName.trim(),
      launcherPath: selectedPath,
      port: parseInt(remotePort, 10) || 8080,
    });
  };

  const pickRemoteScript = (script: AgentAvailableScript) => {
    if (selectedPath === script.path) {
      setSelectedPath(null);
      return;
    }
    setSelectedPath(script.path);
    setRemoteDisplayName(displayNameFromScript(script.path));
  };

  const isScriptRegistered = (path: string) =>
    registered.some(
      (rc) =>
        rc.runtimeKind === "script" &&
        rc.launcherPath?.toLowerCase() === path.toLowerCase(),
    );

  if (isHost) {
    return (
      <div className="space-y-4 p-5">
        <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
          Register a launcher script on{" "}
          <span className="font-mono text-[var(--color-text-heading)]">host</span>.
          Enter the full path to the script — it will be launched when the runtime starts.
        </p>

        <div className="space-y-3">
          <Input
            label="Display name"
            value={hostDisplayName}
            onChange={(e) => setHostDisplayName(e.target.value)}
            placeholder="my-vllm-script"
          />
          <Input
            label="Launcher path"
            value={hostPath}
            onChange={(e) => setHostPath(e.target.value)}
            placeholder="/home/user/scripts/run_vllm.sh"
          />
          <Input
            label="Port"
            type="number"
            value={hostPort}
            onChange={(e) => setHostPort(e.target.value)}
            placeholder="8080"
          />
        </div>

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            size="sm"
            loading={registerMutation.isPending}
            disabled={!hostDisplayName.trim() || !hostPath.trim()}
            onClick={handleHostRegister}
          >
            <Terminal className="size-3" />
            Register script
          </Button>
        </div>

        {registerMutation.isError && (
          <p className="text-xs text-[var(--color-status-error)]">
            {registerMutation.error.message}
          </p>
        )}
      </div>
    );
  }

  // Remote agent: picker flow
  return (
    <div className="space-y-4 p-5">
      <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
        Launcher scripts discovered on{" "}
        <span className="font-mono text-[var(--color-text-heading)]">{agentName}</span>{" "}
        from its <code className="font-mono">scripts_dir</code> configuration.
        Select one to register as a runtime.
      </p>

      {isLoading ? (
        <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} className="h-20 w-full" />
          ))}
        </div>
      ) : error ? (
        <EmptyState
          title="Couldn't list scripts"
          description={`Couldn't reach ${agentName} to list scripts.`}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() =>
                queryClient.invalidateQueries({ queryKey: ["agent-available-scripts", agentName] })
              }
            >
              Retry
            </Button>
          }
        />
      ) : (availableScripts ?? []).length === 0 ? (
        <EmptyState
          icon={<Terminal className="size-12" strokeWidth={1.5} />}
          title="No scripts found"
          description={`No scripts found on ${agentName}. Add .sh files to the agent's scripts_dir.`}
        />
      ) : (
        <>
          <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
            {(availableScripts ?? []).map((s) => {
              const already = isScriptRegistered(s.path);
              const selected = selectedPath === s.path;
              return (
                <button
                  key={s.path}
                  type="button"
                  onClick={() => !already && pickRemoteScript(s)}
                  disabled={already}
                  aria-pressed={selected}
                  className={`
                    group relative flex flex-col gap-2 overflow-hidden rounded-[var(--radius-xl)] border p-3 text-left
                    transition-all duration-[var(--duration-fast)]
                    focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
                    ${
                      selected
                        ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)] cursor-pointer"
                        : already
                          ? "cursor-not-allowed border-[var(--color-border)] bg-[var(--color-bg-muted)] opacity-55"
                          : "cursor-pointer border-[var(--color-border)] bg-[var(--color-bg-surface)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-elevated)]"
                    }
                  `}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate font-mono text-xs text-[var(--color-text-heading)]" title={s.name}>
                      {s.name}
                    </span>
                    {already ? (
                      <Badge variant="success" className="shrink-0 gap-1">
                        <Terminal className="size-2.5" />
                        registered
                      </Badge>
                    ) : selected ? (
                      <Badge variant="info" className="shrink-0">
                        selected
                      </Badge>
                    ) : (
                      <Badge
                        variant="outline"
                        className="shrink-0 opacity-0 transition-opacity group-hover:opacity-100"
                      >
                        register
                      </Badge>
                    )}
                  </div>
                  <p className="truncate text-[10px] text-[var(--color-text-muted)]" title={s.path}>
                    {s.path}
                  </p>
                </button>
              );
            })}
          </div>

          {/* Inline confirm: display name + port + register */}
          <AnimatePresence>
            {selectedPath && (
              <motion.div
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: 8 }}
                transition={{ duration: 0.18 }}
                className="space-y-3 rounded-[var(--radius-xl)] border border-[var(--color-primary)] bg-[var(--color-bg-muted)] p-3.5"
              >
                <div className="flex items-center justify-between gap-2">
                  <p className="text-xs font-medium text-[var(--color-text-heading)]">
                    Register script
                  </p>
                  <button
                    type="button"
                    onClick={() => setSelectedPath(null)}
                    aria-label="Cancel selection"
                    className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:bg-[var(--color-bg-elevated)]"
                  >
                    <X className="size-3.5" />
                  </button>
                </div>
                <p className="truncate font-mono text-[10px] text-[var(--color-text-muted)]">
                  {selectedPath}
                </p>
                <div className="grid gap-2 sm:grid-cols-[1fr_auto] sm:items-end">
                  <Input
                    label="Display name"
                    value={remoteDisplayName}
                    onChange={(e) => setRemoteDisplayName(e.target.value)}
                    placeholder="my-script-server"
                  />
                  <Input
                    label="Port"
                    type="number"
                    value={remotePort}
                    onChange={(e) => setRemotePort(e.target.value)}
                    placeholder="8080"
                  />
                </div>
                <div className="flex justify-end gap-2 pt-1">
                  <Button variant="ghost" size="sm" onClick={() => setSelectedPath(null)}>
                    Cancel
                  </Button>
                  <Button
                    size="sm"
                    loading={registerMutation.isPending}
                    disabled={!remoteDisplayName.trim()}
                    onClick={handleRemoteRegister}
                  >
                    <Terminal className="size-3" />
                    Register on {agentName}
                  </Button>
                </div>
              </motion.div>
            )}
          </AnimatePresence>
        </>
      )}

      {registerMutation.isError && (
        <p className="text-xs text-[var(--color-status-error)]">
          {registerMutation.error.message}
        </p>
      )}
    </div>
  );
}

// ─── Discovered model chip ────────────────────────────────────────

function ModelChip({ model }: { model: Model }) {
  const validating = model.status === "validating";
  return (
    <Tooltip
      content={
        validating
          ? "Validating — not available for inference yet"
          : model.status === "invalid"
            ? "Invalid — cannot be served"
            : model.status === "deprecated"
              ? "Deprecated — legacy model"
              : "Ready for inference"
      }
    >
      <span
        className={`
          inline-flex items-center gap-1.5 rounded-[var(--radius-md)] border px-1.5 py-0.5
          font-mono text-[10px] leading-none
          ${
            validating
              ? "border-[color-mix(in_srgb,var(--color-status-warning)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-warning)_12%,transparent)] text-[var(--color-status-warning)]"
              : model.status === "invalid"
                ? "border-[color-mix(in_srgb,var(--color-status-error)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)] text-[var(--color-status-error)]"
                : model.status === "deprecated"
                  ? "border-[var(--color-border)] bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]"
                  : "border-[color-mix(in_srgb,var(--color-status-running)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-running)_10%,transparent)] text-[var(--color-status-running)]"
          }
        `}
      >
        <StatusDot status={model.status} size="sm" />
        <span className="truncate">{model.name}</span>
        {model.status !== "ready" && (
          <span className="uppercase tracking-wide opacity-80">
            {validating ? "validating…" : model.status}
          </span>
        )}
      </span>
    </Tooltip>
  );
}

// ─── Registered container card ────────────────────────────────────

function RegisteredContainerCard({
  container,
  highlight = false,
  runtimeStatus = null,
}: {
  container: RegisteredRuntime;
  /** When true, briefly ring the card (deep-link focus). */
  highlight?: boolean;
  /** Runtime docker status from the owning agent's telemetry (may be null = unknown). */
  runtimeStatus?: string | null;
}) {
  const queryClient = useQueryClient();
  const [benchmark, setBenchmark] = useState<{
    tokensPerSec: number;
    latencyMs: number;
    promptName?: string | null;
    promptVersion?: number | null;
  } | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [ringActive, setRingActive] = useState(highlight);
  const [rediscoverError, setRediscoverError] = useState<string | null>(null);
  const [editingSlots, setEditingSlots] = useState(false);
  const [slotsValue, setSlotsValue] = useState(container.maxConcurrentInferences);
  const [editingName, setEditingName] = useState(false);

  // Clear the highlight ring after a short window so it doesn't linger.
  useEffect(() => {
    if (!highlight) return;
    const t = setTimeout(() => setRingActive(false), 2600);
    return () => clearTimeout(t);
  }, [highlight]);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
    queryClient.invalidateQueries({ queryKey: ["models"] });
    queryClient.invalidateQueries({ queryKey: ["containers"] });
    queryClient.invalidateQueries({ queryKey: ["agent-containers"] });
    // Runtime dots come from agent telemetry — refresh them after any lifecycle change.
    queryClient.invalidateQueries({ queryKey: ["agents"] });
  };

  const startMutation = useMutation({
    mutationFn: (id: string) => client.startRegisteredRuntime(id),
    onSuccess: invalidate,
  });

  const stopMutation = useMutation({
    mutationFn: (runtimeContainerId: string) => client.stopContainer(runtimeContainerId),
    onSuccess: invalidate,
  });

  const restartMutation = useMutation({
    mutationFn: (runtimeContainerId: string) => client.restartContainer(runtimeContainerId),
    onSuccess: invalidate,
  });

  const rediscoverMutation = useMutation({
    mutationFn: (id: string) => client.rediscoverRuntime(id),
    onSuccess: () => {
      setRediscoverError(null);
      invalidate();
    },
    onError: (err: Error) => {
      // Surface non-2xx failures (e.g. unknown id / dead container) inline.
      setRediscoverError(err.message || "Rediscover failed");
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteRuntime(id),
    onSuccess: invalidate,
  });

  const stopScriptMutation = useMutation({
    mutationFn: (id: string) => client.stopRegisteredRuntime(id),
    onSuccess: invalidate,
  });

  const benchmarkMutation = useMutation({
    mutationFn: (modelId: string) => client.runBenchmark(modelId),
    onSuccess: (result) => {
      setBenchmark({
        tokensPerSec: result.tokensPerSec,
        latencyMs: result.latencyMs,
        promptName: result.promptName,
        promptVersion: result.promptVersion,
      });
    },
  });

  const updateConcurrencyMutation = useMutation({
    mutationFn: (value: number) =>
      client.updateRuntimeConcurrency(container.id, {
        canRunAlongWith: container.canRunAlongWith,
        maxConcurrentInferences: value,
      }),
    onSuccess: () => {
      invalidate();
      setEditingSlots(false);
    },
  });

  const firstModel = container.discoveredModels[0];
  const canBenchmark = !!firstModel && firstModel.status === "ready";
  const transitional = REG_TRANSITIONAL.has(container.status);
  const signal = runtimeSignal(runtimeStatus);
  const isScript = container.runtimeKind === "script";
  const busy =
    startMutation.isPending ||
    stopMutation.isPending ||
    restartMutation.isPending ||
    rediscoverMutation.isPending ||
    deleteMutation.isPending ||
    stopScriptMutation.isPending;

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0, scale: 0.96 }}
      transition={{ duration: 0.2 }}
    >
      <Card
        padding="md"
        className={`
          flex h-full flex-col gap-3 overflow-hidden transition-shadow duration-500
          ${ringActive ? "ring-2 ring-[var(--color-primary)] shadow-[var(--shadow-glow)]" : ""}
        `}
        aria-live={ringActive ? "polite" : undefined}
      >
        {/* Header */}
        <div className="flex items-start justify-between gap-2">
          <div className="flex min-w-0 items-center gap-2">
            <Tooltip content={`Runtime: ${RUNTIME_LABEL[signal]}`}>
              <span className="inline-flex">
                <StatusDot
                  status={
                    signal === "running"
                      ? "running"
                      : signal === "transitional"
                        ? "starting"
                        : signal === "down"
                          ? "error"
                          : "stopped"
                  }
                  size="md"
                />
              </span>
            </Tooltip>
            <div className="min-w-0">
              <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]" title={container.displayName}>
                {container.displayName}
              </p>
              <p className="truncate text-[10px] text-[var(--color-text-muted)]" title={isScript ? (container.launcherPath ?? container.image) : container.image}>
                {isScript ? (container.launcherPath ?? container.image) : container.image}
              </p>
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-1.5">
            {isScript && (
              <Badge variant="outline" className="gap-1">
                <FileCode className="size-2.5" />
                script
              </Badge>
            )}
            <Badge variant={REG_STATUS_VARIANT[container.status]}>
              {container.status}
            </Badge>
          </div>
        </div>

        {/* Metrics */}
        <div className="grid grid-cols-4 gap-2 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] px-2.5 py-2 text-[10px]">
          <div>
            <p className="text-[var(--color-text-muted)]">Port</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              {container.mappedPort ?? container.containerPort}
            </p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)]">Models</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              {container.discoveredModels.length > 0 ? container.discoveredModels.length : "—"}
            </p>
          </div>
          <div className="group/slots relative">
            <p className="text-[var(--color-text-muted)]">Parallel Slots</p>
            {editingSlots ? (
              <div className="flex items-center gap-1">
                <input
                  type="number"
                  min={1}
                  max={128}
                  value={slotsValue}
                  onChange={(e) => {
                    const v = parseInt(e.target.value, 10);
                    if (!isNaN(v)) setSlotsValue(Math.max(1, Math.min(128, v)));
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") updateConcurrencyMutation.mutate(slotsValue);
                    if (e.key === "Escape") {
                      setEditingSlots(false);
                      setSlotsValue(container.maxConcurrentInferences);
                    }
                  }}
                  autoFocus
                  className="h-5 w-10 rounded-[var(--radius-sm)] border border-[var(--color-border)] bg-[var(--color-bg)] px-1 font-mono text-[var(--color-text-heading)] outline-none focus:border-[var(--color-accent)]"
                />
                <button
                  type="button"
                  disabled={updateConcurrencyMutation.isPending}
                  onClick={() => updateConcurrencyMutation.mutate(slotsValue)}
                  className="rounded-[var(--radius-sm)] p-0.5 text-[var(--color-status-success)] hover:bg-[color-mix(in_srgb,var(--color-status-success)_14%,transparent)] disabled:opacity-50"
                  aria-label="Confirm parallel slots"
                >
                  <Check className="size-3" />
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setEditingSlots(false);
                    setSlotsValue(container.maxConcurrentInferences);
                  }}
                  className="rounded-[var(--radius-sm)] p-0.5 text-[var(--color-text-muted)] hover:bg-[color-mix(in_srgb,var(--color-text-muted)_14%,transparent)]"
                  aria-label="Cancel editing parallel slots"
                >
                  <X className="size-3" />
                </button>
              </div>
            ) : (
              <button
                type="button"
                onClick={() => {
                  setSlotsValue(container.maxConcurrentInferences);
                  setEditingSlots(true);
                }}
                className="group/edit flex items-center gap-1 rounded-[var(--radius-sm)] px-0.5 -mx-0.5 text-left hover:bg-[color-mix(in_srgb,var(--color-text-muted)_8%,transparent)]"
              >
                <span className="font-mono text-[var(--color-text-heading)]">
                  {container.maxConcurrentInferences}
                </span>
                <Pencil className="size-2.5 text-[var(--color-text-muted)] opacity-0 transition-opacity group-hover/slots:opacity-100 group-hover/edit:opacity-100" />
              </button>
            )}
          </div>
          <div className="min-w-0">
            <p className="text-[var(--color-text-muted)]">Discovered</p>
            <p className="truncate font-mono text-[var(--color-text-heading)]" title={container.lastDiscoveredAt ?? undefined}>
              {relativeTime(container.lastDiscoveredAt)}
            </p>
          </div>
        </div>

        {/* Discovered models */}
        {container.discoveredModels.length > 0 ? (
          <div className="flex flex-wrap gap-1">
            {container.discoveredModels.map((m) => (
              <ModelChip key={m.id} model={m} />
            ))}
          </div>
        ) : (
          <p className="text-[10px] italic text-[var(--color-text-muted)]">
            No models discovered yet{transitional ? " — discovery in progress" : ""}.
          </p>
        )}

        {container.errorMessage && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{container.errorMessage}</span>
          </div>
        )}

        {rediscoverError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="min-w-0 flex-1 truncate">
              {rediscoverError}
              {signal === "down" && " — the container appears to be stopped; start it first."}
            </span>
            <button
              type="button"
              onClick={() => setRediscoverError(null)}
              aria-label="Dismiss rediscover error"
              className="shrink-0 rounded-[var(--radius-sm)] p-0.5 text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_14%,transparent)]"
            >
              <X className="size-3" />
            </button>
          </div>
        )}

        {/* Actions */}
        <div className="mt-auto flex flex-wrap items-center gap-1.5 pt-1">
          <Tooltip content={benchDisabledTooltip(firstModel)}>
            <span className="inline-flex">
              <Button
                variant="ghost"
                size="sm"
                disabled={!canBenchmark || busy}
                loading={benchmarkMutation.isPending}
                onClick={() => firstModel && benchmarkMutation.mutate(firstModel.id)}
              >
                <Gauge className="size-3" />
                Benchmark
              </Button>
            </span>
          </Tooltip>
          {benchmark && (
            <span
              className="inline-flex items-center gap-1 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-running)_12%,transparent)] px-1.5 py-0.5 font-mono text-[10px] text-[var(--color-status-running)]"
              title="Last benchmark"
            >
              <Zap className="size-2.5" />
              {benchmark.tokensPerSec} tok/s · {benchmark.latencyMs}ms
              {benchmark.promptName && (
                <span className="truncate text-[var(--color-text-muted)]">
                  {" "}· {benchmark.promptName}{benchmark.promptVersion != null ? ` v${benchmark.promptVersion}` : ""}
                </span>
              )}
            </span>
          )}
          <span className="mx-0.5 hidden h-4 w-px bg-[var(--color-border)] sm:block" />
          {isScript ? (
            // Scripts: Start when down, Stop when running, no Restart (managed by registration lifecycle)
            signal === "running" ? (
              <Button
                variant="ghost"
                size="sm"
                disabled={busy}
                loading={stopScriptMutation.isPending}
                onClick={() => stopScriptMutation.mutate(container.id)}
                title="Stop script"
              >
                <Square className="size-3" />
                Stop
              </Button>
            ) : signal === "down" || signal === "unknown" ? (
              <Button
                variant="primary"
                size="sm"
                disabled={busy}
                loading={startMutation.isPending}
                onClick={() => startMutation.mutate(container.id)}
                title="Start script"
              >
                <Play className="size-3" />
                Start
              </Button>
            ) : signal === "transitional" ? (
              <span className="text-[10px] italic text-[var(--color-text-muted)]">
                {RUNTIME_LABEL[signal]}
              </span>
            ) : null
          ) : signal === "running" ? (
            <>
              <Button
                variant="ghost"
                size="sm"
                disabled={!container.runtimeContainerId || busy}
                loading={restartMutation.isPending}
                onClick={() => container.runtimeContainerId && restartMutation.mutate(container.runtimeContainerId)}
                title="Restart runtime container"
              >
                <RotateCw className="size-3" />
                Restart
              </Button>
              <Button
                variant="ghost"
                size="sm"
                disabled={!container.runtimeContainerId || busy}
                loading={stopMutation.isPending}
                onClick={() => container.runtimeContainerId && stopMutation.mutate(container.runtimeContainerId)}
                title="Stop runtime container"
              >
                <Square className="size-3" />
                Stop
              </Button>
            </>
          ) : signal === "down" || signal === "unknown" ? (
            // Stopped (or no telemetry): offer Start — the container may simply be down.
            // Start works by registration id (backend resolves the runtime container by
            // image name), so it must not require runtimeContainerId (covers never-started).
            <Button
              variant="primary"
              size="sm"
              disabled={busy}
              loading={startMutation.isPending}
              onClick={() => startMutation.mutate(container.id)}
              title="Start runtime container"
            >
              <Play className="size-3" />
              Start
            </Button>
          ) : (
            // Transitional (starting/stopping/created/restarting) — no lifecycle action.
            <span className="text-[10px] italic text-[var(--color-text-muted)]">
              {RUNTIME_LABEL[signal]}
            </span>
          )}
          <div className="ml-auto flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              onClick={() => setEditingName(true)}
              title="Edit runtime settings"
            >
              <Pencil className="size-3" />
              Edit
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              loading={rediscoverMutation.isPending}
              onClick={() => rediscoverMutation.mutate(container.id)}
              title="Rediscover models"
            >
              <RefreshCw className="size-3" />
              Rediscover
            </Button>
            {confirmingDelete ? (
              <span className="inline-flex items-center gap-1 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)] px-1.5 py-0.5">
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  loading={deleteMutation.isPending}
                  onClick={() => {
                    deleteMutation.mutate(container.id);
                    setConfirmingDelete(false);
                  }}
                  aria-label={`Confirm delete ${container.displayName} registration`}
                  className="text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_14%,transparent)]"
                >
                  <Trash2 className="size-3" />
                  Delete
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  onClick={() => setConfirmingDelete(false)}
                  aria-label={`Cancel delete ${container.displayName} registration`}
                >
                  Cancel
                </Button>
              </span>
            ) : (
              <Button
                variant="ghost"
                size="sm"
                disabled={busy}
                onClick={() => setConfirmingDelete(true)}
                aria-label={`Delete ${container.displayName} registration`}
                title="Delete registration"
                className="text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)]"
              >
                <Trash2 className="size-3" />
              </Button>
            )}
          </div>
        </div>
      </Card>

      <EditRuntimeDialog
        runtime={container}
        open={editingName}
        onClose={() => setEditingName(false)}
      />
    </motion.div>
  );
}

function benchDisabledTooltip(firstModel: Model | undefined): string {
  if (!firstModel) return "No discovered models to benchmark";
  if (firstModel.status === "validating") return `${firstModel.name} is still validating — not ready to benchmark`;
  if (firstModel.status === "invalid") return `${firstModel.name} is invalid — cannot benchmark`;
  if (firstModel.status === "deprecated") return `${firstModel.name} is deprecated — cannot benchmark`;
  return `${firstModel.name} is not ready to benchmark`;
}

// ─── Agent section ────────────────────────────────────────────────

function AgentSection({
  agent,
  registeredContainers,
  defaultExpanded,
  focusContainerId,
  onManage,
  onConcurrency,
  settings,
}: {
  agent: Agent;
  registeredContainers: RegisteredRuntime[];
  defaultExpanded: boolean;
  /** When a registered container on this agent is the deep-link target. */
  focusContainerId: string | null;
  onManage: (agentName: string) => void;
  onConcurrency: (agentName: string) => void;
  settings?: Settings;
}) {
  const agentRcs = registeredContainers.filter((rc) => rc.agent === agent.name);
  // Deep-link focus forces this section open even if it normally starts collapsed.
  const [expanded, setExpanded] = useState(
    defaultExpanded ||
      (focusContainerId !== null && agentRcs.some((rc) => rc.id === focusContainerId)),
  );
  const [filter, setFilter] = useState("");
  const connectivity = agentConnectivity(agent);
  const isHost = agent.name === "host";
  const focusedCardRef = useRef<HTMLDivElement | null>(null);

  // Agent display name editing
  const [editingAgentName, setEditingAgentName] = useState<string | null>(null);
  const [agentNameDraft, setAgentNameDraft] = useState("");
  const queryClient = useQueryClient();
  const settingsMutation = useMutation({
    mutationFn: (patch: Partial<Settings>) => client.updateSettings(patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings"] }),
  });

  const filteredContainers = useMemo(() => {
    const q = filter.trim().toLowerCase();
    if (!q) return agentRcs;
    return agentRcs.filter(
      (c) =>
        c.displayName.toLowerCase().includes(q) ||
        c.image.toLowerCase().includes(q) ||
        c.discoveredModels.some((m) => m.name.toLowerCase().includes(q)),
    );
  }, [agentRcs, filter]);

  // Deep-link: once the focused card mounts (after the expand animation),
  // bring it into view. Guarded so it only scrolls once per focus target.
  const hasFocusTarget = focusContainerId !== null && agentRcs.some((rc) => rc.id === focusContainerId);
  const scrolledForFocus = useRef<string | null>(null);

  useEffect(() => {
    if (!hasFocusTarget || !expanded || scrolledForFocus.current === focusContainerId) return;
    if (!focusedCardRef.current) return;
    const t = setTimeout(() => {
      focusedCardRef.current?.scrollIntoView({ behavior: "smooth", block: "center" });
      scrolledForFocus.current = focusContainerId;
    }, 350);
    return () => clearTimeout(t);
  }, [hasFocusTarget, expanded, focusContainerId]);

  return (
    <section>
      {/* Agent header */}
      <div className="group flex items-center gap-2 py-1.5">
        <button
          type="button"
          onClick={() => setExpanded((p) => !p)}
          aria-expanded={expanded}
          aria-label={`Toggle ${agent.name} section`}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-3 rounded-[var(--radius-lg)] px-2 py-1.5 text-left transition-colors hover:bg-[var(--color-bg-muted)]"
        >
          {expanded ? (
            <ChevronDown className="size-4 shrink-0 text-[var(--color-text-muted)]" />
          ) : (
            <ChevronRight className="size-4 shrink-0 text-[var(--color-text-muted)]" />
          )}

          <StatusDot status={connectivity} size="md" />

          <span className="truncate text-sm font-semibold text-[var(--color-text-heading)] transition-colors group-hover:text-[var(--color-primary)]">
            {settings?.agentDisplayNames?.[agent.name] ?? agent.name}
          </span>
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              setEditingAgentName(agent.name);
              setAgentNameDraft(settings?.agentDisplayNames?.[agent.name] ?? agent.name);
            }}
            aria-label={`Rename agent ${agent.name}`}
            title="Edit display name"
            className="flex shrink-0 cursor-pointer items-center rounded p-0.5 text-[var(--color-text-muted)] opacity-0 transition-opacity hover:text-[var(--color-text)] group-hover:opacity-100"
          >
            <Pencil className="size-3" />
          </button>
          {isHost && <Badge variant="outline">host</Badge>}

          <div className="hidden min-w-0 items-center gap-3 text-[10px] text-[var(--color-text-muted)] md:flex">
            {agent.osPlatform && (
              <span className="flex min-w-0 items-center gap-1" title={agent.osPlatform}>
                <OsIcon platform={agent.osPlatform} className="size-3 shrink-0" />
                <span className="truncate">{formatOsPlatform(agent.osPlatform)}</span>
              </span>
            )}
            {agent.hostname && (
              <span className="flex min-w-0 items-center gap-1">
                <Server className="size-3 shrink-0" />
                <span className="truncate">{agent.hostname}</span>
              </span>
            )}
            {agent.gpuInfo && (
              <span className="flex items-center gap-1">
                <Cpu className="size-3 shrink-0" />
                <span className="truncate">{agent.gpuInfo}</span>
              </span>
            )}
            {agent.totalMemoryMb > 0 && (
              <span className="flex shrink-0 items-center gap-1">
                <MemoryStick className="size-3 shrink-0" />
                {formatMb(agent.totalMemoryMb)}
              </span>
            )}
            {agent.cpuCores > 0 && (
              <span className="flex shrink-0 items-center gap-1">
                <Hash className="size-3 shrink-0" />
                {agent.cpuCores} threads
              </span>
            )}
          </div>
        </button>

        <Badge variant={AGENT_STATUS_VARIANT[connectivity]} className="shrink-0">
          {connectivity}
        </Badge>

        <div className="flex shrink-0 items-center gap-1">
          {agent.scripts.length > 0 && (
            <Tooltip content={`${agent.scripts.length} launcher script${agent.scripts.length !== 1 ? "s" : ""} available`}>
              <span className="inline-flex items-center gap-1 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-primary)_10%,transparent)] px-1.5 py-0.5 text-[10px] text-[var(--color-primary)]">
                <Terminal className="size-2.5" />
                {agent.scripts.length}
              </span>
            </Tooltip>
          )}
          <button
            type="button"
            onClick={() => onManage(agent.name)}
            aria-label={`Manage runtimes on ${agent.name}`}
            title="Manage runtimes"
            className="flex cursor-pointer items-center gap-1.5 rounded-[var(--radius-md)] px-2.5 py-1 text-sm text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
          >
            <PackageOpen className="size-3.5" />
            Manage
          </button>
          <button
            type="button"
            onClick={() => onConcurrency(agent.name)}
            aria-label={`Concurrency on ${agent.name}`}
            title="Configure concurrency"
            className="flex cursor-pointer items-center gap-1.5 rounded-[var(--radius-md)] px-2.5 py-1 text-sm text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
          >
            <Grid2x2 className="size-3.5" />
            Concurrency
          </button>
        </div>
      </div>

      {/* Collapsible content */}
      <AnimatePresence initial={false}>
        {expanded && (
          <motion.div
            key="agent-body"
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.22, ease: "easeOut" }}
            className="overflow-hidden"
          >
            <div className="pb-4 pl-9">
              {agentRcs.length > 0 ? (
                <>
                  {agentRcs.length >= 2 && (
                    <div className="relative mb-3">
                      <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-text-muted)]" />
                      <input
                        type="search"
                        value={filter}
                        onChange={(e) => setFilter(e.target.value)}
                        placeholder="Search runtimes..."
                        aria-label="Search registered containers"
                        className="h-8 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] pl-8 pr-3 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] outline-none transition-colors focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]"
                      />
                    </div>
                  )}
                  <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                    {filteredContainers.map((rc) => {
                      const focused = focusContainerId === rc.id;
                      const runtimeStatus =
                        rc.runtimeKind === "script"
                          ? runtimeStatusForScript(agent.scripts, rc)
                          : runtimeStatusFor(agent.containers, rc);
                      return (
                        <div key={rc.id} ref={focused ? focusedCardRef : undefined}>
                          <RegisteredContainerCard
                            container={rc}
                            highlight={focused}
                            runtimeStatus={runtimeStatus}
                          />
                        </div>
                      );
                    })}
                  </div>
                </>
              ) : (
                <Card
                  padding="md"
                  className="flex flex-col items-center gap-3 overflow-hidden border-dashed py-10 text-center"
                >
                  <div className="flex size-10 items-center justify-center rounded-[var(--radius-xl)] bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]">
                    <Box className="size-5" strokeWidth={1.5} />
                  </div>
                  <div>
                    <p className="text-sm font-medium text-[var(--color-text-heading)]">
                      No runtimes registered
                    </p>
                    <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
                      Register a container or script to auto-discover its models.
                    </p>
                  </div>
                  <Button variant="secondary" size="sm" onClick={() => onManage(agent.name)}>
                    Manage runtimes
                  </Button>
                </Card>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      <Dialog
        open={editingAgentName === agent.name}
        onOpenChange={(o) => {
          if (!o) setEditingAgentName(null);
        }}
        title={`Rename agent ${agent.name}`}
      >
        <div className="space-y-4 p-5">
          <Input
            label="Display name"
            value={agentNameDraft}
            onChange={(e) => setAgentNameDraft(e.target.value)}
            placeholder={agent.name}
            aria-label="Display name"
            autoFocus
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                const val = agentNameDraft.trim();
                const currentNames = settings?.agentDisplayNames ?? {};
                if (val && val !== agent.name) {
                  settingsMutation.mutate({ agentDisplayNames: { ...currentNames, [agent.name]: val } });
                } else {
                  const updated = { ...currentNames };
                  delete updated[agent.name];
                  settingsMutation.mutate({ agentDisplayNames: updated });
                }
                setEditingAgentName(null);
              }
            }}
          />

          <div className="flex justify-end gap-2 pt-1">
            <Button variant="ghost" size="sm" onClick={() => setEditingAgentName(null)}>
              Cancel
            </Button>
            <Button
              size="sm"
              onClick={() => {
                const val = agentNameDraft.trim();
                const currentNames = settings?.agentDisplayNames ?? {};
                if (val && val !== agent.name) {
                  settingsMutation.mutate({ agentDisplayNames: { ...currentNames, [agent.name]: val } });
                } else {
                  const updated = { ...currentNames };
                  delete updated[agent.name];
                  settingsMutation.mutate({ agentDisplayNames: updated });
                }
                setEditingAgentName(null);
              }}
            >
              Save
            </Button>
          </div>
        </div>
      </Dialog>
    </section>
  );
}

// ─── Main Fleet page ──────────────────────────────────────────────

export default function Fleet() {
  const [manageAgent, setManageAgent] = useState<string | null>(null);
  const [concurrencyAgent, setConcurrencyAgent] = useState<string | null>(null);
  const [showAddAgent, setShowAddAgent] = useState(false);
  const [searchParams] = useSearchParams();
  const focusContainerId = searchParams.get("focus");

  const {
    data: agents,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["agents"],
    queryFn: () => client.listAgents(),
    refetchInterval: 30_000,
  });

  const { data: registeredContainers } = useQuery({
    queryKey: ["registered-containers"],
    queryFn: () => client.listRegisteredRuntimes(),
  });

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
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
          title="Failed to load fleet"
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
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Fleet</h2>
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
          description="Run the agent binary on a machine with Docker to add it to the fleet."
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
