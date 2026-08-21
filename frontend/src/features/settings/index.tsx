import { useState, useEffect } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Settings as SettingsIcon, Shield, Users, Plus } from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Skeleton,
  Input,
  Switch,
  Button,
  Badge,
  Modal,
  ConfirmDialog,
} from "../../components/ui";
import type { User } from "../../lib/api/types";

// ─── Tab Definitions ────────────────────────────────────────────

const TABS = [
  { key: "general", label: "General", icon: SettingsIcon },
  { key: "users", label: "Users", icon: Users },
  { key: "scheduler", label: "Scheduler", icon: Shield },
] as const;

type TabKey = (typeof TABS)[number]["key"];

// ─── Scheduler Policy Section ────────────────────────────────────

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
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} className="h-8 w-full" />
          ))}
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
            onChange={(e) =>
              updateMutation.mutate({ maxQueueDepth: Number(e.target.value) || 0 })
            }
          />
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
      setError("Username is required.");
      return;
    }
    if (password.length < 6) {
      setError("Password must be at least 6 characters.");
      return;
    }
    createMutation.mutate({ username: username.trim(), password });
  };

  return (
    <Modal open={open} onClose={onClose}>
      <h3 className="text-sm font-semibold text-[var(--color-text-heading)] mb-4">
        Add User
      </h3>
      <form onSubmit={handleSubmit} className="space-y-4">
        <Input
          label="Username"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          autoFocus
        />
        <Input
          label="Password"
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
            Cancel
          </Button>
          <Button
            type="submit"
            variant="primary"
            size="sm"
            loading={createMutation.isPending}
          >
            Add User
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
      setError("Password must be at least 6 characters.");
      return;
    }
    if (password !== confirm) {
      setError("Passwords do not match.");
      return;
    }
    resetMutation.mutate({ id: targetUser.id, pw: password });
  };

  return (
    <Modal open={open} onClose={onClose}>
      <h3 className="text-sm font-semibold text-[var(--color-text-heading)] mb-1">
        Reset Password
      </h3>
      {targetUser && (
        <p className="text-xs text-[var(--color-text-muted)] mb-4">
          Set a new password for <span className="font-medium text-[var(--color-text)]">{targetUser.username}</span>.
        </p>
      )}
      <form onSubmit={handleSubmit} className="space-y-4">
        <Input
          label="New password"
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoFocus
          autoComplete="new-password"
        />
        <Input
          label="Confirm new password"
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
            Cancel
          </Button>
          <Button
            type="submit"
            variant="primary"
            size="sm"
            loading={resetMutation.isPending}
          >
            Set Password
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
            temp password
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
          Reset Password
        </Button>
        <Button
          variant="danger"
          size="sm"
          onClick={() => onDelete(u)}
        >
          Delete
        </Button>
      </div>
    </div>
  );
}

// ─── Users Tab ──────────────────────────────────────────────────

function UsersTab() {
  const queryClient = useQueryClient();

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
  if (error && (error as Error).message?.includes("403")) {
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
              Users
            </p>
          </div>
          <Button
            variant="primary"
            size="sm"
            onClick={() => setAddModalOpen(true)}
          >
            <Plus className="size-3.5" />
            Add User
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
                User
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0">
                Status
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[200px] text-right">
                Actions
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
            <p className="text-sm text-[var(--color-text-muted)]">No users found.</p>
          </div>
        )}
      </Card>

      <AddUserModal open={addModalOpen} onClose={() => setAddModalOpen(false)} />

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete user"
        description={`Delete user "${deleteTarget?.username ?? ""}"? This cannot be undone.`}
        confirmLabel="Delete"
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
  return (
    <Card padding="md">
      <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-2">
        Theme
      </p>
      <p className="text-sm text-[var(--color-text)]">
        Theme is controlled from the topbar toggle (light / dark / system).
      </p>
    </Card>
  );
}

// ─── Main Settings Page ─────────────────────────────────────────

export default function Settings() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tabParam = searchParams.get("tab") as TabKey | null;
  const activeTab: TabKey =
    tabParam && TABS.some((t) => t.key === tabParam) ? tabParam : "general";

  // Admin gate: only needed for users tab
  const { error: usersError } = useQuery({
    queryKey: ["users"],
    queryFn: () => client.listUsers(),
    retry: false,
  });
  const isAdmin = !(usersError && (usersError as Error).message?.includes("403"));

  const visibleTabs = TABS.filter((t) => {
    if (t.key === "users" && !isAdmin) return false;
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
          Settings
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          System configuration and preferences.
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
              {tab.label}
            </button>
          );
        })}
      </div>

      {/* Tab content */}
      {effectiveTab === "general" && <GeneralTab />}
      {effectiveTab === "users" && <UsersTab />}
      {effectiveTab === "scheduler" && <SchedulerPolicySection />}
    </div>
  );
}
