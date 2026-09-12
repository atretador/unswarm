import type {
  Agent,
  AgentScriptStatus,
  Container,
  Model,
  RegisteredRuntime,
} from "../../lib/api/types";
import { enumLabel } from "../../i18n/enum-map";
import i18n from "../../i18n";
import { formatRelativeTime } from "../../i18n/format";

// ─── Status semantics ─────────────────────────────────────────────

export const REG_STATUS_VARIANT: Record<string, "success" | "info" | "error" | "warning" | "default"> = {
  registered: "info",
  starting: "info",
  healthy: "success",
  discovering: "info",
  ready: "success",
  error: "error",
};

export type AgentConnectivity = "connected" | "stale" | "disconnected";

export const AGENT_STATUS_VARIANT: Record<AgentConnectivity, "success" | "warning" | "error"> = {
  connected: "success",
  stale: "warning",
  disconnected: "error",
};

export const REG_TRANSITIONAL = new Set<string>([
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

export function runtimeSignal(status: string | null | undefined): RuntimeSignal {
  const s = (status ?? "").toLowerCase();
  if (s === "running") return "running";
  if (RUNTIME_TRANSITIONAL.has(s)) return "transitional";
  if (RUNTIME_DOWN.has(s)) return "down";
  return "unknown";
}

/** Translated runtime signal label. */
export function getRuntimeLabel(signal: string): string {
  return enumLabel('runtimeSignal', signal);
}

/** Find the runtime telemetry status for a registered container on its agent. */
export function runtimeStatusFor(
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

/** Find the runtime telemetry status for a registered script on its agent. */
export function runtimeStatusForScript(
  agentScripts: AgentScriptStatus[],
  rc: RegisteredRuntime,
): string | null {
  // Primary match: registrationId (stable, set by backend when starting script)
  if (rc.id) {
    const byId = agentScripts.find((s) => s.registrationId === rc.id);
    if (byId) return byId.status;
  }
  // Fallback: match by port (script runtimes have a unique port)
  if (rc.mappedPort) {
    const byPort = agentScripts.find((s) => s.port === rc.mappedPort);
    if (byPort) return byPort.status;
  }
  // Last resort: path matching (original behavior, for backward compat)
  const path = rc.launcherPath?.toLowerCase();
  if (!path) return null;
  return agentScripts.find((s) => s.path.toLowerCase() === path)?.status ?? null;
}

// ─── Formatting helpers ───────────────────────────────────────────

export function formatMb(mb: number): string {
  if (!mb) return "—";
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} ${i18n.t('swarm:metrics.gb')}`;
  return `${mb} ${i18n.t('swarm:metrics.mb')}`;
}

export function formatOsPlatform(platform: string): string {
  const lower = platform.toLowerCase();
  if (lower.includes("linux")) return i18n.t('swarm:platform.linux');
  if (lower.includes("windows")) return i18n.t('swarm:platform.windows');
  if (lower.includes("darwin") || lower.includes("mac")) return i18n.t('swarm:platform.macos');
  return platform;
}

export function agentConnectivity(agent: Agent): AgentConnectivity {
  // The backend's isConnected flag is authoritative for the connected state —
  // lastSeen may be null on a freshly-connected agent.
  if (agent.isConnected) return "connected";
  if (!agent.lastSeen) return "disconnected";
  const ageMs = Date.now() - new Date(agent.lastSeen).getTime();
  if (ageMs < 120_000) return "stale";
  return "disconnected";
}

export function relativeTime(iso: string | null): string {
  if (!iso) return "—";
  return formatRelativeTime(iso);
}

/** A container counts as "already registered" when name/id matches image or runtimeContainerId (case-insensitive, matching backend OrdinalIgnoreCase). */
export function isContainerRegistered(rcs: RegisteredRuntime[], agentName: string, c: Container): boolean {
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
export function displayNameFromContainer(c: Container): string {
  return (
    c.modelName
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      || "container"
  );
}

export function benchDisabledTooltip(firstModel: Model | undefined): string {
  if (!firstModel) return i18n.t('swarm:bench.noModels');
  if (firstModel.status === "validating") return i18n.t('swarm:bench.validating', { name: firstModel.name });
  if (firstModel.status === "invalid") return i18n.t('swarm:bench.invalid', { name: firstModel.name });
  if (firstModel.status === "deprecated") return i18n.t('swarm:bench.deprecated', { name: firstModel.name });
  if (firstModel.status === "conflict") return i18n.t('swarm:bench.conflict', { name: firstModel.name });
  return i18n.t('swarm:bench.notReady', { name: firstModel.name });
}

// ─── Constants ───────────────────────────────────────────────────

export const PAGE_SIZE = 9;

export type ManageTab = "containers" | "scripts";
