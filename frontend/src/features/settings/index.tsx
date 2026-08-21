import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Settings as SettingsIcon, Shield, Users } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Skeleton, Input, Switch, Button, Badge } from "../../components/ui";
import { useAuth } from "../../lib/auth-context";
import type { User } from "../../lib/api/types";

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

// ─── Change Password Section ────────────────────────────────────

function ChangePasswordSection() {
  const { user, changePassword } = useAuth();
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccess(false);

    if (newPassword.length < 6) {
      setError("New password must be at least 6 characters.");
      return;
    }
    if (newPassword !== confirmPassword) {
      setError("New passwords do not match.");
      return;
    }

    setSubmitting(true);
    try {
      await changePassword(currentPassword, newPassword);
      setSuccess(true);
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to change password.");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-4">
        <Shield className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          Change Password
        </p>
      </div>

      {user?.isTempPassword && (
        <div className="mb-4 rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-warning)_15%,transparent)] border border-[color-mix(in_srgb,var(--color-status-warning)_30%,transparent)] px-4 py-3">
          <p className="text-sm text-[var(--color-status-warning)] font-medium">
            You&apos;re using a temporary password. Please change it now.
          </p>
        </div>
      )}

      <form onSubmit={handleSubmit} className="space-y-4">
        <Input
          label="Current password"
          type="password"
          value={currentPassword}
          onChange={(e) => setCurrentPassword(e.target.value)}
          autoComplete="current-password"
        />
        <Input
          label="New password"
          type="password"
          value={newPassword}
          onChange={(e) => setNewPassword(e.target.value)}
          autoComplete="new-password"
        />
        <Input
          label="Confirm new password"
          type="password"
          value={confirmPassword}
          onChange={(e) => setConfirmPassword(e.target.value)}
          autoComplete="new-password"
        />

        {error && (
          <p className="text-sm text-[var(--color-status-error)]">{error}</p>
        )}
        {success && (
          <p className="text-sm text-[var(--color-status-running)]">
            Password changed successfully.
          </p>
        )}

        <Button type="submit" variant="primary" size="md" loading={submitting}>
          Change Password
        </Button>
      </form>
    </Card>
  );
}

// ─── User Management Section ────────────────────────────────────

function UserRow({
  user: u,
  client,
  queryClient,
}: {
  user: User;
  client: typeof import("../../lib/query-client").client;
  queryClient: ReturnType<typeof useQueryClient>;
}) {
  const [resetting, setResetting] = useState(false);
  const [resetPw, setResetPw] = useState("");
  const [resetConfirm, setResetConfirm] = useState("");
  const [resetError, setResetError] = useState<string | null>(null);

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteUser(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["users"] }),
  });

  const resetMutation = useMutation({
    mutationFn: ({ id, pw }: { id: string; pw: string }) => client.resetPassword(id, pw),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      setResetting(false);
      setResetPw("");
      setResetConfirm("");
      setResetError(null);
    },
    onError: (err: Error) => setResetError(err.message),
  });

  const handleDelete = () => {
    if (!window.confirm(`Delete user "${u.username}"? This cannot be undone.`)) return;
    deleteMutation.mutate(u.id);
  };

  const handleResetSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setResetError(null);
    if (resetPw.length < 6) {
      setResetError("Password must be at least 6 characters.");
      return;
    }
    if (resetPw !== resetConfirm) {
      setResetError("Passwords do not match.");
      return;
    }
    resetMutation.mutate({ id: u.id, pw: resetPw });
  };

  return (
    <div className="border-t border-[var(--color-border)] first:border-t-0">
      <div className="flex items-center justify-between py-3">
        <div className="flex items-center gap-2">
          <span className="text-sm text-[var(--color-text)]">{u.username}</span>
          {u.isTempPassword && (
            <Badge variant="warning" size="sm">
              temp
            </Badge>
          )}
        </div>
        <div className="flex items-center gap-2">
          {!resetting && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setResetting(true);
                setResetError(null);
              }}
            >
              Reset Password
            </Button>
          )}
          <Button variant="danger" size="sm" onClick={handleDelete} loading={deleteMutation.isPending}>
            Delete
          </Button>
        </div>
      </div>

      {resetting && (
        <form onSubmit={handleResetSubmit} className="mb-3 space-y-2 pl-4 border-l-2 border-[var(--color-border)]">
          <Input
            label="New password"
            type="password"
            value={resetPw}
            onChange={(e) => setResetPw(e.target.value)}
          />
          <Input
            label="Confirm new password"
            type="password"
            value={resetConfirm}
            onChange={(e) => setResetConfirm(e.target.value)}
          />
          {resetError && (
            <p className="text-xs text-[var(--color-status-error)]">{resetError}</p>
          )}
          <div className="flex items-center gap-2">
            <Button type="submit" variant="primary" size="sm" loading={resetMutation.isPending}>
              Set Password
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                setResetting(false);
                setResetPw("");
                setResetConfirm("");
                setResetError(null);
              }}
            >
              Cancel
            </Button>
          </div>
        </form>
      )}
    </div>
  );
}

