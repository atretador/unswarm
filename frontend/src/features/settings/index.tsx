import { useState, useEffect } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Settings as SettingsIcon, Shield, Users, Plus, Route } from "lucide-react";
import { client } from "../../lib/query-client";
import { ApiError } from "../../lib/api/httpClient";
import {
  Card,
  Skeleton,
  Input,
  Select,
  Switch,
  Button,
  Badge,
  Modal,
  ConfirmDialog,
} from "../../components/ui";
import type { Settings as SettingsData, User } from "../../lib/api/types";

// ─── Tab Definitions ────────────────────────────────────────────

const TABS = [
  { key: "general", labelKey: "tabs.general", icon: SettingsIcon },
  { key: "users", labelKey: "tabs.users", icon: Users },
  { key: "scheduler", labelKey: "tabs.scheduler", icon: Shield },
  { key: "router", labelKey: "tabs.router", icon: Route },
] as const;

type TabKey = (typeof TABS)[number]["key"];

// ─── Scheduler Policy Section ────────────────────────────────────

type NumericField =
  | "maxQueueDepth"
  | "parallelSlotSkipLimit"
  | "queueStepsTillReset"
  | "conversationDwellSeconds"
  | "requestTimeout"
  | "idleTimeout"
  | "healthCheckInterval"
  | "healthCheckTimeoutSeconds";

type BooleanField =
  | "autoShutdownIdle"
  | "batchDrain"
  | "lazyStop"
  | "enableParallelSlotSkip"
  | "enableConversationAffinity"
  | "enableBenchmarking";

function clampNumericField(field: NumericField, num: number): number {
  if (field === "parallelSlotSkipLimit" || field === "queueStepsTillReset") {
    return Math.max(1, Math.min(1000, num));
  }
  if (field === "conversationDwellSeconds") return Math.max(1, num);
  if (field === "maxQueueDepth") return Math.max(0, num);
  if (field === "requestTimeout") return Math.max(5, num);
  if (field === "idleTimeout") return Math.max(10, num);
  if (field === "healthCheckTimeoutSeconds") return Math.max(10, Math.min(600, num));
  return Math.max(5, num); // healthCheckInterval
}

const TOGGLES: Array<{ key: string; labelKey: string; descKey: string; field: BooleanField }> = [
  {
    key: "auto-shutdown",
    labelKey: "autoShutdown",
    descKey: "autoShutdownDesc",
    field: "autoShutdownIdle",
  },
  {
    key: "batch-drain",
    labelKey: "batchDrain",
    descKey: "batchDrainDesc",
    field: "batchDrain",
  },
  {
    key: "lazy-stop",
    labelKey: "lazyStop",
    descKey: "lazyStopDesc",
    field: "lazyStop",
  },
  {
    key: "enable-parallel-slot-skip",
    labelKey: "parallelSlotSkip",
    descKey: "parallelSlotSkipDesc",
    field: "enableParallelSlotSkip",
  },
  {
    key: "conversation-affinity",
    labelKey: "conversationAffinity",
    descKey: "conversationAffinityDesc",
    field: "enableConversationAffinity",
  },
  {
    key: "enable-benchmarking",
    labelKey: "enableBenchmarking",
    descKey: "enableBenchmarkingDesc",
    field: "enableBenchmarking",
  },
];

