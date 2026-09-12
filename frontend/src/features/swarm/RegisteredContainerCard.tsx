import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { motion } from "motion/react";
import {
  AlertTriangle,
  FileCode,
  Gauge,
  Pencil,
  Play,
  RefreshCw,
  RotateCw,
  Square,
  Stethoscope,
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
  Tooltip,
  ConfirmDialog,
  Input,
} from "../../components/ui";
import type {
  Model,
  RegisteredRuntime,
  UpdateRuntimePayload,
  ContainerMetrics,
} from "../../lib/api/types";
import { enumLabel } from "../../i18n/enum-map";
import {
  REG_STATUS_VARIANT,
  REG_TRANSITIONAL,
  getRuntimeLabel,
  runtimeSignal,
  relativeTime,
  benchDisabledTooltip,
} from "./helpers";
import type { RuntimeSignal } from "./helpers";
import { ContainerKPIBadges } from "./ContainerKPIBadges";

// ─── Discovered model chip ────────────────────────────────────────

function ModelChip({ model }: { model: Model }) {
  const { t } = useTranslation('swarm');
  const validating = model.status === "validating";
  const conflicted = model.status === "conflict";
  return (
    <Tooltip
      content={
        validating
          ? t('model.validating')
          : model.status === "invalid"
            ? t('model.invalid')
            : model.status === "deprecated"
              ? t('model.deprecated')
              : conflicted
                ? t('model.conflict')
                : t('model.ready')
      }
    >
      <span
        className={`
          inline-flex items-center gap-1.5 rounded-[var(--radius-md)] border px-1.5 py-0.5
          font-mono text-[10px] leading-none
          ${
            validating
              ? "border-[color-mix(in_srgb,var(--color-status-warning)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-warning)_12%,transparent)] text-[var(--color-status-warning)]"
              : model.status === "invalid" || conflicted
                ? "border-[color-mix(in_srgb,var(--color-status-error)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)] text-[var(--color-status-error)]"
                : model.status === "deprecated"
                  ? "border-[var(--color-border)] bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]"
                  : "border-[color-mix(in_srgb,var(--color-status-running)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-running)_10%,transparent)] text-[var(--color-status-running)]"
          }
        `}
      >
        <StatusDot status={model.status} size="sm" />
        {model.sourceRuntimeName && (
          <span className="opacity-60">{model.sourceRuntimeName} /</span>
        )}
        <span className="truncate">{model.displayName || model.name}</span>
        {model.status !== "ready" && (
          <span className="uppercase tracking-wide opacity-80">
            {validating ? enumLabel('modelStatus', 'validating') : model.status}
          </span>
        )}
      </span>
    </Tooltip>
  );
}

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
  const { t } = useTranslation('swarm');
  const { t: tc } = useTranslation('common');
  const queryClient = useQueryClient();
  const [displayName, setDisplayName] = useState(runtime?.displayName ?? "");
  const [containerPort, setContainerPort] = useState(
    String(runtime?.containerPort ?? 8080),
  );
  const [maxConcurrentInferences, setMaxConcurrentInferences] = useState(
    runtime?.maxConcurrentInferences ?? 1,
  );

  // Sync state when dialog opens or runtime changes
  const prevOpenRef = useRef(false);
  useEffect(() => {
    if (open && !prevOpenRef.current) {
      setDisplayName(runtime?.displayName ?? "");
      setContainerPort(String(runtime?.containerPort ?? 8080));
      setMaxConcurrentInferences(runtime?.maxConcurrentInferences ?? 1);
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

  const parsedPort = Number(containerPort);
  const portValid = Number.isFinite(parsedPort) && parsedPort > 0 && parsedPort <= 65535;
  const hasChanges =
    displayName.trim() !== (runtime?.displayName ?? "") ||
    (portValid && parsedPort !== (runtime?.containerPort ?? 8080)) ||
    (Number.isFinite(maxConcurrentInferences) && maxConcurrentInferences > 0 && maxConcurrentInferences !== (runtime?.maxConcurrentInferences ?? 1));

  const handleSave = () => {
    const slotsValid = Number.isFinite(maxConcurrentInferences) && maxConcurrentInferences > 0;
    if (!runtime || !displayName.trim() || !portValid || !slotsValid) return;
    const payload: UpdateRuntimePayload = { displayName: displayName.trim() };
    if (parsedPort !== (runtime?.containerPort ?? 8080)) {
      payload.containerPort = parsedPort;
    }
    if (maxConcurrentInferences !== (runtime?.maxConcurrentInferences ?? 1)) {
      payload.maxConcurrentInferences = maxConcurrentInferences;
    }
    updateMutation.mutate(payload);
  };

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title={t('runtime.editRuntime')}>
      <div className="space-y-4 p-5">
        <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
          {t('runtime.updateSettings', { name: runtime?.displayName })}
        </p>

        <Input
          label={t('runtime.displayName')}
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          placeholder={t('runtime.placeholders.displayName')}
          aria-label={t('runtime.displayName')}
          autoFocus
        />

        <Input
          label={t('runtime.runtimePort')}
          type="number"
          value={containerPort}
          onChange={(e) => setContainerPort(e.target.value)}
          placeholder={t('runtime.placeholders.port')}
          aria-label={t('runtime.runtimePort')}
        />

        <Input
          label={t('runtime.parallelSlots')}
          type="number"
          value={String(maxConcurrentInferences)}
          onChange={(e) => {
            const v = parseInt(e.target.value, 10);
            if (!isNaN(v)) setMaxConcurrentInferences(Math.max(1, Math.min(128, v)));
          }}
          placeholder={t('runtime.placeholders.parallelLanes')}
          aria-label={t('runtime.parallelSlots')}
        />

        {updateMutation.isError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{updateMutation.error.message}</span>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={onClose}>
            {tc('cancel')}
          </Button>
          <Button
            size="sm"
            loading={updateMutation.isPending}
            disabled={!hasChanges}
            onClick={handleSave}
          >
            {tc('save')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

// ─── Registered container card ────────────────────────────────────

export function RegisteredContainerCard({
  container,
  highlight = false,
  runtimeStatus = null,
  containerMetrics,
}: {
  container: RegisteredRuntime;
  /** When true, briefly ring the card (deep-link focus). */
  highlight?: boolean;
  /** Runtime docker status from the owning agent's telemetry (may be null = unknown). */
  runtimeStatus?: string | null;
  /** Live CPU/RAM metrics from agent telemetry. */
  containerMetrics?: ContainerMetrics;
}) {
  const { t } = useTranslation('swarm');
  const { t: tc } = useTranslation('common');
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
  const [healthCheckError, setHealthCheckError] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
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
    onMutate: () => {
      setStartError(null);
    },
    onSuccess: () => {
      setStartError(null);
      invalidate();
    },
    onError: (err: Error) => {
      setStartError(err.message || t('container.startFailed'));
      invalidate();
    },
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
      setRediscoverError(err.message || t('container.rediscoverFailed'));
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

  const healthCheckMutation = useMutation({
    mutationFn: () => client.healthCheckRuntime(container.id),
    onMutate: () => {
      setHealthCheckError(null);
    },
    onSuccess: () => {
      setHealthCheckError(null);
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      queryClient.invalidateQueries({ queryKey: ["agents"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (err: Error) => {
      setHealthCheckError(err.message || t('container.healthCheckFailed'));
    },
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

  const firstModel = container.discoveredModels[0];
  const canBenchmark = !!firstModel && firstModel.status === "ready";
  const transitional = REG_TRANSITIONAL.has(container.status);
  // For scripts, the backend DB status is the authoritative lifecycle signal.
  // Agent telemetry is polled every 30s and may be stale during startup/shutdown.
  const isScript = container.runtimeKind === "script";
  const signal = isScript
    ? (() => {
        const backendStatus = (container.status ?? "").toLowerCase();
        if (backendStatus === "starting") return "transitional" as RuntimeSignal;
        if (backendStatus === "error") return "down" as RuntimeSignal;
        if (backendStatus === "registered") return "down" as RuntimeSignal;
        // For "ready": prefer agent telemetry when available, fallback to "running"
        const ts = runtimeSignal(runtimeStatus);
        if (ts === "running" || ts === "down") return ts;
        return "running" as RuntimeSignal;
      })()
    : runtimeSignal(runtimeStatus);
  const busy =
    startMutation.isPending ||
    stopMutation.isPending ||
    restartMutation.isPending ||
    rediscoverMutation.isPending ||
    deleteMutation.isPending ||
    stopScriptMutation.isPending ||
    healthCheckMutation.isPending;

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
            <Tooltip content={getRuntimeLabel(signal)}>
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
                {t('runtime.scriptBadge')}
              </Badge>
            )}
            <Badge variant={REG_STATUS_VARIANT[container.status]}>
              {enumLabel('containerRegistrationStatus', container.status)}
            </Badge>
          </div>
        </div>

        <ContainerKPIBadges metrics={containerMetrics} />

        {/* Metrics */}
        <div className="grid grid-cols-4 gap-2 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] px-2.5 py-2 text-[10px]">
          <div>
            <p className="text-[var(--color-text-muted)]">{t('runtime.port')}</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              {container.mappedPort ?? container.containerPort}
            </p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)]">{t('runtime.models')}</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              {container.discoveredModels.length > 0 ? container.discoveredModels.length : "—"}
            </p>
          </div>
          <div>
            <p className="text-[var(--color-text-muted)]">{t('runtime.parallelSlots')}</p>
            <p className="font-mono text-[var(--color-text-heading)]">
              {container.maxConcurrentInferences}
            </p>
          </div>
          <div className="min-w-0">
            <p className="text-[var(--color-text-muted)]">{t('runtime.discovered')}</p>
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
            {t('runtime.noModels')}{transitional ? "" : ""}.
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
              {signal === "down" && t('container.stoppedHint')}
            </span>
            <button
              type="button"
              onClick={() => setRediscoverError(null)}
              aria-label={t('container.errors.dismissRediscover')}
              className="shrink-0 rounded-[var(--radius-sm)] p-0.5 text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_14%,transparent)]"
            >
              <X className="size-3" />
            </button>
          </div>
        )}

        {healthCheckError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="min-w-0 flex-1 truncate">{healthCheckError}</span>
            <button
              type="button"
              onClick={() => setHealthCheckError(null)}
              aria-label={t('container.errors.dismissHealthCheck')}
              className="shrink-0 rounded-[var(--radius-sm)] p-0.5 text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_14%,transparent)]"
            >
              <X className="size-3" />
            </button>
          </div>
        )}

        {startError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="min-w-0 flex-1 truncate">{startError}</span>
            <button
              type="button"
              onClick={() => setStartError(null)}
              aria-label={t('container.errors.dismissStart')}
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
                {t('runtime.benchmark')}
              </Button>
            </span>
          </Tooltip>
          {benchmark && (
            <span
              className="inline-flex items-center gap-1 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-running)_12%,transparent)] px-1.5 py-0.5 font-mono text-[10px] text-[var(--color-status-running)]"
              title={t('runtime.lastBenchmark')}
            >
              <Zap className="size-2.5" />
              {Number(benchmark.tokensPerSec).toFixed(2)} {tc('units.tokPerSec')} · {Number(benchmark.latencyMs).toFixed(2)}{tc('units.ms')}
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
                title={t('runtime.stopScript')}
              >
                <Square className="size-3" />
                {t('runtime.stop')}
              </Button>
            ) : signal === "down" || signal === "unknown" ? (
              <Button
                variant="primary"
                size="sm"
                disabled={busy}
                loading={startMutation.isPending}
                onClick={() => startMutation.mutate(container.id)}
                title={t('runtime.startScript')}
              >
                <Play className="size-3" />
                {t('runtime.start')}
              </Button>
            ) : signal === "transitional" ? (
              <>
                <span className="text-[10px] italic text-[var(--color-text-muted)]">
                  {getRuntimeLabel(signal)}
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  loading={stopScriptMutation.isPending}
                  onClick={() => stopScriptMutation.mutate(container.id)}
                  title={t('runtime.stopScriptStarting')}
                >
                  <Square className="size-3" />
                  {t('runtime.stop')}
                </Button>
              </>
            ) : null
          ) : signal === "running" ? (
            <>
              <Button
                variant="ghost"
                size="sm"
                disabled={!container.runtimeContainerId || busy}
                loading={restartMutation.isPending}
                onClick={() => container.runtimeContainerId && restartMutation.mutate(container.runtimeContainerId)}
                title={t('runtime.restartRuntime')}
              >
                <RotateCw className="size-3" />
                {t('runtime.restart')}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                disabled={!container.runtimeContainerId || busy}
                loading={stopMutation.isPending}
                onClick={() => container.runtimeContainerId && stopMutation.mutate(container.runtimeContainerId)}
                title={t('runtime.stopRuntime')}
              >
                <Square className="size-3" />
                {t('runtime.stop')}
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
              title={t('runtime.startRuntime')}
            >
              <Play className="size-3" />
              {t('runtime.start')}
            </Button>
          ) : (
            // Transitional (starting/stopping/created/restarting) — no lifecycle action.
            <span className="text-[10px] italic text-[var(--color-text-muted)]">
              {getRuntimeLabel(signal)}
            </span>
          )}
          {signal !== "down" && signal !== "unknown" && (
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              loading={healthCheckMutation.isPending}
              onClick={() => healthCheckMutation.mutate()}
              title={t('runtime.healthCheck')}
            >
              <Stethoscope className="size-3" />
              {t('runtime.checkHealth')}
            </Button>
          )}
          <div className="ml-auto flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              onClick={() => setEditingName(true)}
              title={t('runtime.editSettings')}
            >
              <Pencil className="size-3" />
              {tc('edit')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              loading={rediscoverMutation.isPending}
              onClick={() => rediscoverMutation.mutate(container.id)}
              title={t('runtime.rediscoverModels')}
            >
              <RefreshCw className="size-3" />
              {t('runtime.rediscover')}
            </Button>
            {confirmingDelete ? (
              <ConfirmDialog
                open={confirmingDelete}
                title={t('runtime.confirmDelete', { name: container.displayName })}
                description={t('runtime.deleteRegistrationDesc')}
                confirmLabel={tc('delete')}
                variant="danger"
                loading={deleteMutation.isPending}
                onConfirm={() => {
                  deleteMutation.mutate(container.id);
                  setConfirmingDelete(false);
                }}
                onCancel={() => setConfirmingDelete(false)}
              />
            ) : (
              <Button
                variant="ghost"
                size="sm"
                disabled={busy}
                onClick={() => setConfirmingDelete(true)}
                aria-label={t('runtime.deleteRegistrationAria', { name: container.displayName })}
                title={t('runtime.deleteRegistration')}
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