function UserManagementSection() {
  const queryClient = useQueryClient();

  const {
    data: users,
    isLoading,
    error,
  } = useQuery({
    queryKey: ["users"],
    queryFn: () => client.listUsers(),
    // Silently handle 403 — section just won't appear
    retry: false,
  });

  const [newUsername, setNewUsername] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [createError, setCreateError] = useState<string | null>(null);
  const [createSuccess, setCreateSuccess] = useState(false);

  const createMutation = useMutation({
    mutationFn: ({ username, password }: { username: string; password: string }) =>
      client.createUser(username, password),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      setNewUsername("");
      setNewPassword("");
      setCreateError(null);
      setCreateSuccess(true);
      setTimeout(() => setCreateSuccess(false), 3000);
    },
    onError: (err: Error) => setCreateError(err.message),
  });

  const handleCreate = (e: React.FormEvent) => {
    e.preventDefault();
    setCreateError(null);
    setCreateSuccess(false);

    if (!newUsername.trim()) {
      setCreateError("Username is required.");
      return;
    }
    if (newPassword.length < 6) {
      setCreateError("Password must be at least 6 characters.");
      return;
    }
    createMutation.mutate({ username: newUsername.trim(), password: newPassword });
  };

  // Hide section entirely on 403 (not admin)
  if (error && (error as Error).message?.includes("403")) {
    return null;
  }

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-4">
        <Users className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          User Management
        </p>
      </div>

      {isLoading ? (
        <div className="space-y-3">
          {Array.from({ length: 2 }, (_, i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      ) : (
        <>
          {/* User list */}
          {users && users.length > 0 ? (
            <div className="mb-4">
              {users.map((u) => (
                <UserRow key={u.id} user={u} client={client} queryClient={queryClient} />
              ))}
            </div>
          ) : (
            <p className="text-sm text-[var(--color-text-muted)] mb-4">No users found.</p>
          )}

          {/* Add user form */}
          <div className="border-t border-[var(--color-border)] pt-4">
            <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-3">
              Add User
            </p>
            <form onSubmit={handleCreate} className="space-y-3">
              <Input
                label="Username"
                value={newUsername}
                onChange={(e) => setNewUsername(e.target.value)}
              />
              <Input
                label="Password"
                type="password"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                autoComplete="new-password"
              />

              {createError && (
                <p className="text-sm text-[var(--color-status-error)]">{createError}</p>
              )}
              {createSuccess && (
                <p className="text-sm text-[var(--color-status-running)]">
                  User created successfully.
                </p>
              )}

              <Button type="submit" variant="primary" size="md" loading={createMutation.isPending}>
                Add User
              </Button>
            </form>
          </div>
        </>
      )}
    </Card>
  );
}

// ─── Main Settings Page ─────────────────────────────────────────

export default function Settings() {
  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Settings
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          System configuration and preferences.
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

      <ChangePasswordSection />
      <UserManagementSection />
      <SchedulerPolicySection />
    </div>
  );
}
