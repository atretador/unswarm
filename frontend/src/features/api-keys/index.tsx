import { useState } from "react";
import { useTranslation, Trans } from "react-i18next";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Key, Copy, RefreshCw, Trash2, Check, ShieldAlert, KeySquare, Bot, SlidersHorizontal, Terminal, Plus } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Skeleton, Button, Badge, EmptyState, Input, ConfirmDialog, Dialog } from "../../components/ui";
import type { ApiKeyCreateResponse, ApiKeyItem, PermissionMatrix } from "../../lib/api/types";
import { CLI_DOMAINS } from "../../lib/api/types";
import { ManageKeyModal, PermissionMatrixEditor } from "./manage-key-modal";
import { formatRelativeTime } from "../../i18n/format";

// ─── Copy-to-clipboard helper ──────────────────────────────────────────

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const { t } = useTranslation("api-keys");

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
      aria-label={t("copy")}
    >
      {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
      {copied ? t("copied") : t("copy")}
    </Button>
  );
}

function formatRelative(ts: string | null): string {
  if (!ts) return "—";
  return formatRelativeTime(ts);
}

function scopeLabel(scope: "inference" | "agent" | "control-plane", t: (key: string) => string) {
  if (scope === "control-plane") return { text: t("tabs.cli"), variant: "info" as const };
  return scope === "inference"
    ? { text: t("tabs.inference"), variant: "info" as const }
    : { text: t("tabs.agent"), variant: "outline" as const };
}

function emptyPermissions(): PermissionMatrix {
  return Object.fromEntries(CLI_DOMAINS.map((domain) => [domain, "none"])) as PermissionMatrix;
}

function permissionSummary(permissions: PermissionMatrix | undefined): string {
  if (!permissions) return "—";
  return CLI_DOMAINS.filter((d) => permissions[d] && permissions[d] !== "none")
    .map((d) => `${d}:${permissions[d] === "rw" ? "rw" : "r"}`).join(", ") || "—";
}

function CreateCliKeySection({ queryClient }: { queryClient: ReturnType<typeof useQueryClient> }) {
  const { t } = useTranslation("api-keys");
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [permissions, setPermissions] = useState<PermissionMatrix>(emptyPermissions);
  const [created, setCreated] = useState<ApiKeyCreateResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mutation = useMutation({
    mutationFn: () => client.createControlPlaneApiKey(name.trim(), permissions),
    onSuccess: (res) => { queryClient.invalidateQueries({ queryKey: ["api-keys"] }); setCreated(res); setError(null); },
    onError: (err: Error) => setError(err.message),
  });
  const close = () => { setOpen(false); setCreated(null); setName(""); setPermissions(emptyPermissions()); setError(null); mutation.reset(); };
  return <>
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-2"><Terminal className="size-4 text-[var(--color-primary)]" /><p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">{t("cli.newKey")}</p></div>
      <p className="text-sm text-[var(--color-text)] mb-4">{t("cli.createDesc")}</p>
      <Button type="button" variant="primary" size="md" onClick={() => setOpen(true)}><Plus className="size-3.5" />{t("cli.createKey")}</Button>
    </Card>
    <Dialog open={open} onOpenChange={(next) => !mutation.isPending && setOpen(next)} title={created ? t("cli.secretTitle") : t("cli.createKey")} className="sm:max-w-[680px]">
      <div className="px-5 py-4 space-y-4">
        {created ? <>
          <div className="rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-running)_12%,transparent)] border border-[color-mix(in_srgb,var(--color-status-running)_30%,transparent)] px-4 py-3 space-y-2">
            <p className="text-sm font-medium text-[var(--color-status-running)]">{t("keyCreated")}</p><p className="text-[10px] text-[var(--color-text-muted)]">{t("keyCreatedDesc")}</p>
            <div className="flex gap-2 items-center"><code className="flex-1 min-w-0 truncate bg-[var(--color-bg-muted)] rounded px-2 py-1 text-xs font-mono">{created.secret}</code><CopyButton value={created.secret} /></div>
            <code className="block text-[11px] text-[var(--color-text-muted)] break-all">export UNSWARM_API_KEY={created.secret}</code>
          </div><Button type="button" variant="primary" size="sm" onClick={close}>{t("cli.done")}</Button>
        </> : <form onSubmit={(e) => { e.preventDefault(); if (!name.trim()) { setError(t("validate.nameRequired")); return; } mutation.mutate(); }} className="space-y-4">
          <Input label={t("keyName")} value={name} onChange={(e) => setName(e.target.value)} placeholder={t("cli.namePlaceholder")} disabled={mutation.isPending} />
          <PermissionMatrixEditor permissions={permissions} onChange={setPermissions} disabled={mutation.isPending} />
          {error && <p className="text-sm text-[var(--color-status-error)]">{error}</p>}
          <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setOpen(false)}>{t("cli.cancel")}</Button><Button type="submit" variant="primary" loading={mutation.isPending}>{t("cli.createKey")}</Button></div>
        </form>}
      </div>
    </Dialog>
  </>;
}

