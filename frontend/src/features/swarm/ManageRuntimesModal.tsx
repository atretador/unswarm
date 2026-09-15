import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { AnimatePresence, motion } from "motion/react";
import {
  Box,
  ChevronLeft,
  ChevronRight,
  MemoryStick,
  PackageOpen,
  Plus,
  RefreshCw,
  Search,
  Terminal,
  X,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Badge,
  Button,
  Dialog,
  EmptyState,
  Input,
  Skeleton,
  StatusDot,
} from "../../components/ui";
import type {
  Container,
  RegisteredRuntime,
} from "../../lib/api/types";
import { HostScriptUpload, AgentScriptUpload } from "./ScriptEditor";
import { enumLabel } from "../../i18n/enum-map";
import {
  PAGE_SIZE,
  isContainerRegistered,
  displayNameFromContainer,
  formatMb,
} from "./helpers";
import type { ManageTab } from "./helpers";

// ─── Create container dialog ─────────────────────────────────────

function CreateContainerForm({
  agentName,
  open,
  onOpenChange,
}: {
  agentName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const { t } = useTranslation('swarm');
  const { t: tc } = useTranslation('common');
  const queryClient = useQueryClient();

  // Form state
  const [image, setImage] = useState("");
  const [name, setName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [containerPort, setContainerPort] = useState("8080");
  const [hostPort, setHostPort] = useState("");
  const [devices, setDevices] = useState("/dev/kfd\n/dev/dri/renderD128\n/dev/dri/card0");
  const [volumes, setVolumes] = useState("");
  const [envVars, setEnvVars] = useState("");
  const [shmSize, setShmSize] = useState("16384");
  const [ipcMode, setIpcMode] = useState("host");
  const [network, setNetwork] = useState("bridge");
  const [restartPolicy, setRestartPolicy] = useState("unless-stopped");
  const [serverArgs, setServerArgs] = useState("");

  const resetForm = () => {
    setImage("");
    setName("");
    setDisplayName("");
    setContainerPort("8080");
    setHostPort("");
    setDevices("/dev/kfd\n/dev/dri/renderD128\n/dev/dri/card0");
    setVolumes("");
    setEnvVars("");
    setShmSize("16384");
    setIpcMode("host");
    setNetwork("bridge");
    setRestartPolicy("unless-stopped");
    setServerArgs("");
  };

  const createMutation = useMutation({
    mutationFn: (payload: Parameters<typeof client.createContainer>[0]) =>
      client.createContainer(payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      resetForm();
      onOpenChange(false);
    },
  });

  // Auto-derive name from image slug
  const deriveName = (img: string) => {
    const slug = img.split("/").pop()?.split(":")[0] ?? "";
    return slug.replace(/[^a-zA-Z0-9_-]/g, "").toLowerCase();
  };

  const derivedName = name.trim() || deriveName(image);
  const derivedDisplayName = displayName.trim() || derivedName;

  const handleSubmit = () => {
    if (!image.trim() || !derivedName) return;

    const devicesList = devices.split("\n").map((d) => d.trim()).filter(Boolean);
    const volumesList = volumes.split("\n").map((v) => v.trim()).filter(Boolean).map((v) => {
      const parts = v.split(":");
      const isReadonly = parts.length === 3 && parts[2] === "ro";
      return {
        host: parts[0] ?? "",
        container: parts[1] ?? parts[0] ?? "",
        ...(isReadonly ? { readonly: true } : {}),
      };
    });
    const envList = envVars.split("\n").map((e) => e.trim()).filter(Boolean).map((e) => {
      const eqIdx = e.indexOf("=");
      return eqIdx > -1
        ? { key: e.slice(0, eqIdx), value: e.slice(eqIdx + 1) }
        : { key: e, value: "" };
    });
    const serverArgsList = serverArgs.split("\n").map((a) => a.trim()).filter(Boolean);
    const hp = parseInt(hostPort, 10);

    createMutation.mutate({
      image: image.trim(),
      name: derivedName,
      displayName: derivedDisplayName,
      dockerParams: {
        containerName: derivedName,
        containerPort: parseInt(containerPort, 10) || 8080,
        ...(hp > 0 ? { hostPort: hp } : {}),
        ...(devicesList.length > 0 ? { devices: devicesList } : {}),
        ...(volumesList.length > 0 ? { volumes: volumesList } : {}),
        ...(envList.length > 0 ? { env: envList } : {}),
        shmSizeMb: parseInt(shmSize, 10) || 16384,
        ipcMode: ipcMode || "host",
        networkMode: network || "bridge",
        restartPolicy: restartPolicy || "unless-stopped",
        ...(serverArgsList.length > 0 ? { serverArgs: serverArgsList } : {}),
      },
      agent: agentName,
      detach: true,
    });
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={t('create.title')}>
      <div className="space-y-4 p-5">
        <Input
          label={t('create.image')}
          value={image}
          onChange={(e) => {
            setImage(e.target.value);
            if (!name.trim()) setName(deriveName(e.target.value));
          }}
          placeholder={t('create.imagePlaceholder')}
          aria-label={t('create.image')}
          required
        />
        <Input
          label={t('create.name')}
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t('create.namePlaceholder')}
          aria-label={t('create.name')}
        />
        <Input
          label={t('create.displayName')}
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          placeholder={derivedName || t('create.namePlaceholder')}
          aria-label={t('create.displayName')}
        />
        <div className="grid grid-cols-2 gap-2.5">
          <Input
            label={t('create.containerPort')}
            type="number"
            value={containerPort}
            onChange={(e) => setContainerPort(e.target.value)}
            aria-label={t('create.containerPort')}
          />
          <Input
            label={t('create.hostPort')}
            type="number"
            value={hostPort}
            onChange={(e) => setHostPort(e.target.value)}
            placeholder="auto"
            aria-label={t('create.hostPort')}
          />
        </div>

        {/* Devices */}
        <div className="flex flex-col gap-1">
          <label className="text-xs font-medium text-[var(--color-text-muted)]">
            {t('create.devices')}
          </label>
          <textarea
            value={devices}
            onChange={(e) => setDevices(e.target.value)}
            placeholder={t('create.devicesHint')}
            rows={3}
            className="rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors duration-[var(--duration-fast)] resize-none font-mono"
          />
          <span className="text-[10px] leading-tight text-[var(--color-text-muted)]">
            {t('create.devicesHint')}
          </span>
        </div>

        {/* Volumes */}
        <div className="flex flex-col gap-1">
          <label className="text-xs font-medium text-[var(--color-text-muted)]">
            {t('create.volumes')}
          </label>
          <textarea
            value={volumes}
            onChange={(e) => setVolumes(e.target.value)}
            placeholder={t('create.volumesHint')}
            rows={2}
            className="rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors duration-[var(--duration-fast)] resize-none font-mono"
          />
          <span className="text-[10px] leading-tight text-[var(--color-text-muted)]">
            {t('create.volumesHint')}
          </span>
        </div>

        {/* Environment Variables */}
        <div className="flex flex-col gap-1">
          <label className="text-xs font-medium text-[var(--color-text-muted)]">
            {t('create.envVars')}
          </label>
          <textarea
            value={envVars}
            onChange={(e) => setEnvVars(e.target.value)}
            placeholder={t('create.envVarsHint')}
            rows={2}
            className="rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors duration-[var(--duration-fast)] resize-none font-mono"
          />
          <span className="text-[10px] leading-tight text-[var(--color-text-muted)]">
            {t('create.envVarsHint')}
          </span>
        </div>

        <div className="grid grid-cols-3 gap-2.5">
          <Input
            label={t('create.shmSize')}
            type="number"
            value={shmSize}
            onChange={(e) => setShmSize(e.target.value)}
            aria-label={t('create.shmSize')}
          />
          <Input
            label={t('create.ipcMode')}
            value={ipcMode}
            onChange={(e) => setIpcMode(e.target.value)}
            aria-label={t('create.ipcMode')}
          />
          <Input
            label={t('create.network')}
            value={network}
            onChange={(e) => setNetwork(e.target.value)}
            aria-label={t('create.network')}
          />
        </div>
        <Input
          label={t('create.restartPolicy')}
          value={restartPolicy}
          onChange={(e) => setRestartPolicy(e.target.value)}
          aria-label={t('create.restartPolicy')}
        />

        {/* Server Args */}
        <div className="flex flex-col gap-1">
          <label className="text-xs font-medium text-[var(--color-text-muted)]">
            {t('create.serverArgs')}
          </label>
          <textarea
            value={serverArgs}
            onChange={(e) => setServerArgs(e.target.value)}
            placeholder={t('create.serverArgsHint')}
            rows={2}
            className="rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors duration-[var(--duration-fast)] resize-none font-mono"
          />
          <span className="text-[10px] leading-tight text-[var(--color-text-muted)]">
            {t('create.serverArgsHint')}
          </span>
        </div>

        {/* Error */}
        {createMutation.isError && (
          <div className="rounded-[var(--radius-lg)] border border-[var(--color-status-error)] bg-[var(--color-status-error)]/10 px-3 py-2">
            <p className="text-xs text-[var(--color-status-error)]">
              {t('create.error')}: {createMutation.error.message}
            </p>
          </div>
        )}

        {/* Success */}
        {createMutation.isSuccess && (
          <div className="rounded-[var(--radius-lg)] border border-[var(--color-status-success)] bg-[var(--color-status-success)]/10 px-3 py-2">
            <p className="text-xs text-[var(--color-status-success)]">
              {t('create.success')} — {t('runtime.registerOn', { agentName })}
            </p>
          </div>
        )}

        {/* Actions */}
        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={() => onOpenChange(false)}>
            {tc('cancel')}
          </Button>
          <Button
            size="sm"
            loading={createMutation.isPending}
            disabled={!image.trim() || !derivedName}
            onClick={handleSubmit}
          >
            <Plus className="size-3" />
            {t('create.submit', { agentName })}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

// ─── Manage runtimes modal ──────────────────────────────────────

function ManageContainersBody({
  agentName,
  onClose,
  registered,
}: {
  agentName: string;
  onClose: () => void;
  registered: RegisteredRuntime[];
}) {
  const { t } = useTranslation('swarm');
  const { t: tc } = useTranslation('common');
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState("");
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [port, setPort] = useState("8080");
  const [mappedPort, setMappedPort] = useState("");
  const [maxConcurrentInferences, setMaxConcurrentInferences] = useState(1);

  const { data: containers, isLoading, error } = useQuery({
    queryKey: ["agent-containers", agentName],
    queryFn: () => client.listAgentContainers(agentName),
    staleTime: 15_000,
  });

  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; image: string; port: number; mappedPort?: number; maxConcurrentInferences?: number }) =>
      client.registerRuntime({
        displayName: payload.displayName,
        image: payload.image,
        containerPort: payload.port,
        mappedPort: payload.mappedPort,
        agent: agentName,
        maxConcurrentInferences: payload.maxConcurrentInferences,
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
    setMappedPort("");
  };

  const confirmRegister = () => {
    if (!selected || !displayName.trim()) return;
    const mp = parseInt(mappedPort, 10);
    registerMutation.mutate({
      displayName: displayName.trim(),
      image: selected.modelName || selected.id,
      port: parseInt(port, 10) || 8080,
      ...(mp > 0 ? { mappedPort: mp } : {}),
      maxConcurrentInferences: maxConcurrentInferences,
    });
  };

  const go = (p: number) => setPage(Math.min(Math.max(1, p), totalPages));

  return (
    <div className="space-y-4 p-5">
      <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
        {t('runtime.registerOn', { agentName })}
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
            placeholder={t('filterPlaceholder')}
            aria-label={t('agent.searchContainers')}
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
            title={t('container.failedToLoad')}
            description={error.message}
            action={
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  queryClient.invalidateQueries({ queryKey: ["agent-containers", agentName] })
                }
              >
                {t('retry')}
              </Button>
            }
          />
        ) : filtered.length === 0 ? (
          <EmptyState
            icon={<Box className="size-12" strokeWidth={1.5} />}
            title={containers?.length ? t('container.noMatches') : t('container.noRunningContainers')}
            description={
              containers?.length
                ? t('nothingMatches', { filter })
                : t('startContainerHint')
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
                      <span className="flex items-center gap-1">
                        <StatusDot
                          status={c.status === "stopping" ? "stopped" : c.status}
                          size="sm"
                        />
                        {enumLabel('containerStatus', c.status)}
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
                          {t('registered')}
                        </Badge>
                      ) : selectedCard ? (
                        <Badge variant="info">
                          {t('selected')}
                        </Badge>
                      ) : (
                        <Badge
                          variant="outline"
                          className="opacity-0 transition-opacity group-hover:opacity-100"
                        >
                          {t('register')}
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
                  {t('containerCount', { count: filtered.length, page: safePage, total: totalPages })}
                </span>
                <div className="flex items-center gap-1">
                  <button
                    type="button"
                    onClick={() => go(safePage - 1)}
                    disabled={safePage <= 1}
                    aria-label={t('previousPage')}
                    className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    <ChevronLeft className="size-3.5" />
                  </button>
                  {Array.from({ length: totalPages }, (_, i) => i + 1).map((p) => (
                    <button
                      key={p}
                      type="button"
                      onClick={() => go(p)}
                      aria-label={`${t('previousPage')} ${p}`}
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
                    aria-label={t('nextPage')}
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
                  {t('register')} {selected.modelName}
                </p>
                <button
                  type="button"
                  onClick={() => setSelectedId(null)}
                  aria-label={t('script.cancelSelection')}
                  className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:bg-[var(--color-bg-elevated)]"
                >
                  <X className="size-3.5" />
                </button>
              </div>
              <div className="space-y-2.5">
                <Input
                  label={t('runtime.displayName')}
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  placeholder={t('runtime.placeholders.displayName')}
                  aria-label={t('runtime.displayName')}
                />
                <div className="flex flex-col gap-1">
                  <span className="text-xs font-medium text-[var(--color-text-muted)]">{t('containerLabel')}</span>
                  <code className="h-8 truncate rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-1.5 font-mono text-xs text-[var(--color-text)]">
                    {selected.modelName}
                  </code>
                </div>
                <Input
                  label={t('runtime.port')}
                  type="number"
                  value={port}
                  onChange={(e) => setPort(e.target.value)}
                  placeholder={t('runtime.placeholders.port')}
                  aria-label={t('runtime.port')}
                />
                <Input
                  label={t('runtime.mappedPort')}
                  type="number"
                  value={mappedPort}
                  onChange={(e) => setMappedPort(e.target.value)}
                  placeholder={t('runtime.placeholders.mappedPort')}
                  aria-label={t('runtime.mappedPort')}
                />
                <p className="text-[10px] leading-tight text-[var(--color-text-muted)]">
                  {t('runtime.hostPortHint')}
                </p>
                <Input
                  label={t('runtime.parallelLanes')}
                  type="number"
                  value={String(maxConcurrentInferences)}
                  onChange={(e) => {
                    const v = parseInt(e.target.value, 10);
                    if (!isNaN(v)) setMaxConcurrentInferences(Math.max(1, Math.min(128, v)));
                  }}
                  placeholder={t('runtime.placeholders.parallelLanes')}
                  aria-label={t('runtime.parallelLanes')}
                />
                <p className="text-[10px] leading-tight text-[var(--color-text-muted)]">
                  {t('runtime.maxConcurrentHint')}
                </p>
              </div>
              <div className="flex justify-end gap-2 pt-1">
                <Button variant="ghost" size="sm" onClick={() => setSelectedId(null)}>
                  {tc('cancel')}
                </Button>
                <Button
                  size="sm"
                  loading={registerMutation.isPending}
                  disabled={!displayName.trim()}
                  onClick={confirmRegister}
                >
                  <PackageOpen className="size-3" />
                  {t('runtime.registerOn', { agentName })}
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

function ManageScriptsBody({
  agentName,
  onClose,
  registered,
}: {
  agentName: string;
  onClose: () => void;
  registered: RegisteredRuntime[];
}) {
  const isHost = agentName === "host";

  if (isHost) {
    return <HostScriptUpload registered={registered} onClose={onClose} />;
  }

  return (
    <AgentScriptUpload
      agentName={agentName}
      registered={registered}
      onClose={onClose}
    />
  );
}

// ─── Manage runtimes modal (public) ─────────────────────────────

export function ManageRuntimesModal({
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
  const { t } = useTranslation('swarm');
  const [activeTab, setActiveTab] = useState<ManageTab>("containers");
  const [createOpen, setCreateOpen] = useState(false);
  const queryClient = useQueryClient();
  // Remount the body whenever the modal opens (or targets a different agent)
  // so filter/page/selection always start fresh.
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }} title={t('manage.title', { agentName })}>
      {/* Tab bar */}
      <div className="flex items-center justify-between border-b border-[var(--color-border-subtle)]">
        <div className="flex" role="tablist" aria-label={t('agent.manageRuntimes')}>
          <button
            type="button"
            role="tab"
            id="swarm-tab-containers"
            aria-selected={activeTab === "containers"}
            aria-controls="swarm-panel-containers"
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
            {t('manage.containers')}
          </button>
          <button
            type="button"
            role="tab"
            id="swarm-tab-scripts"
            aria-selected={activeTab === "scripts"}
            aria-controls="swarm-panel-scripts"
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
            {t('manage.scripts')}
          </button>
        </div>
        <div className="mr-2 flex items-center gap-1">
          <button
            type="button"
            onClick={() => setCreateOpen(true)}
            aria-label={t('create.toggle')}
            className="flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
          >
            <Plus className="size-3.5" />
          </button>
          <button
            type="button"
            onClick={() =>
              queryClient.invalidateQueries({ queryKey: ["agent-containers", agentName] })
            }
            aria-label={t('manage.refreshContainers')}
            className="flex size-7 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
          >
            <RefreshCw className="size-3.5" />
          </button>
        </div>
      </div>

      {activeTab === "containers" ? (
        <div role="tabpanel" id="swarm-panel-containers" aria-labelledby="swarm-tab-containers">
          <ManageContainersBody
            key={open ? `containers:${agentName}:${open}` : "closed"}
            agentName={agentName}
            onClose={onClose}
            registered={registered}
          />
        </div>
      ) : (
        <div role="tabpanel" id="swarm-panel-scripts" aria-labelledby="swarm-tab-scripts">
          <ManageScriptsBody
            key={open ? `scripts:${agentName}:${open}` : "closed"}
            agentName={agentName}
            onClose={onClose}
            registered={registered}
          />
        </div>
      )}

      <CreateContainerForm
        agentName={agentName}
        open={createOpen}
        onOpenChange={setCreateOpen}
      />
    </Dialog>
  );
}
