import { useState } from "react";
import { Shield, User } from "lucide-react";
import { useAuth } from "../../lib/auth-context";
import { Dialog, Input, Button } from "../../components/ui";

interface EditProfileModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function EditProfileModal({ open, onOpenChange }: EditProfileModalProps) {
  const { user, changePassword } = useAuth();
  const [tab, setTab] = useState<"details" | "password">("details");

  // Password form state
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const resetPasswordForm = () => {
    setCurrentPassword("");
    setNewPassword("");
    setConfirmPassword("");
    setError(null);
    setSuccess(false);
  };

  const handleTabChange = (newTab: "details" | "password") => {
    resetPasswordForm();
    setTab(newTab);
  };

  const handlePasswordSubmit = async (e: React.FormEvent) => {
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

  const letter = user?.username?.charAt(0)?.toUpperCase() ?? "?";

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Edit Profile">
      <div className="p-5 space-y-5">
        {/* Tabs */}
        <div className="flex gap-1 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] p-1">
          <button
            type="button"
            onClick={() => handleTabChange("details")}
            className={`flex-1 rounded-[var(--radius-md)] px-3 py-1.5 text-xs font-medium transition-colors ${
              tab === "details"
                ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm"
                : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
            }`}
          >
            <User className="inline-block size-3.5 mr-1.5 -mt-0.5" />
            Details
          </button>
          <button
            type="button"
            onClick={() => handleTabChange("password")}
            className={`flex-1 rounded-[var(--radius-md)] px-3 py-1.5 text-xs font-medium transition-colors ${
              tab === "password"
                ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm"
                : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
            }`}
          >
            <Shield className="inline-block size-3.5 mr-1.5 -mt-0.5" />
            Password
          </button>
        </div>

        {/* Details tab */}
        {tab === "details" && (
          <div className="space-y-4">
            <div className="flex items-center gap-4">
              <div className="flex items-center justify-center size-14 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] font-heading text-xl font-bold select-none">
                {letter}
              </div>
              <div>
                <p className="text-sm font-medium text-[var(--color-text-heading)]">
                  {user?.username ?? "Unknown"}
                </p>
                <p className="text-xs text-[var(--color-text-muted)]">
                  {user?.isTempPassword ? "Temporary password — change it now" : "Account active"}
                </p>
              </div>
            </div>

            <Input
              label="Username"
              value={user?.username ?? ""}
              readOnly
              className="opacity-60 cursor-not-allowed"
            />

            {user?.isTempPassword && (
              <div className="rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-warning)_15%,transparent)] border border-[color-mix(in_srgb,var(--color-status-warning)_30%,transparent)] px-4 py-3">
                <p className="text-sm text-[var(--color-status-warning)] font-medium">
                  You&apos;re using a temporary password. Switch to the Password tab to change it.
                </p>
              </div>
            )}
          </div>
        )}

        {/* Password tab */}
        {tab === "password" && (
          <form onSubmit={handlePasswordSubmit} className="space-y-4">
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

            <div className="flex justify-end gap-2 pt-1">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => onOpenChange(false)}
              >
                Cancel
              </Button>
              <Button type="submit" variant="primary" size="sm" loading={submitting}>
                Change Password
              </Button>
            </div>
          </form>
        )}

        {/* Close button for details tab */}
        {tab === "details" && (
          <div className="flex justify-end gap-2 pt-1">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={() => onOpenChange(false)}
            >
              Close
            </Button>
          </div>
        )}
      </div>
    </Dialog>
  );
}