function SchedulerPolicySection() {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");

  const { data: settings, isLoading } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const [draftMaxQueue, setDraftMaxQueue] = useState<string>("");
  const [draftParallelSkip, setDraftParallelSkip] = useState<string>("");
  const [draftQueueStepsTillReset, setDraftQueueStepsTillReset] = useState<string>("");
  const [draftConversationDwell, setDraftConversationDwell] = useState<string>("");
  const [draftRequestTimeout, setDraftRequestTimeout] = useState<string>("");
  const [draftIdleTimeout, setDraftIdleTimeout] = useState<string>("");
  const [draftHealthCheckInterval, setDraftHealthCheckInterval] = useState<string>("");
  const [draftHealthCheckTimeoutSeconds, setDraftHealthCheckTimeoutSeconds] = useState<string>("");
  const [draftToggles, setDraftToggles] = useState<Partial<Record<BooleanField, boolean>>>({});
  const [draftPriorityMode, setDraftPriorityMode] = useState<SettingsData["priorityMode"]>("fifo");

  // Reset draft when server data changes
  useEffect(() => {
    if (settings) {
      setDraftMaxQueue(String(settings.maxQueueDepth));
      setDraftParallelSkip(String(settings.parallelSlotSkipLimit));
      setDraftQueueStepsTillReset(String(settings.queueStepsTillReset));
      setDraftConversationDwell(String(settings.conversationDwellSeconds));
      setDraftRequestTimeout(String(settings.requestTimeout));
      setDraftIdleTimeout(String(settings.idleTimeout));
      setDraftHealthCheckInterval(String(settings.healthCheckInterval));
      setDraftHealthCheckTimeoutSeconds(String(settings.healthCheckTimeoutSeconds ?? 120));
      setDraftPriorityMode(settings.priorityMode);
      setDraftToggles({
        autoShutdownIdle: settings.autoShutdownIdle,
        batchDrain: settings.batchDrain,
        lazyStop: settings.lazyStop,
        enableParallelSlotSkip: settings.enableParallelSlotSkip,
        enableConversationAffinity: settings.enableConversationAffinity,
        enableBenchmarking: settings.enableBenchmarking,
      });
    }
  }, [settings]);

  const updateMutation = useMutation({
    mutationFn: (patch: Partial<SettingsData>) => client.updateSettings(patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings"] }),
  });

  if (isLoading || !settings) {
    return (
      <Card padding="lg">
        <Skeleton className="h-4 w-40 mb-4" />
        <div className="space-y-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} className="h-8 w-full" />
          ))}
        </div>
      </Card>
    );
  }

  // Build the set of changed fields (with clamping applied). Non-finite
  // numeric input marks the form invalid instead of being silently dropped.
  const numericDrafts: Array<{ field: NumericField; raw: string }> = [
    { field: "maxQueueDepth", raw: draftMaxQueue },
    { field: "parallelSlotSkipLimit", raw: draftParallelSkip },
    { field: "queueStepsTillReset", raw: draftQueueStepsTillReset },
    { field: "conversationDwellSeconds", raw: draftConversationDwell },
    { field: "requestTimeout", raw: draftRequestTimeout },
    { field: "idleTimeout", raw: draftIdleTimeout },
    { field: "healthCheckInterval", raw: draftHealthCheckInterval },
    { field: "healthCheckTimeoutSeconds", raw: draftHealthCheckTimeoutSeconds },
  ];

  let hasInvalidInput = false;
  const patch: Partial<SettingsData> = {};

  for (const { field, raw } of numericDrafts) {
    const num = Number(raw);
    if (!Number.isFinite(num)) {
      hasInvalidInput = true;
      continue;
    }
    const clamped = clampNumericField(field, num);
    if (clamped !== settings[field]) {
      patch[field] = clamped;
    }
  }

  for (const toggle of TOGGLES) {
    const draftValue = draftToggles[toggle.field];
    if (draftValue !== undefined && draftValue !== settings[toggle.field]) {
      patch[toggle.field] = draftValue;
    }
  }

  if (draftPriorityMode !== settings.priorityMode) {
    patch.priorityMode = draftPriorityMode;
  }

  const isDirty = Object.keys(patch).length > 0;
  const canSave = isDirty && !hasInvalidInput;

  const handleCancel = () => {
    setDraftMaxQueue(String(settings.maxQueueDepth));
    setDraftParallelSkip(String(settings.parallelSlotSkipLimit));
    setDraftQueueStepsTillReset(String(settings.queueStepsTillReset));
    setDraftConversationDwell(String(settings.conversationDwellSeconds));
    setDraftRequestTimeout(String(settings.requestTimeout));
    setDraftIdleTimeout(String(settings.idleTimeout));
    setDraftHealthCheckInterval(String(settings.healthCheckInterval));
    setDraftHealthCheckTimeoutSeconds(String(settings.healthCheckTimeoutSeconds ?? 120));
    setDraftPriorityMode(settings.priorityMode);
    setDraftToggles({
      autoShutdownIdle: settings.autoShutdownIdle,
      batchDrain: settings.batchDrain,
      lazyStop: settings.lazyStop,
      enableParallelSlotSkip: settings.enableParallelSlotSkip,
      enableConversationAffinity: settings.enableConversationAffinity,
      enableBenchmarking: settings.enableBenchmarking,
    });
  };

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-4">
        <SettingsIcon className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {t("schedulerPolicy")}
        </p>
      </div>

      <div className="space-y-4">
        {TOGGLES.map((toggle) => (
          <div key={toggle.key} className="flex items-center justify-between">
            <div>
              <p className="text-sm text-[var(--color-text)]">{t(toggle.labelKey)}</p>
              <p className="text-[10px] text-[var(--color-text-muted)]">{t(toggle.descKey)}</p>
            </div>
            <Switch
              checked={draftToggles[toggle.field] ?? settings[toggle.field]}
              onCheckedChange={(v) => setDraftToggles((prev) => ({ ...prev, [toggle.field]: v }))}
            />
          </div>
        ))}

        <div className="pt-2 border-t border-[var(--color-border-subtle)]">
          <div className={!settings.enableConversationAffinity ? "opacity-50" : ""}>
            <Input
              label={t("fields.conversationHold")}
              type="number"
              value={draftConversationDwell}
              disabled={!settings.enableConversationAffinity}
              onChange={(e) => setDraftConversationDwell(e.target.value)}
            />
            <p className="text-[10px] text-[var(--color-text-muted)]">
              {t("conversationHoldDesc")}
            </p>
          </div>
          <Input
            label={t("fields.maxQueueDepth")}
            type="number"
            value={draftMaxQueue}
            onChange={(e) => setDraftMaxQueue(e.target.value)}
          />
          {settings.enableParallelSlotSkip && (
            <>
              <Input
                label={t("fields.parallelSlotSkipLimit")}
                type="number"
                value={draftParallelSkip}
                onChange={(e) => setDraftParallelSkip(e.target.value)}
              />
              <div>
                <Input
                  label={t("fields.queueStepsTillSkipReset")}
                  type="number"
                  value={draftQueueStepsTillReset}
                  onChange={(e) => setDraftQueueStepsTillReset(e.target.value)}
                />
                <p className="text-[10px] text-[var(--color-text-muted)]">
                  {t("queueStepsResetDesc")}
                </p>
              </div>
            </>
          )}
          <Input
            label={t("fields.requestTimeout")}
            type="number"
            value={draftRequestTimeout}
            onChange={(e) => setDraftRequestTimeout(e.target.value)}
          />
          <Input
            label={t("fields.idleTimeout")}
            type="number"
            value={draftIdleTimeout}
            onChange={(e) => setDraftIdleTimeout(e.target.value)}
          />
          <Input
            label={t("fields.healthCheckInterval")}
            type="number"
            value={draftHealthCheckInterval}
            onChange={(e) => setDraftHealthCheckInterval(e.target.value)}
          />
          <Input
            label={t("fields.healthCheckTimeout")}
            type="number"
            value={draftHealthCheckTimeoutSeconds}
            onChange={(e) => setDraftHealthCheckTimeoutSeconds(e.target.value)}
          />
          <p className="text-[10px] text-[var(--color-text-muted)]">
            {t("maxWaitTime")}
          </p>
          {Number(draftHealthCheckTimeoutSeconds) !== 0 && (Number(draftHealthCheckTimeoutSeconds) < 10 || Number(draftHealthCheckTimeoutSeconds) > 600) && (
            <p className="text-xs text-amber-500">{t("maxWaitTimeHint")}</p>
          )}
          <Select
            label={t("fields.priorityMode")}
            value={draftPriorityMode}
            options={[
              { value: "fifo", label: t("priorityModes.fifo") },
              { value: "priority", label: t("priorityModes.priority") },
            ]}
            onChange={(e) => {
              setDraftPriorityMode(e.target.value as SettingsData["priorityMode"]);
            }}
          />
        </div>

        {updateMutation.error && (
          <p className="text-sm text-[var(--color-status-error)]">
            {updateMutation.error.message}
          </p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={handleCancel} disabled={updateMutation.isPending}>
            {tc("cancel")}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => updateMutation.mutate(patch)}
            disabled={!canSave}
            loading={updateMutation.isPending}
          >
            {tc("save")}
          </Button>
        </div>
      </div>
    </Card>
  );
}