// ─── Create Key form ────────────────────────────────────────────────────

function CreateKeySection({
  queryClient,
  scope,
}: {
  queryClient: ReturnType<typeof useQueryClient>;
  scope: "inference" | "agent" | "control-plane";
}) {
  const { t } = useTranslation("api-keys");
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<ApiKeyCreateResponse | null>(null);

  const createMutation = useMutation({
    mutationFn: (name: string) =>
      scope === "agent" ? client.createAgentApiKey(name) : client.createApiKey(name),
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
      setError(t("validate.nameRequired"));
      return;
    }
    createMutation.mutate(name.trim());
  };

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-3">
        <Key className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {scope === "agent" ? t("newAgentKey") : t("newInferenceKey")}
        </p>
      </div>

      <p className="text-sm text-[var(--color-text)] mb-4">
        {scope === "agent" ? (
          <Trans
            i18nKey="createDescAgent"
            ns="api-keys"
            values={{ channel1: "/api/agents", channel2: "/ws/agent" }}
            components={[
              <span className="font-mono" />,
              <span className="font-mono" />,
              <strong />,
            ]}
          />
        ) : (
          <Trans
            i18nKey="createDescInference"
            ns="api-keys"
            values={{ proxy: "/v1" }}
            components={[
              <span className="font-mono" />,
              <strong />,
            ]}
          />
        )}
      </p>

      {!created ? (
        <form onSubmit={handleSubmit} className="space-y-3">
          <Input
            label={t("keyName")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={scope === "agent" ? t("placeholders.keyName") : t("placeholders.keyNameAgent")}
            disabled={createMutation.isPending}
          />
          {error && <p className="text-sm text-[var(--color-status-error)]">{error}</p>}
          <Button type="submit" variant="primary" size="md" loading={createMutation.isPending}>
            {t("createKey")}
          </Button>
        </form>
      ) : (
        <div className="space-y-3">
          <div className="rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-running)_15%,transparent)] border border-[color-mix(in_srgb,var(--color-status-running)_30%,transparent)] px-4 py-3">
            <p className="text-sm font-medium text-[var(--color-status-running)] mb-2">
              {t("keyCreated")}
            </p>
            <p className="text-[10px] text-[var(--color-text-muted)] mb-2">
              {t("keyCreatedDesc")}
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
            {t("createAnother")}
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
  scope: "inference" | "agent" | "control-plane";
  isActive: boolean;
  lastUsedAt: string | null;
  createdAt: string;
  queryClient: ReturnType<typeof useQueryClient>;
}) {
  const { t } = useTranslation("api-keys");
  const [rotating, setRotating] = useState(false);
  const [rotated, setRotated] = useState<ApiKeyCreateResponse | null>(null);
  const [rotateError, setRotateError] = useState<string | null>(null);
  const [revokeError, setRevokeError] = useState<string | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState<ApiKeyItem | null>(null);
  const [manageOpen, setManageOpen] = useState(false);

  const revokeMutation = useMutation({
    mutationFn: () => client.revokeApiKey(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["api-keys"] });
      setConfirmRevoke(null);
      setRevokeError(null);
    },
    onError: (err: Error) => {
      setConfirmRevoke(null);
      setRevokeError(err.message);
    },
  });

  const rotateMutation = useMutation({
    mutationFn: () => client.rotateApiKey(id),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ["api-keys"] });
      setRotated(res);
      setRotating(false);
      setRotateError(null);
    },
    onError: (err: Error) => {
      setRotating(false);
      setRotateError(err.message);
    },
  });

  const scopeStyle = scopeLabel(scope, t);
  const permissionsQuery = useQuery({ queryKey: ["api-key-permissions", id], queryFn: () => client.getApiKeyPermissions(id), enabled: scope === "control-plane" });

  const handleRotate = () => {
    setRotated(null);
    setRotateError(null);
    setRotating(true);
    rotateMutation.mutate();
  };

  const handleRevoke = () => {
    setRevokeError(null);
    setConfirmRevoke({ id, name, keyPrefix, scope, isActive, lastUsedAt, createdAt } as ApiKeyItem);
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
                {t("revoked")}
              </Badge>
            )}
          </div>
          <p className="text-xs font-mono text-[var(--color-text-muted)] mt-1 truncate">
            {keyPrefix}…
          </p>
          {scope === "control-plane" && permissionsQuery.data && (
            <p className="text-[10px] text-[var(--color-text-muted)] mt-0.5">
              {permissionSummary(permissionsQuery.data)}
            </p>
          )}
          <p className="text-[10px] text-[var(--color-text-muted)] mt-0.5">
            {t("createdPrefix")} {formatRelative(createdAt)} · {formatRelative(lastUsedAt)}
          </p>
        </div>

        <div className="flex items-center gap-1 shrink-0">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => setManageOpen(true)}
            aria-label={t("aria.manageKey", { name })}
          >
            <SlidersHorizontal className="size-3.5" /> {t("aria.manage")}
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleRotate}
            loading={rotating}
            aria-label={t("aria.rotateKey", { name })}
          >
            <RefreshCw className="size-3.5" /> {t("aria.rotate")}
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleRevoke}
            disabled={!isActive}
            aria-label={t("aria.revokeKey", { name })}
          >
            <Trash2 className="size-3.5" /> {t("aria.revoke")}
          </Button>
        </div>
      </div>

      {(rotateError || revokeError) && (
        <div className="mb-3 pl-4 border-l-2 border-[color-mix(in_srgb,var(--color-status-error)_50%,var(--color-border))] rounded-r bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-4 py-3">
          <p className="text-xs text-[var(--color-status-error)]">
            {rotateError ?? revokeError}
          </p>
        </div>
      )}

      {rotating && (
        <div className="mb-3 pl-4 border-l-2 border-[var(--color-border)] space-y-2">
          <p className="text-xs text-[var(--color-text-muted)]">
            {t("rotateWarningComplete")}
          </p>
        </div>
      )}

      {rotated && (
        <div className="mb-3 pl-4 border-l-2 border-[var(--color-status-running)] rounded-r bg-[color-mix(in_srgb,var(--color-status-running)_10%,transparent)] px-4 py-3">
          <p className="text-xs font-medium text-[var(--color-status-running)] mb-2">
            {t("rotated")}
          </p>
          <div className="flex items-center gap-2">
            <code className="flex-1 bg-[var(--color-bg-muted)] rounded px-2 py-1 text-xs font-mono text-[var(--color-text)] truncate">
              {rotated.secret}
            </code>
            <CopyButton value={rotated.secret} />
          </div>
        </div>
      )}

      <ConfirmDialog
        open={confirmRevoke !== null}
        title={t("revoke.confirm", { name: confirmRevoke?.name ?? name })}
        description={t("revoke.desc")}
        confirmLabel={t("aria.revoke")}
        variant="danger"
        loading={revokeMutation.isPending}
        onConfirm={() => {
          if (confirmRevoke) revokeMutation.mutate();
        }}
        onCancel={() => setConfirmRevoke(null)}
      />

      <ManageKeyModal
        open={manageOpen}
        onOpenChange={setManageOpen}
        apiKey={{ id, name, keyPrefix, scope, isActive, lastUsedAt, createdAt }}
      />
    </div>
  );
}

