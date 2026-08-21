import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Key, Copy, RefreshCw, Trash2, Check, ShieldAlert, KeySquare } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Skeleton, Button, Badge, EmptyState, Input } from "../../components/ui";
import type { ApiKeyCreateResponse } from "../../lib/api/types";

// ─── Copy-to-clipboard helper ──────────────────────────────────────────

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard API unavailable — swallow
    }
  };

  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      onClick={handleCopy}
      aria-label="Copy"
    >
      {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
      {copied ? "Copied" : "Copy"}
    </Button>
  );
}

function formatRelative(ts: string | null): string {
  if (!ts) return "Never used";
  const diff = Date.now() - new Date(ts).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return "just now";
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function scopeLabel(scope: "inference" | "agent") {
  return scope === "inference"
    ? { text: "Inference", variant: "info" as const }
    : { text: "Agent", variant: "outline" as const };
}

// ─── Create Key form ────────────────────────────────────────────────────

function CreateKeySection({ queryClient }: { queryClient: ReturnType<typeof useQueryClient> }) {
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<ApiKeyCreateResponse | null>(null);

  const createMutation = useMutation({
    mutationFn: (name: string) => client.createApiKey(name),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ["api-keys"] });
      setCreated(res);
      setName("");
      setError(null);
    },
    onError: (err: Error) => setError(err.message),
  });

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!name.trim()) {
      setError("Name is required.");
      return;
    }
    createMutation.mutate(name.trim());
  };

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-3">
        <Key className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          New Inference Key
        </p>
      </div>

      <p className="text-sm text-[var(--color-text)] mb-4">
        Create a key to authenticate to the inference proxy (<span className="font-mono">/v1</span>).
        These keys are <strong>not</strong> login credentials.
      </p>

      {!created ? (
        <form onSubmit={handleSubmit} className="space-y-3">
          <Input
            label="Key name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. CI runner"
            disabled={createMutation.isPending}
          />
          {error && <p className="text-sm text-[var(--color-status-error)]">{error}</p>}
          <Button type="submit" variant="primary" size="md" loading={createMutation.isPending}>
            Create Key
          </Button>
        </form>
      ) : (
        <div className="space-y-3">
          <div className="rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-running)_15%,transparent)] border border-[color-mix(in_srgb,var(--color-status-running)_30%,transparent)] px-4 py-3">
            <p className="text-sm font-medium text-[var(--color-status-running)] mb-2">
              Key created — copy your secret now.
            </p>
            <p className="text-[10px] text-[var(--color-text-muted)] mb-2">
              This is the only time the full secret is shown. Store it somewhere safe.
            </p>
            <div className="flex items-center gap-2">
              <code className="flex-1 bg-[var(--color-bg-muted)] rounded px-2 py-1 text-xs font-mono text-[var(--color-text)] truncate">
                {created.secret}
              </code>
              <CopyButton value={created.secret} />
            </div>
          </div>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => setCreated(null)}
          >
            Create another
          </Button>
        </div>
      )}
    </Card>
  );
}

// ─── Key list ────────────────────────────────────────────────────────────