// ─── Add User Modal ─────────────────────────────────────────────

function AddUserModal({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  const createMutation = useMutation({
    mutationFn: ({
      username: u,
      password: p,
    }: {
      username: string;
      password: string;
    }) => client.createUser(u, p),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      setUsername("");
      setPassword("");
      setError(null);
      onClose();
    },
    onError: (err: Error) => setError(err.message),
  });

  // Reset state when modal closes
  useEffect(() => {
    if (!open) {
      setUsername("");
      setPassword("");
      setError(null);
    }
  }, [open]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!username.trim()) {
      setError(t("validation.usernameRequired"));
      return;
    }
    if (password.length < 6) {
      setError(t("validation.passwordMinLength"));
      return;
    }
    createMutation.mutate({ username: username.trim(), password });
  };

  return (
    <Modal open={open} onClose={onClose}>
      <h3 className="text-sm font-semibold text-[var(--color-text-heading)] mb-4">
        {t("users.addUser")}
      </h3>
      <form onSubmit={handleSubmit} className="space-y-4">
        <Input
          label={t("placeholders.username")}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          autoFocus
        />
        <Input
          label={t("placeholders.password")}
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="new-password"
        />

        {error && (
          <p className="text-sm text-[var(--color-status-error)]">{error}</p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={onClose}>
            {tc("cancel")}
          </Button>
          <Button
            type="submit"
            variant="primary"
            size="sm"
            loading={createMutation.isPending}
          >
            {t("users.addUser")}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ─── Reset Password Modal ───────────────────────────────────────

function ResetPasswordModal({
  open,
  onClose,
  user: targetUser,
}: {
  open: boolean;
  onClose: () => void;
  user: User | null;
}) {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);

  const resetMutation = useMutation({
    mutationFn: ({ id, pw }: { id: string; pw: string }) =>
      client.resetPassword(id, pw),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (err: Error) => setError(err.message),
  });

  useEffect(() => {
    if (!open) {
      setPassword("");
      setConfirm("");
      setError(null);
    }
  }, [open]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!targetUser) return;
    if (password.length < 6) {
      setError(t("validation.passwordMinLength"));
      return;
    }
    if (password !== confirm) {
      setError(t("validation.passwordsNoMatch"));
      return;
    }
    resetMutation.mutate({ id: targetUser.id, pw: password });
  };

  return (
    <Modal open={open} onClose={onClose}>
      <h3 className="text-sm font-semibold text-[var(--color-text-heading)] mb-1">
        {t("users.resetPasswordTitle")}
      </h3>
      {targetUser && (
        <p className="text-xs text-[var(--color-text-muted)] mb-4">
          {t("users.resetPasswordDesc", { username: targetUser.username })}
        </p>
      )}
      <form onSubmit={handleSubmit} className="space-y-4">
        <Input
          label={t("placeholders.newPassword")}
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoFocus
          autoComplete="new-password"
        />
        <Input
          label={t("placeholders.confirmPassword")}
          type="password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          autoComplete="new-password"
        />

        {error && (
          <p className="text-sm text-[var(--color-status-error)]">{error}</p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={onClose}>
            {tc("cancel")}
          </Button>
          <Button
            type="submit"
            variant="primary"
            size="sm"
            loading={resetMutation.isPending}
          >
            {t("users.setPassword")}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ─── User Table Row ─────────────────────────────────────────────

function UserRow({
  user: u,
  onDelete,
  onResetPassword,
}: {
  user: User;
  onDelete: (user: User) => void;
  onResetPassword: (user: User) => void;
}) {
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");
  const letter = u.username?.charAt(0)?.toUpperCase() ?? "?";

  return (
    <div className="flex items-center gap-4 px-4 py-3 border-b border-[var(--color-border-subtle)] last:border-b-0 hover:bg-[var(--color-bg-muted)]/50 transition-colors duration-[var(--duration-fast)]">
      {/* User identity */}
      <div className="flex items-center gap-3 min-w-0 flex-1">
        <div className="flex items-center justify-center size-8 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] font-heading text-xs font-bold select-none shrink-0">
          {letter}
        </div>
        <span className="text-sm text-[var(--color-text)] truncate">
          {u.username}
        </span>
      </div>

      {/* Status */}
      <div className="shrink-0">
        {u.isTempPassword && (
          <Badge variant="warning" size="sm">
            {t("users.tempPassword")}
          </Badge>
        )}
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1 shrink-0">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => onResetPassword(u)}
        >
          {t("users.resetPassword")}
        </Button>
        <Button
          variant="danger"
          size="sm"
          onClick={() => onDelete(u)}
        >
          {tc("delete")}
        </Button>
      </div>
    </div>
  );
}

// ─── Users Tab ──────────────────────────────────────────────────

function UsersTab() {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");

  const {
    data: users,
    isLoading,
    error,
  } = useQuery({
    queryKey: ["users"],
    queryFn: () => client.listUsers(),
    retry: false,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteUser(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["users"] }),
  });

  const [addModalOpen, setAddModalOpen] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<User | null>(null);
  const [resetTarget, setResetTarget] = useState<User | null>(null);

  // 403 detection — non-admin
  if (error && error instanceof ApiError && error.status === 403) {
    return null;
  }

  return (
    <>
      <Card padding="none">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--color-border-subtle)]">
          <div className="flex items-center gap-2">
            <Users className="size-4 text-[var(--color-text-muted)]" />
            <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              {t("users.title")}
            </p>
          </div>
          <Button
            variant="primary"
            size="sm"
            onClick={() => setAddModalOpen(true)}
          >
            <Plus className="size-3.5" />
            {t("users.addUser")}
          </Button>
        </div>

        {/* Table */}
        {isLoading ? (
          <div className="p-4 space-y-3">
            {Array.from({ length: 2 }, (_, i) => (
              <Skeleton key={i} className="h-10 w-full" />
            ))}
          </div>
        ) : users && users.length > 0 ? (
          <div>
            {/* Column headers */}
            <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--color-border)] bg-[var(--color-bg-muted)]/30">
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider min-w-0 flex-1">
                {t("user")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0">
                {t("status")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[200px] text-right">
                {t("actions")}
              </span>
            </div>

            {users.map((u) => (
              <UserRow
                key={u.id}
                user={u}
                onDelete={setDeleteTarget}
                onResetPassword={setResetTarget}
              />
            ))}
          </div>
        ) : (
          <div className="px-4 py-8 text-center">
            <p className="text-sm text-[var(--color-text-muted)]">{t("users.noUsers")}</p>
          </div>
        )}
      </Card>

      <AddUserModal open={addModalOpen} onClose={() => setAddModalOpen(false)} />

      <ConfirmDialog
        open={deleteTarget !== null}
        title={t("users.deleteUser")}
        description={t("users.deleteUserDesc", { name: deleteTarget?.username ?? "" })}
        confirmLabel={tc("delete")}
        variant="danger"
        loading={deleteMutation.isPending}
        onConfirm={() => {
          if (deleteTarget) {
            deleteMutation.mutate(deleteTarget.id);
            setDeleteTarget(null);
          }
        }}
        onCancel={() => setDeleteTarget(null)}
      />

      <ResetPasswordModal
        open={resetTarget !== null}
        onClose={() => setResetTarget(null)}
        user={resetTarget}
      />
    </>
  );
}

// ─── General Tab ────────────────────────────────────────────────

function GeneralTab() {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");

  const { data: settings, isLoading } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const [draftRetention, setDraftRetention] = useState<string>("");
  const [draftHideOriginPrefix, setDraftHideOriginPrefix] = useState(false);
  const [draftPollInterval, setDraftPollInterval] = useState<string>("");

  useEffect(() => {
    if (settings) {
      setDraftRetention(String(settings.logRetention));
      setDraftHideOriginPrefix(settings.hideOriginPrefix);
      setDraftPollInterval(String(settings.telemetryPollInterval ?? 10));
    }
  }, [settings]);

  const updateMutation = useMutation({
    mutationFn: (patch: Partial<SettingsData>) => client.updateSettings(patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings"] }),
  });

  if (isLoading || !settings) {
    return (
      <Card padding="md">
        <div className="flex items-center gap-2 mb-4">
          <SettingsIcon className="size-4 text-[var(--color-text-muted)]" />
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            {t("system")}
          </p>
        </div>
        <Skeleton className="h-8 w-full" />
      </Card>
    );
  }

  const num = Number(draftRetention);
  const pollNum = Number(draftPollInterval);
  const hasInvalidInput = !Number.isFinite(num) || !Number.isFinite(pollNum);
  const clampedRetention = hasInvalidInput ? settings.logRetention : Math.max(1, num);
  const clampedPollInterval = hasInvalidInput ? (settings.telemetryPollInterval ?? 10) : Math.max(5, Math.min(60, pollNum));
  const isDirty =
    (!hasInvalidInput && clampedRetention !== settings.logRetention) ||
    draftHideOriginPrefix !== settings.hideOriginPrefix ||
    (!hasInvalidInput && clampedPollInterval !== (settings.telemetryPollInterval ?? 10));
  const canSave = isDirty && !hasInvalidInput;

  const handleCancel = () => {
    setDraftRetention(String(settings.logRetention));
    setDraftHideOriginPrefix(settings.hideOriginPrefix);
    setDraftPollInterval(String(settings.telemetryPollInterval ?? 10));
  };

  return (
    <Card padding="md">
      <div className="flex items-center gap-2 mb-4">
        <SettingsIcon className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {t("system")}
        </p>
      </div>

      <div className="space-y-4">
        <Input
          label={t("logRetention")}
          type="number"
          value={draftRetention}
          onChange={(e) => setDraftRetention(e.target.value)}
        />

        <div>
          <label className="text-sm font-medium">{t("metricsPollInterval")}</label>
          <p className="text-xs text-[var(--color-text-muted)]">
            {t("metricsPollIntervalDesc")}
          </p>
          <Input
            type="number"
            min={5}
            max={60}
            step={1}
            value={draftPollInterval}
            onChange={(e) => setDraftPollInterval(e.target.value)}
          />
        </div>

        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-[var(--color-text)]">{t("hideOriginPrefix")}</p>
            <p className="text-[10px] text-[var(--color-text-muted)]">
              {t("hideOriginPrefixDesc")}
            </p>
          </div>
          <Switch
            checked={draftHideOriginPrefix}
            onCheckedChange={(v) => setDraftHideOriginPrefix(v)}
          />
        </div>

        {updateMutation.error && (
          <p className="text-sm text-[var(--color-status-error)]">
            {updateMutation.error.message}
          </p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={handleCancel} disabled={updateMutation.isPending}>
            {tc("cancel")}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() =>
              updateMutation.mutate({
                ...(clampedRetention !== settings.logRetention ? { logRetention: clampedRetention } : {}),
                ...(draftHideOriginPrefix !== settings.hideOriginPrefix
                  ? { hideOriginPrefix: draftHideOriginPrefix }
                  : {}),
                ...(clampedPollInterval !== (settings.telemetryPollInterval ?? 10) ? { telemetryPollInterval: clampedPollInterval } : {}),
              })
            }
            disabled={!canSave}
            loading={updateMutation.isPending}
          >
            {tc("save")}
          </Button>
        </div>
      </div>
    </Card>
  );
}

// ─── Router Tab ────────────────────────────────────────────────

function RouterTab() {
  const queryClient = useQueryClient();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");

  const { data: settings, isLoading } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const [draftRetryAttempts, setDraftRetryAttempts] = useState<string>("");
  const [draftRetryDelayMs, setDraftRetryDelayMs] = useState<string>("");

  useEffect(() => {
    if (settings) {
      setDraftRetryAttempts(String(settings.routerRetryAttempts ?? 3));
      setDraftRetryDelayMs(String(settings.routerRetryDelayMs ?? 1000));
    }
  }, [settings]);

  const updateMutation = useMutation({
    mutationFn: (patch: Partial<SettingsData>) => client.updateSettings(patch),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings"] }),
  });

  if (isLoading || !settings) {
    return (
      <Card padding="md">
        <div className="flex items-center gap-2 mb-4">
          <Route className="size-4 text-[var(--color-text-muted)]" />
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            {t("router")}
          </p>
        </div>
        <Skeleton className="h-8 w-full" />
      </Card>
    );
  }

  const numAttempts = Number(draftRetryAttempts);
  const numDelay = Number(draftRetryDelayMs);
  const hasInvalidInput = !Number.isFinite(numAttempts) || !Number.isFinite(numDelay);
  const clampedAttempts = hasInvalidInput ? (settings.routerRetryAttempts ?? 3) : Math.max(0, Math.min(10, numAttempts));
  const clampedDelay = hasInvalidInput ? (settings.routerRetryDelayMs ?? 1000) : Math.max(0, Math.min(30000, numDelay));
  const isDirty =
    (!hasInvalidInput && (clampedAttempts !== (settings.routerRetryAttempts ?? 3) || clampedDelay !== (settings.routerRetryDelayMs ?? 1000)));
  const canSave = isDirty && !hasInvalidInput;

  const handleCancel = () => {
    setDraftRetryAttempts(String(settings.routerRetryAttempts ?? 3));
    setDraftRetryDelayMs(String(settings.routerRetryDelayMs ?? 1000));
  };

  return (
    <Card padding="md">
      <div className="flex items-center gap-2 mb-4">
        <Route className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {t("router")}
        </p>
      </div>

      <div className="space-y-4">
        <div>
          <p className="text-sm text-[var(--color-text)] mb-1">{t("serverErrorRetry")}</p>
          <p className="text-[10px] text-[var(--color-text-muted)] mb-3">
            {t("serverErrorRetryDesc")}
          </p>

          <Input
            label={t("retryAttempts")}
            type="number"
            value={draftRetryAttempts}
            onChange={(e) => setDraftRetryAttempts(e.target.value)}
          />
          <p className="text-[10px] text-[var(--color-text-muted)]">
            {t("retryAttemptsDesc")}
          </p>
          {Number(draftRetryAttempts) !== 0 && (Number(draftRetryAttempts) < 0 || Number(draftRetryAttempts) > 10) && (
            <p className="text-xs text-amber-500">{t("retryHint")}</p>
          )}

          <Input
            label={t("retryDelay")}
            type="number"
            value={draftRetryDelayMs}
            onChange={(e) => setDraftRetryDelayMs(e.target.value)}
          />
          <p className="text-[10px] text-[var(--color-text-muted)]">
            {t("retryDelayDesc")}
          </p>
          {Number(draftRetryDelayMs) !== 0 && (Number(draftRetryDelayMs) < 0 || Number(draftRetryDelayMs) > 30000) && (
            <p className="text-xs text-amber-500">{t("retryDelayHint")}</p>
          )}
        </div>

        {updateMutation.error && (
          <p className="text-sm text-[var(--color-status-error)]">
            {updateMutation.error.message}
          </p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={handleCancel} disabled={updateMutation.isPending}>
            {tc("cancel")}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() =>
              updateMutation.mutate({
                ...(clampedAttempts !== (settings.routerRetryAttempts ?? 3) ? { routerRetryAttempts: clampedAttempts } : {}),
                ...(clampedDelay !== (settings.routerRetryDelayMs ?? 1000) ? { routerRetryDelayMs: clampedDelay } : {}),
              })
            }
            disabled={!canSave}
            loading={updateMutation.isPending}
          >
            {tc("save")}
          </Button>
        </div>
      </div>
    </Card>
  );
}

// ─── Main Settings Page ─────────────────────────────────────────

export default function Settings() {
  const [searchParams, setSearchParams] = useSearchParams();
  const { t } = useTranslation("settings");
  const tabParam = searchParams.get("tab") as TabKey | null;
  const activeTab: TabKey =
    tabParam && TABS.some((t) => t.key === tabParam) ? tabParam : "general";

  // Admin gate: only needed for users tab
  const { error: usersError } = useQuery({
    queryKey: ["users"],
    queryFn: () => client.listUsers(),
    retry: false,
  });
  const isAdmin = !(usersError && usersError instanceof ApiError && usersError.status === 403);

  const visibleTabs = TABS.filter((tab) => {
    if (tab.key === "users" && !isAdmin) return false;
    return true;
  });

  // If the active tab was "users" but user isn't admin, fall back
  const effectiveTab =
    activeTab === "users" && !isAdmin ? "general" : activeTab;

  const setTab = (tab: TabKey) => {
    setSearchParams({ tab });
  };

  return (
    <div className="p-6 space-y-6 max-w-3xl">
      {/* Page header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          {t("title")}
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          {t("description")}
        </p>
      </div>

      {/* Tab bar */}
      <div className="flex gap-1 p-1 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] border border-[var(--color-border-subtle)]">
        {visibleTabs.map((tab) => {
          const Icon = tab.icon;
          const isActive = effectiveTab === tab.key;
          return (
            <button
              key={tab.key}
              onClick={() => setTab(tab.key)}
              className={`
                flex items-center gap-2 px-3 py-1.5 rounded-[var(--radius-md)] text-xs font-medium
                transition-all duration-[var(--duration-fast)]
                cursor-pointer
                ${
                  isActive
                    ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm"
                    : "text-[var(--color-text-muted)] hover:text-[var(--color-text)] hover:bg-[var(--color-bg-surface)]/50"
                }
              `}
            >
              <Icon className="size-3.5" />
              {t(tab.labelKey)}
            </button>
          );
        })}
      </div>

      {/* Tab content */}
      {effectiveTab === "general" && <GeneralTab />}
      {effectiveTab === "users" && <UsersTab />}
      {effectiveTab === "scheduler" && <SchedulerPolicySection />}
      {effectiveTab === "router" && <RouterTab />}
    </div>
  );
}
