import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { useSearchParams } from "react-router-dom";
import {
  AlertTriangle,
  Box,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Cpu,
  Gauge,
  KeyRound,
  MemoryStick,
  Monitor,
  PackageOpen,
  Play,
  Plus,
  RefreshCw,
  RotateCw,
  Search,
  Server,
  Square,
  Trash2,
  X,
  Zap,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Skeleton,
  EmptyState,
  Input,
  Tooltip,
} from "../../components/ui";
import type {
  Agent,
  Container,
  ContainerRegistrationStatus,
  Model,
  RegisteredContainer,
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
  rc: RegisteredContainer,
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

// ─── Formatting helpers ───────────────────────────────────────────

function formatMb(mb: number): string {
  if (!mb) return "—";
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb} MB`;
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
function isContainerRegistered(rcs: RegisteredContainer[], agentName: string, c: Container): boolean {
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

// ─── Focus-trap modal shell ───────────────────────────────────────

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function Modal({
  open,
  onClose,
  label,
  children,
}: {
  open: boolean;
  onClose: () => void;
  label: string;
  children: ReactNode;
}) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onClose();
        return;
      }
      if (e.key !== "Tab" || !dialogRef.current) return;
      const focusable = dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey) {
        if (document.activeElement === first) {
          e.preventDefault();
          last.focus();
        }
      } else if (document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    },
    [onClose],
  );

  useEffect(() => {
    if (!open) return;
    previousFocusRef.current = document.activeElement as HTMLElement;
    closeRef.current?.focus();
    document.addEventListener("keydown", handleKeyDown);

    // Lock background scroll while the dialog is open; restore on close.
    const previousOverflow = document.body.style.overflow;
    const previousTouchAction = document.body.style.touchAction;
    document.body.style.overflow = "hidden";
    document.body.style.touchAction = "none";

    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      document.body.style.overflow = previousOverflow;
      document.body.style.touchAction = previousTouchAction;
      previousFocusRef.current?.focus();
    };
  }, [open, handleKeyDown]);

  return (
    <AnimatePresence>
      {open && (
        <div className="fixed inset-0 z-50 flex items-end justify-center sm:items-center sm:p-6">
          {/* Overlay */}
          <motion.div
            key="overlay"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.18 }}
            className="absolute inset-0 bg-[var(--color-bg-overlay)] backdrop-blur-[2px]"
            onClick={onClose}
            aria-hidden="true"
          />
          {/* Dialog: bottom sheet on mobile, centered card on desktop */}
          <motion.div
            key="dialog"
            ref={dialogRef}
            role="dialog"
            aria-modal="true"
            aria-label={label}
            initial={{ opacity: 0, y: 32, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 24, scale: 0.98 }}
            transition={{ duration: 0.22, ease: [0.16, 1, 0.3, 1] }}
            className={`
              relative flex max-h-[92dvh] w-full flex-col overflow-hidden
              rounded-t-[var(--radius-2xl)] sm:rounded-[var(--radius-2xl)]
              border border-[var(--color-border)] bg-[var(--color-bg-surface)]
              shadow-xl sm:max-w-2xl
            `}
          >
            <div className="flex items-center justify-between gap-4 border-b border-[var(--color-border-subtle)] px-5 py-4">
              <h3 className="font-heading text-sm font-semibold text-[var(--color-text-heading)]">
                {label}
              </h3>
              <button
                ref={closeRef}
                onClick={onClose}
                aria-label="Close dialog"
                className="flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
              >
                <X className="size-4" />
              </button>
            </div>
            <div className="flex-1 overflow-y-auto">{children}</div>
          </motion.div>
        </div>
      )}
    </AnimatePresence>
  );
}

// ─── Add agent modal ──────────────────────────────────────────────

function AddAgentModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Modal open={open} onClose={onClose} label="Add an agent">
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
    </Modal>
  );
}

// ─── Manage containers modal ──────────────────────────────────────

const PAGE_SIZE = 9;

function ManageContainersModal({
  agentName,
  open,
  onClose,
  registered,
}: {
  agentName: string;
  open: boolean;
  onClose: () => void;
  registered: RegisteredContainer[];
}) {
  // Remount the body whenever the modal opens (or targets a different agent)
  // so filter/page/selection always start fresh.
  return (
    <Modal open={open} onClose={onClose} label={`Manage containers on ${agentName}`}>
      <ManageContainersBody
        key={open ? `${agentName}:${open}` : "closed"}
        agentName={agentName}
        onClose={onClose}
        registered={registered}
      />
    </Modal>
  );
}

function ManageContainersBody({
  agentName,
  onClose,
  registered,
}: {
  agentName: string;
  onClose: () => void;
  registered: RegisteredContainer[];
}) {
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState("");
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");

  const { data: containers, isLoading, error } = useQuery({
    queryKey: ["agent-containers", agentName],
    queryFn: () => client.listAgentContainers(agentName),
    staleTime: 15_000,
  });

  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; image: string }) =>
      client.registerContainer({
        displayName: payload.displayName,
        image: payload.image,
        containerPort: 8080,
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
  };

  const confirmRegister = () => {
    if (!selected || !displayName.trim()) return;
    registerMutation.mutate({
      displayName: displayName.trim(),
      image: selected.modelName || selected.id,
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
                      group relative flex flex-col gap-2 rounded-[var(--radius-xl)] border p-3 text-left
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
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate font-mono text-xs text-[var(--color-text-heading)]">
                        {c.modelName}
                      </span>
                      {already ? (
                        <Badge variant="success" className="shrink-0 gap-1">
                          <PackageOpen className="size-2.5" />
                          registered
                        </Badge>
                      ) : selectedCard ? (
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
                    <div className="grid grid-cols-2 gap-x-3 gap-y-1 text-[10px] text-[var(--color-text-muted)]">
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

        {/* Inline confirm: display name + register */}
        <AnimatePresence>
          {selected && (
            <motion.div
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: 8 }}
              transition={{ duration: 0.18 }}
              className="space-y-3 rounded-[var(--radius-xl)] border border-[var(--color-primary)] bg-[var(--color-bg-muted)] p-3.5"
            >
              <div className="flex items-center justify-between gap-2">
                <p className="text-xs font-medium text-[var(--color-text-heading)]">
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
              <div className="grid gap-2 sm:grid-cols-[1fr_auto_1fr] sm:items-center">
                <Input
                  label="Display name"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  placeholder="my-model-server"
                  aria-label="Display name"
                />
                <span className="hidden text-center text-[10px] text-[var(--color-text-muted)] sm:block">
                  →
                </span>
                <div className="flex flex-col gap-1">
                  <span className="text-xs font-medium text-[var(--color-text-muted)]">Image</span>
                  <code className="h-8 truncate rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-1.5 font-mono text-xs text-[var(--color-text)]">
                    {selected.modelName}
                  </code>
                </div>
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
  container: RegisteredContainer;
  /** When true, briefly ring the card (deep-link focus). */
  highlight?: boolean;
  /** Runtime docker status from the owning agent's telemetry (may be null = unknown). */
  runtimeStatus?: string | null;
}) {
  const queryClient = useQueryClient();
  const [benchmark, setBenchmark] = useState<{
    tokensPerSec: number;
    latencyMs: number;
  } | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [ringActive, setRingActive] = useState(highlight);
  const [rediscoverError, setRediscoverError] = useState<string | null>(null);

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
    mutationFn: (id: string) => client.startRegisteredContainer(id),
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
    mutationFn: (id: string) => client.rediscoverContainer(id),
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
    mutationFn: (id: string) => client.deleteRegisteredContainer(id),
    onSuccess: invalidate,
  });

  const benchmarkMutation = useMutation({
    mutationFn: (modelId: string) => client.runBenchmark(modelId),
    onSuccess: (result) => {
      setBenchmark({ tokensPerSec: result.tokensPerSec, latencyMs: result.latencyMs });
    },
  });

  const firstModel = container.discoveredModels[0];
  const canBenchmark = !!firstModel && firstModel.status === "ready";
  const transitional = REG_TRANSITIONAL.has(container.status);
  const signal = runtimeSignal(runtimeStatus);
  const busy =
    startMutation.isPending ||
    stopMutation.isPending ||
    restartMutation.isPending ||
    rediscoverMutation.isPending ||
    deleteMutation.isPending;

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
          flex h-full flex-col gap-3 transition-shadow duration-500
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
              <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
                {container.displayName}
              </p>
              <p className="truncate text-[10px] text-[var(--color-text-muted)]">
                {container.image}
              </p>
            </div>
          </div>
          <Badge variant={REG_STATUS_VARIANT[container.status]} className="shrink-0">
            {container.status}
          </Badge>
        </div>

        {/* Metrics */}
        <div className="grid grid-cols-3 gap-2 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] px-2.5 py-2 text-[10px]">
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
          <div>
            <p className="text-[var(--color-text-muted)]">Discovered</p>
            <p className="font-mono text-[var(--color-text-heading)]">
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
            </span>
          )}
          <span className="mx-0.5 hidden h-4 w-px bg-[var(--color-border)] sm:block" />
          {signal === "running" ? (
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
  onAddAgent,
}: {
  agent: Agent;
  registeredContainers: RegisteredContainer[];
  defaultExpanded: boolean;
  /** When a registered container on this agent is the deep-link target. */
  focusContainerId: string | null;
  onManage: (agentName: string) => void;
  onAddAgent: () => void;
}) {
  const agentRcs = registeredContainers.filter((rc) => rc.agent === agent.name);
  // Deep-link focus forces this section open even if it normally starts collapsed.
  const [expanded, setExpanded] = useState(
    defaultExpanded ||
      (focusContainerId !== null && agentRcs.some((rc) => rc.id === focusContainerId)),
  );
  const connectivity = agentConnectivity(agent);
  const isHost = agent.name === "host";
  const focusedCardRef = useRef<HTMLDivElement | null>(null);

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
            {agent.name}
          </span>
          {isHost && <Badge variant="outline">host</Badge>}

          <div className="hidden min-w-0 items-center gap-3 text-[10px] text-[var(--color-text-muted)] md:flex">
            {agent.hostname && (
              <span className="flex min-w-0 items-center gap-1">
                <Monitor className="size-3 shrink-0" />
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
                <Server className="size-3 shrink-0" />
                {agent.cpuCores} cores
              </span>
            )}
          </div>
        </button>

        <Badge variant={AGENT_STATUS_VARIANT[connectivity]} className="shrink-0">
          {connectivity}
        </Badge>

        <div className="flex shrink-0 items-center gap-1">
          <button
            type="button"
            onClick={() => onManage(agent.name)}
            aria-label={`Manage containers on ${agent.name}`}
            title="Manage containers"
            className="flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
          >
            <PackageOpen className="size-3.5" />
          </button>
          {isHost && (
            <Tooltip content="Run the agent binary on another machine and it joins this fleet.">
              <Button variant="ghost" size="sm" onClick={onAddAgent} className="shrink-0">
                <Plus className="size-3" />
                Add agent
              </Button>
            </Tooltip>
          )}
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
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {agentRcs.map((rc) => {
                    const focused = focusContainerId === rc.id;
                    return (
                      <div key={rc.id} ref={focused ? focusedCardRef : undefined}>
                        <RegisteredContainerCard
                          container={rc}
                          highlight={focused}
                          runtimeStatus={runtimeStatusFor(agent.containers, rc)}
                        />
                      </div>
                    );
                  })}
                </div>
              ) : (
                <Card
                  padding="md"
                  className="flex flex-col items-center gap-3 border-dashed py-10 text-center"
                >
                  <div className="flex size-10 items-center justify-center rounded-[var(--radius-xl)] bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]">
                    <Box className="size-5" strokeWidth={1.5} />
                  </div>
                  <div>
                    <p className="text-sm font-medium text-[var(--color-text-heading)]">
                      No containers registered
                    </p>
                    <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
                      Register a running container to auto-discover its models.
                    </p>
                  </div>
                  <Button variant="secondary" size="sm" onClick={() => onManage(agent.name)}>
                    Manage containers
                  </Button>
                </Card>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </section>
  );
}

// ─── Main Fleet page ──────────────────────────────────────────────

export default function Fleet() {
  const [manageAgent, setManageAgent] = useState<string | null>(null);
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
    queryFn: () => client.listRegisteredContainers(),
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
            Manage registered containers and run model discovery across agents.
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
              onAddAgent={() => setShowAddAgent(true)}
            />
          ))}
        </div>
      )}

      {/* Modals */}
      <ManageContainersModal
        agentName={manageAgent ?? ""}
        open={manageAgent !== null}
        onClose={() => setManageAgent(null)}
        registered={registeredContainers ?? []}
      />
      <AddAgentModal open={showAddAgent} onClose={() => setShowAddAgent(false)} />
    </div>
  );
}
