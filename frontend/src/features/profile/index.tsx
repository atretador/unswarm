import { useState } from "react";
import { Shield, User } from "lucide-react";
import { useAuth } from "../../lib/auth-context";
import { Card, Input, Button } from "../../components/ui";

// ─── Change Password Section (moved from settings) ──────────────

function ChangePasswordSection() {
  const { changePassword } = useAuth();
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

// ─── Profile Page ───────────────────────────────────────────────

export default function Profile() {
  const { user } = useAuth();
  const letter = user?.username?.charAt(0)?.toUpperCase() ?? "?";

  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Profile
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Account details and security.
        </p>
      </div>

      {/* Account identity */}
      <Card padding="lg">
        <div className="flex items-center gap-2 mb-4">
          <User className="size-4 text-[var(--color-text-muted)]" />
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            Account
          </p>
        </div>

        {user?.isTempPassword && (
          <div className="mb-4 rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-warning)_15%,transparent)] border border-[color-mix(in_srgb,var(--color-status-warning)_30%,transparent)] px-4 py-3">
            <p className="text-sm text-[var(--color-status-warning)] font-medium">
              You&apos;re using a temporary password. Please change it now.
            </p>
          </div>
        )}

        <div className="flex items-center gap-4">
          <div className="flex items-center justify-center size-12 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] font-heading text-lg font-bold select-none">
            {letter}
          </div>
          <div>
            <p className="text-sm font-medium text-[var(--color-text-heading)]">
              {user?.username ?? "Unknown"}
            </p>
            <p className="text-xs text-[var(--color-text-muted)]">
              {user?.isTempPassword ? "Temporary password" : "Account active"}
            </p>
          </div>
        </div>
      </Card>

      <ChangePasswordSection />
    </div>
  );
}