function KeyList({
  queryClient,
  scope,
}: {
  queryClient: ReturnType<typeof useQueryClient>;
  scope: "inference" | "agent" | "control-plane";
}) {
  const { t } = useTranslation("api-keys");
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

  const allKeys = data ?? [];
  const keys = allKeys.filter((k) => k.scope === scope);
  const activeKeys = keys.filter((k) => k.isActive);
  const retiredKeys = keys.filter((k) => !k.isActive);

  return (
    <Card padding="lg">
      <div className="flex items-center gap-2 mb-3">
        <ShieldAlert className="size-4 text-[var(--color-text-muted)]" />
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {scope === "agent" ? t("agentKeys") : scope === "control-plane" ? t("cliKeys") : t("inferenceKeys")}
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
          title={t("noKeys", { scope })}
          description={
            scope === "agent"
              ? t("noKeysAgent")
              : scope === "control-plane"
              ? t("cli.noKeys")
              : t("noKeysInference")
          }
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
                {t("retired", { count: retiredKeys.length })}
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

type Tab = "inference" | "agent" | "control-plane";

export default function ApiKeys() {
  const { t } = useTranslation("api-keys");
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<Tab>("inference");

  const tabs: { key: Tab; label: string; icon: typeof Key }[] = [
    { key: "inference", label: t("tabs.inference"), icon: Key },
    { key: "agent", label: t("tabs.agent"), icon: Bot },
    { key: "control-plane", label: t("tabs.cli"), icon: Terminal },
  ];

  return (
    <div className="p-6 space-y-6 max-w-3xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          {t("title")}
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          {t("desc")}
        </p>
      </div>

      <Card padding="md">
        <div className="flex items-start gap-3">
          <ShieldAlert className="size-4 text-[var(--color-status-warning)] mt-0.5 shrink-0" />
          <p className="text-xs text-[var(--color-text-muted)] leading-relaxed">
            <Trans
              i18nKey="info"
              ns="api-keys"
              values={{ proxy: "/v1" }}
              components={[<span className="font-mono" />]}
            />
          </p>
        </div>
      </Card>

      {/* Tab bar — same pattern as swarm ManageRuntimesModal */}
      <div role="tablist" aria-label={t("aria.keyScope")} className="flex border-b border-[var(--color-border-subtle)]">
        {tabs.map((tab) => (
          <button
            key={tab.key}
            type="button"
            role="tab"
            id={`apikeys-tab-${tab.key}`}
            aria-selected={activeTab === tab.key}
            aria-controls="apikeys-panel"
            onClick={() => setActiveTab(tab.key)}
            className={`
              flex items-center gap-1.5 px-4 py-2 text-xs font-medium transition-colors
              border-b-2 -mb-px
              ${
                activeTab === tab.key
                  ? "border-b-2 border-[var(--color-primary)] text-[var(--color-primary)]"
                  : "border-b-2 border-transparent text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
              }
            `}
          >
            <tab.icon className="size-3.5" />
            {tab.label}
          </button>
        ))}
      </div>

      {/* Tab content */}
      <div role="tabpanel" id="apikeys-panel" aria-labelledby={`apikeys-tab-${activeTab}`}>
        {activeTab === "control-plane" ? <CreateCliKeySection queryClient={queryClient} /> : <CreateKeySection queryClient={queryClient} scope={activeTab} />}
        <div className="mt-6">
          <KeyList queryClient={queryClient} scope={activeTab} />
        </div>
      </div>
    </div>
  );
}
