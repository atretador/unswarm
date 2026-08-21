import { useState } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { useAuth } from "../../lib/auth-context";
import { Input } from "../../components/ui/Input";
import { Button } from "../../components/ui/Button";
import { Logo } from "../../components/ui/Logo";

export default function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string })?.from || "/";

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      await login(username, password);
      navigate(from, { replace: true });
    } catch {
      setError("Invalid username or password");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="flex items-center justify-center min-h-screen bg-[var(--color-bg-base)]">
      <div className="w-full max-w-sm p-8 rounded-[var(--radius-xl)] bg-[var(--color-bg-surface)] border border-[var(--color-border)] shadow-lg">
        {/* Logo */}
        <div className="flex justify-center mb-6">
          <Logo size={44} />
        </div>

        <h1 className="font-heading text-base font-semibold text-[var(--color-text-heading)] mb-6 text-center">
          Sign in to unswarm
        </h1>

        <form onSubmit={handleSubmit} className="space-y-4">
          <Input
            label="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoFocus
            required
          />
          <Input
            label="Password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />

          {error && (
            <div className="rounded-[var(--radius-md)] bg-[var(--color-status-error)]/10 border border-[var(--color-status-error)]/20 px-3 py-2">
              <p className="text-sm text-[var(--color-status-error)]">
                {error}
              </p>
            </div>
          )}

          <Button
            type="submit"
            disabled={loading}
            loading={loading}
            className="w-full"
          >
            Sign in
          </Button>
        </form>
      </div>
    </div>
  );
}
