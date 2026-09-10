import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  Box,
  ChevronDown,
  ChevronRight,
  Cpu,
  Grid2x2,
  Hash,
  MemoryStick,
  Monitor,
  PackageOpen,
  Pencil,
  Search,
  Server,
  Terminal,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { Dialog } from "../../components/ui/Dialog";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Tooltip,
  Input,
} from "../../components/ui";
import type {
  Agent,
  RegisteredRuntime,
  Settings,
} from "../../lib/api/types";
import {
  AGENT_STATUS_VARIANT,
  agentConnectivity,
  formatMb,
  formatOsPlatform,
  runtimeStatusFor,
  runtimeStatusForScript,
} from "./helpers";
import { RegisteredContainerCard } from "./RegisteredContainerCard";
import { AgentMetricsBar } from "./AgentMetricsBar";


// ─── OS icon ──────────────────────────────────────────────────────

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

// ─── Agent section ────────────────────────────────────────────────

export function AgentSection({
  agent,
  registeredContainers,
  defaultExpanded,
  focusContainerId,
  onManage,
  onConcurrency,
  settings,
  onExpandedChange,
}: {
  agent: Agent;
  registeredContainers: RegisteredRuntime[];
  defaultExpanded: boolean;
  /** When a registered container on this agent is the deep-link target. */
  focusContainerId: string | null;
  onManage: (agentName: string) => void;
  onConcurrency: (agentName: string) => void;
  settings?: Settings;
  onExpandedChange?: (expanded: boolean) => void;
}) {
  const agentRcs = registeredContainers.filter((rc) => rc.agent === agent.name);
  // Deep-link focus forces this section open even if it normally starts collapsed.
  const [expanded, setExpanded] = useState(
    defaultExpanded ||
      (focusContainerId !== null && agentRcs.some((rc) => rc.id === focusContainerId)),
  );

  useEffect(() => {
    onExpandedChange?.(expanded);
  }, [expanded, onExpandedChange]);

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

        {/* Rename agent display name — sibling to the toggle button to avoid nesting */}
        <button
          type="button"
          onClick={() => {
            setEditingAgentName(agent.name);
            setAgentNameDraft(settings?.agentDisplayNames?.[agent.name] ?? agent.name);
          }}
          aria-label={`Rename agent ${agent.name}`}
          title="Edit display name"
          className="flex shrink-0 cursor-pointer items-center rounded p-0.5 text-[var(--color-text-muted)] opacity-0 transition-opacity hover:text-[var(--color-text)] group-hover:opacity-100"
        >
          <Pencil className="size-3" />
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
              {agent.telemetry && <AgentMetricsBar telemetry={agent.telemetry} />}
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
                            containerMetrics={
                              agent.telemetry?.containers?.[rc.runtimeContainerId ?? ""]
                            }
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