function KeyRow({
  id,
  name,
  keyPrefix,
  scope,
  isActive,
  lastUsedAt,
  createdAt,
  queryClient,
}: {
  id: string;
  name: string;
  keyPrefix: string;
  scope: "inference" | "agent";
  isActive: boolean;
  lastUsedAt: string | null;
  createdAt: string;
  queryClient: ReturnType<typeof useQueryClient>;
}) {
  const [rotating, setRotating] = useState(false);
  const [rotated, setRotated] = useState<ApiKeyCreateResponse | null>(null);

  const revokeMutation = useMutation({
    mutationFn: () => client.revokeApiKey(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["api-keys"] }),
  });

  const rotateMutation = useMutation({
    mutationFn: () => client.rotateApiKey(id),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ["api-keys"] });
      setRotated(res);
      setRotating(false);
    },
  });

  const scopeStyle = scopeLabel(scope);

  const handleRotate = () => {
    setRotated(null);
    setRotating(true);
    rotateMutation.mutate();
  };

  const handleRevoke = () => {
    if (window.confirm(`Revoke "${name}"? Existing clients will lose access immediately.`)) {
      revokeMutation.mutate();
    }
  };

  return (
    <div className="border-t border-[var(--color-border)] first:border-t-0">
      <div className="flex items-center justify-between py-3 gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-[var(--color-text)] truncate">{name}</span>
            <Badge variant={scopeStyle.variant} size="sm">
              {scopeStyle.text}
            </Badge>
            {!isActive && (
              <Badge variant="error" size="sm">
                revoked
              </Badge>
            )}
          </div>
          <p className="text-xs font-mono text-[var(--color-text-muted)] mt-1 truncate">
            {keyPrefix}…
          </p>
          <p className="text-[10px] text-[var(--color-text-muted)] mt-0.5">
            Created {formatRelative(createdAt)} · {formatRelative(lastUsedAt)}
          </p>
        </div>

        <div className="flex items-center gap-1 shrink-0">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleRotate}
            loading={rotating}
            aria-label={`Rotate ${name}`}
          >
            <RefreshCw className="size-3.5" /> Rotate
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleRevoke}
            disabled={!isActive}
            aria-label={`Revoke ${name}`}
          >
            <Trash2 className="size-3.5" /> Revoke
          </Button>
        </div>
      </div>

      {rotating && (
        <div className="mb-3 pl-4 border-l-2 border-[var(--color-border)] space-y-2">
          <p className="text-xs text-[var(--color-text-muted)]">
            Rotating the key invalidates the previous secret. Deployers of this key must
            replace their credentials now.
          </p>
        </div>
      )}

      {rotated && (
        <div className="mb-3 pl-4 border-l-2 border-[var(--color-status-running)] rounded-r bg-[color-mix(in_srgb,var(--color-status-running)_10%,transparent)] px-4 py-3">
          <p className="text-xs font-medium text-[var(--color-status-running)] mb-2">
            Key rotated — copy your new secret now.
          </p>
          <div className="flex items-center gap-2">
            <code className="flex-1 bg-[var(--color-bg-muted)] rounded px-2 py-1 text-xs font-mono text-[var(--color-text)] truncate">
              {rotated.secret}
            </code>
            <CopyButton value={rotated.secret} />
          </div>
        </div>
      )}
    </div>
  );
}

function KeyList({ queryClient }: { queryClient: ReturnType<typeof useQueryClient> }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["api-keys"],
    queryFn: () => client.listApiKeys(),
  });

  if (isLoading) {
    return (
      <Card padding="lg">
        <div className="space-y-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      </Card>
    );
  }

  const keys = data ?? [];
  const activeKeys = keys.filter((k) => k.isActive);
  const retiredKeys = keys.filter((k) => !k.isActive);

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-3">
        <ShieldAlert className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          Managed Keys
        </p>
      </div>

      {error && (
        <p className="text-sm text-[var(--color-status-error)] mb-4">
          {(error as Error).message}
        </p>
      )}

      {keys.length === 0 ? (
        <EmptyState
          icon={<KeySquare className="size-8" />}
          title="No API keys yet"
          description="Create an inference key to authenticate clients against the /v1 proxy."
        />
      ) : (
        <div>
          {activeKeys.map((k) => (
            <KeyRow
              key={k.id}
              id={k.id}
              name={k.name}
              keyPrefix={k.keyPrefix}
              scope={k.scope}
              isActive={k.isActive}
              lastUsedAt={k.lastUsedAt}
              createdAt={k.createdAt}
              queryClient={queryClient}
            />
          ))}

          {retiredKeys.length > 0 && (
            <div className="mt-6 pt-4 border-t border-[var(--color-border)]">
              <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-2">
                Retired ({retiredKeys.length})
              </p>
              {retiredKeys.map((k) => (
                <KeyRow
                  key={k.id}
                  id={k.id}
                  name={k.name}
                  keyPrefix={k.keyPrefix}
                  scope={k.scope}
                  isActive={k.isActive}
                  lastUsedAt={k.lastUsedAt}
                  createdAt={k.createdAt}
                  queryClient={queryClient}
                />
              ))}
            </div>
          )}
        </div>
      )}
    </Card>
  );
}

// ─── Main Page ──────────────────────────────────────────────────────────

export default function ApiKeys() {
  const queryClient = useQueryClient();

  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          API Keys
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Manage keys that authenticate to the inference proxy. These are not login credentials.
        </p>
      </div>

      <Card padding="md">
        <div className="flex items-start gap-3">
          <ShieldAlert className="size-4 text-[var(--color-status-warning)] mt-0.5 shrink-0" />
          <p className="text-xs text-[var(--color-text-muted)] leading-relaxed">
            Inference API keys authenticate to the OpenAI-compatible proxy at{" "}
            <span className="font-mono">/v1</span> (or{" "}
            <span className="font-mono">/api/v1/models</span>). Login uses a separate cookie
            session — a login credential is not an inference key, and vice versa.
          </p>
        </div>
      </Card>

      <CreateKeySection queryClient={queryClient} />
      <KeyList queryClient={queryClient} />
    </div>
  );
}
