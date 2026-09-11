import { useState, useEffect, useCallback, useRef } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Cloud,
  Plus,
  Pencil,
  Trash2,
  Loader2,
  Check,
  AlertCircle,
  ExternalLink,
  Copy,
  KeyRound,
  Shield,
  RefreshCw,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Skeleton,
  Input,
  Button,
  Badge,
  EmptyState,
  ConfirmDialog,
  Dialog,
  TriCheckbox,
} from "../../components/ui";
import { getProviderModelCatalog } from "../api-keys/api-keys-api";
import type {
  CloudProvider,
  CloudProviderAuthType,
  CloudProviderInput,
  CloudProviderRead,
  CloudProviderUpdateInput,
} from "../../lib/api/types";
import { formatRelativeTime } from "../../i18n/format";
import i18n from "../../i18n";

// ─── Helpers ─────────────────────────────────────────────────────

function formatExpiry(iso: string, t: (key: string) => string): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  const diff = new Date(iso).getTime() - Date.now();
  if (diff <= 0) {
    return t('expired');
  }
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  const diffMin = Math.floor(diff / 60000);
  if (diffMin < 60) return rtf.format(diffMin, 'minute');
  const hours = Math.floor(diffMin / 60);
  const mins = diffMin % 60;
  if (mins > 0) {
    return `${rtf.format(hours, 'hour')} ${rtf.format(mins, 'minute')}`;
  }
  return rtf.format(hours, 'hour');
}

// ─── Provider Row ────────────────────────────────────────────────

function ProviderRow({
  provider: p,
  onEdit,
  onDelete,
}: {
  provider: CloudProvider;
  onEdit: (provider: CloudProvider) => void;
  onDelete: (provider: CloudProvider) => void;
}) {
  const { t } = useTranslation("providers");

  return (
    <div className="flex items-center gap-4 px-4 py-3 border-b border-[var(--color-border-subtle)] last:border-b-0 hover:bg-[var(--color-bg-muted)]/50 transition-colors duration-[var(--duration-fast)]">
      {/* Name */}
      <div className="flex items-center gap-3 min-w-0 flex-1">
        <div className="flex items-center justify-center size-8 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] shrink-0">
          <Cloud className="size-4" />
        </div>
        <span className="text-sm text-[var(--color-text)] truncate font-medium">
          {p.name}
        </span>
        {p.authType === 1 && (
          <Badge variant="info" size="sm">
            <Shield className="size-2.5" />
            {t("gptSub")}
          </Badge>
        )}
      </div>

      {/* Base URL */}
      <div className="shrink-0 w-[220px] min-w-0">
        <span className="text-xs text-[var(--color-text-muted)] truncate block" title={p.baseUrl}>
          {p.baseUrl}
        </span>
      </div>

      {/* API Key Hint */}
      <div className="shrink-0 w-[120px] min-w-0">
        <span className="text-xs text-[var(--color-text-muted)] font-mono">
          {p.authType === 1 ? "\u2014" : (p.apiKeyHint || "\u2014")}
        </span>
      </div>

      {/* Model Count */}
      <div className="shrink-0 w-[80px] text-right">
        <span className="text-xs text-[var(--color-text)]">
          {p.modelCount} {t("model", { count: p.modelCount })}
        </span>
      </div>

      {/* Updated */}
      <div className="shrink-0 w-[100px] text-right">
        <span className="text-xs text-[var(--color-text-muted)]">
          {formatRelativeTime(p.updatedAt)}
        </span>
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1 shrink-0">
        <Button variant="ghost" size="sm" onClick={() => onEdit(p)}>
          <Pencil className="size-3.5" />
          {t("edit", { ns: "common" })}
        </Button>
        <Button variant="danger" size="sm" onClick={() => onDelete(p)}>
          <Trash2 className="size-3.5" />
        </Button>
      </div>
    </div>
  );
}

// ─── Auth Type Selector ──────────────────────────────────────────

function AuthTypeSelector({
  value,
  onChange,
  disabled,
}: {
  value: CloudProviderAuthType;
  onChange: (v: CloudProviderAuthType) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation("providers");

  const options: { label: string; value: CloudProviderAuthType; icon: typeof KeyRound }[] = [
    { label: t("authTypeApiKey"), value: 0, icon: KeyRound },
    { label: t("authTypeGptSubscription"), value: 1, icon: Shield },
  ];

  return (
    <div className="space-y-1.5">
      <label className="text-xs font-medium text-[var(--color-text-muted)]">
        {t("authType")}
      </label>
      <div className="flex rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-bg-muted)]/30 p-0.5">
        {options.map((opt) => {
          const Icon = opt.icon;
          const isActive = value === opt.value;
          return (
            <button
              key={opt.value}
              type="button"
              disabled={disabled}
              onClick={() => onChange(opt.value)}
              className={`
                flex-1 flex items-center justify-center gap-1.5 px-3 py-1.5 text-xs font-medium
                rounded-[var(--radius-sm)] transition-all duration-[var(--duration-fast)]
                cursor-pointer select-none
                ${isActive
                  ? "bg-[var(--color-bg-surface)] text-[var(--color-text)] shadow-sm border border-[var(--color-border-subtle)]"
                  : "text-[var(--color-text-muted)] hover:text-[var(--color-text)] border border-transparent"
                }
                ${disabled ? "opacity-50 cursor-not-allowed" : ""}
              `}
            >
              <Icon className="size-3.5" />
              {opt.label}
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ─── OAuth Modal ─────────────────────────────────────────────────

function OAuthModal({
  open,
  onClose,
  providerId,
  onSuccess,
}: {
  open: boolean;
  onClose: () => void;
  providerId: string;
  onSuccess: () => void;
}) {
  const { t } = useTranslation("providers");
  const [userCode, setUserCode] = useState<string | null>(null);
  const [_deviceAuthId, setDeviceAuthId] = useState<string | null>(null);
  const [verificationUrl, setVerificationUrl] = useState<string>("https://auth.openai.com/codex/device");
  const [status, setStatus] = useState<"loading" | "waiting" | "success" | "error">("loading");
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Clean up intervals on unmount / close
  useEffect(() => {
    return () => {
      if (pollRef.current) clearInterval(pollRef.current);
    };
  }, []);

  // Start OAuth flow when modal opens
  useEffect(() => {
    if (!open) {
      // Reset state on close
      setUserCode(null);
      setDeviceAuthId(null);
      setVerificationUrl("https://auth.openai.com/codex/device");
      setStatus("loading");
      setErrorMsg(null);
      setCopied(false);
      if (pollRef.current) clearInterval(pollRef.current);
      return;
    }

    let cancelled = false;

    const startFlow = async () => {
      try {
        const result = await client.startOAuth(providerId);
        if (cancelled) return;

        setUserCode(result.userCode);
        setDeviceAuthId(result.deviceAuthId);
        setVerificationUrl(result.verificationUrl);
        setStatus("waiting");

        // Start polling with the real device auth id and user code from the server
        const pollInterval = Math.max(result.interval, 2) * 1000; // interval from server, min 2s
        pollRef.current = setInterval(async () => {
          try {
            const pollResult = await client.pollOAuth(providerId, {
              deviceAuthId: result.deviceAuthId,
              userCode: result.userCode,
            });
            if (cancelled) return;

            if (pollResult.status === "success") {
              if (pollRef.current) clearInterval(pollRef.current);
              setStatus("success");
              // Wait a beat for the success state to show, then close
              setTimeout(() => {
                if (!cancelled) onSuccess();
              }, 1200);
            }
          } catch {
            // Poll errors are expected until the user authenticates — don't update UI
          }
        }, pollInterval);
      } catch (err) {
        if (cancelled) return;
        setStatus("error");
        setErrorMsg(err instanceof Error ? err.message : t("oauthFailedToStart"));
      }
    };

    startFlow();

    return () => {
      cancelled = true;
    };
  }, [open, providerId, onSuccess]);

  const handleCopy = async () => {
    if (!userCode) return;
    try {
      await navigator.clipboard.writeText(userCode);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard API may be blocked — fallback
    }
  };

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()} title={t("signInWithChatGPT")}>
      <div className="p-5 space-y-5">
        {status === "loading" && (
          <div className="flex flex-col items-center gap-3 py-6">
            <Loader2 className="size-6 animate-spin text-[var(--color-primary)]" />
            <span className="text-sm text-[var(--color-text-muted)]">{t("signInProgress")}</span>
          </div>
        )}

        {status === "waiting" && userCode && (
          <>
            <div className="rounded-[var(--radius-md)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30 p-4 text-center space-y-3">
              <p className="text-sm text-[var(--color-text)]">
                {t("open")}{" "}
                <a
                  href={verificationUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-[var(--color-primary)] underline underline-offset-2 hover:text-[var(--color-primary-hover)] inline-flex items-center gap-1"
                >
                  auth.openai.com/codex/device
                  <ExternalLink className="size-3" />
                </a>
              </p>
              <p className="text-xs text-[var(--color-text-muted)]">{t("signInCode")}</p>

              {/* Device code display */}
              <div className="flex items-center justify-center gap-2">
                <span className="text-2xl font-mono font-bold tracking-[0.25em] text-[var(--color-text-heading)] select-all">
                  {userCode}
                </span>
                <button
                  type="button"
                  onClick={handleCopy}
                  className="flex size-7 items-center justify-center rounded-[var(--radius-sm)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] cursor-pointer"
                  title={t("copyCode")}
                >
                  {copied ? <Check className="size-3.5 text-[var(--color-status-success)]" /> : <Copy className="size-3.5" />}
                </button>
              </div>
            </div>

            <p className="text-xs text-[var(--color-text-muted)] text-center">
              {t("waitingAuth")}
            </p>
          </>
        )}

        {status === "success" && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="flex size-10 items-center justify-center rounded-full bg-[color-mix(in_srgb,var(--color-status-success)_15%,transparent)]">
              <Check className="size-5 text-[var(--color-status-success)]" />
            </div>
            <span className="text-sm text-[var(--color-text)] font-medium">{t("authSuccess")}</span>
            <span className="text-xs text-[var(--color-text-muted)]">{t("fetchingModels")}</span>
          </div>
        )}

        {status === "error" && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="flex size-10 items-center justify-center rounded-full bg-[color-mix(in_srgb,var(--color-status-error)_15%,transparent)]">
              <AlertCircle className="size-5 text-[var(--color-status-error)]" />
            </div>
            <span className="text-sm text-[var(--color-text)] font-medium">{t("authFailed")}</span>
            <span className="text-xs text-[var(--color-text-muted)] text-center max-w-xs">{errorMsg}</span>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button
            variant="secondary"
            size="sm"
            onClick={onClose}
            disabled={status === "loading"}
          >
            {status === "success" ? t("done") : t("cancel")}
          </Button>
          {status === "error" && (
            <Button
              variant="primary"
              size="sm"
              onClick={() => {
                // Reset to re-trigger the effect
                setStatus("loading");
                setErrorMsg(null);
              }}
            >
              <RefreshCw className="size-3.5" />
              {t("retry", { ns: "common" })}
            </Button>
          )}
        </div>
      </div>
    </Dialog>
  );
}

// ─── Add / Edit Dialog ──────────────────────────────────────────

function ProviderDialog({
  open,
  onClose,
  editProvider,
}: {
  open: boolean;
  onClose: () => void;
  editProvider: CloudProvider | null;
}) {
  const { t } = useTranslation("providers");
  const queryClient = useQueryClient();
  const isEdit = editProvider !== null;

  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [authType, setAuthType] = useState<CloudProviderAuthType>(0);
  const [error, setError] = useState<string | null>(null);

  // Fetch models state
  const [fetchedModels, setFetchedModels] = useState<string[] | null>(null);
  const [fetchModelsPending, setFetchModelsPending] = useState(false);
  const [fetchModelsError, setFetchModelsError] = useState<string | null>(null);

  // Model selection state
  const [selectedModels, setSelectedModels] = useState<Set<string>>(new Set());
  const [savedModelIds, setSavedModelIds] = useState<Set<string> | null>(null);

  // OAuth state for subscription providers
  const [oauthModalOpen, setOauthModalOpen] = useState(false);
  const [createdProviderId, setCreatedProviderId] = useState<string | null>(null);

  // Edit mode: token status
  const [readProvider, setReadProvider] = useState<CloudProviderRead | null>(null);
  const [tokenRefreshing, setTokenRefreshing] = useState(false);

  // Reset state on open / editProvider change
  useEffect(() => {
    if (open) {
      if (editProvider) {
        setName(editProvider.name);
        setBaseUrl(editProvider.baseUrl);
        setApiKey("");
        setAuthType(editProvider.authType);
      } else {
        setName("");
        setBaseUrl("");
        setApiKey("");
        setAuthType(0);
      }
      setError(null);
      setFetchedModels(null);
      setFetchModelsError(null);
      setSelectedModels(new Set());
      setSavedModelIds(null);
      setOauthModalOpen(false);
      setCreatedProviderId(null);
      setReadProvider(null);
      setTokenRefreshing(false);
    }
  }, [open, editProvider]);

  // Fetch full provider details in edit mode
  useEffect(() => {
    if (!open || !editProvider) return;
    let cancelled = false;
    client.getCloudProvider(editProvider.id).then((data) => {
      if (!cancelled) setReadProvider(data);
    }).catch(() => {});
    return () => { cancelled = true; };
  }, [open, editProvider]);

  // Fetch saved models from catalog when editing an existing provider
  useEffect(() => {
    if (!open || !editProvider) return;

    let cancelled = false;
    getProviderModelCatalog()
      .then((catalog) => {
        if (cancelled) return;
        const entry = catalog.find(
          (e) => e.name === editProvider.name && e.kind === "cloud",
        );
        setSavedModelIds(new Set(entry?.models ?? []));
      })
      .catch(() => {
        if (!cancelled) setSavedModelIds(new Set());
      });

    return () => { cancelled = true; };
  }, [open, editProvider]);

  const createMutation = useMutation({
    mutationFn: (data: CloudProviderInput) => client.createCloudProvider(data),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: CloudProviderUpdateInput }) =>
      client.updateCloudProvider(id, data),
  });

  const handleFetchModels = async () => {
    setFetchModelsPending(true);
    setFetchModelsError(null);
    setFetchedModels(null);
    try {
      let result: { modelIds: string[] };
      if (isEdit && editProvider) {
        result = await client.fetchCloudProviderModels(editProvider.id);
        queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
      } else {
        result = await client.testAndFetchModels(baseUrl.trim(), apiKey);
      }
      setFetchedModels(result.modelIds);

      // Populate selection: for edit, pre-check previously saved models; otherwise select all
      if (isEdit && savedModelIds && savedModelIds.size > 0) {
        setSelectedModels(new Set(result.modelIds.filter((id) => savedModelIds.has(id))));
      } else {
        setSelectedModels(new Set(result.modelIds));
      }
    } catch (err) {
      setFetchModelsError(err instanceof Error ? err.message : t("validation.saveFailed"));
    } finally {
      setFetchModelsPending(false);
    }
  };

  const handleRefreshToken = async () => {
    if (!editProvider) return;
    setTokenRefreshing(true);
    try {
      const result = await client.refreshOAuth(editProvider.id);
      if (result.success) {
        // Refresh the read provider data
        const updated = await client.getCloudProvider(editProvider.id);
        setReadProvider(updated);
      }
    } catch {
      // Token refresh failed — non-critical
    } finally {
      setTokenRefreshing(false);
    }
  };

  // Select all / deselect all
  const allModelsSelected =
    fetchedModels !== null &&
    fetchedModels.length > 0 &&
    selectedModels.size === fetchedModels.length;
  const someModelsSelected =
    fetchedModels !== null && selectedModels.size > 0 && !allModelsSelected;

  const toggleSelectAll = useCallback(() => {
    if (allModelsSelected) {
      setSelectedModels(new Set());
    } else if (fetchedModels) {
      setSelectedModels(new Set(fetchedModels));
    }
  }, [allModelsSelected, fetchedModels]);

  const toggleModel = useCallback((modelId: string) => {
    setSelectedModels((prev) => {
      const next = new Set(prev);
      if (next.has(modelId)) {
        next.delete(modelId);
      } else {
        next.add(modelId);
      }
      return next;
    });
  }, []);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setFetchModelsError(null);

    if (!name.trim()) {
      setError(t("validation.nameRequired"));
      return;
    }

    if (isEdit) {
      // Edit mode — always requires a base URL
      if (!baseUrl.trim()) {
        setError(t("validation.baseUrlRequired"));
        return;
      }
    } else {
      // Add mode
      if (authType === 0) {
        // API Key mode — requires baseUrl and apiKey
        if (!baseUrl.trim()) {
          setError(t("validation.baseUrlRequired"));
          return;
        }
        if (!apiKey) {
          setError(t("validation.apiKeyRequired"));
          return;
        }
      } else {
        // GPT Subscription mode — baseUrl is auto-filled
        if (!baseUrl.trim()) {
          setBaseUrl("https://chatgpt.com");
        }
      }
    }

    try {
      if (isEdit && editProvider) {
        const patch: CloudProviderUpdateInput = {
          baseUrl: baseUrl.trim(),
          apiKey: apiKey || null,
        };
        await updateMutation.mutateAsync({ id: editProvider.id, data: patch });

        // Persist model selection if the user fetched and curated models
        if (fetchedModels !== null) {
          await client.saveCloudProviderModels(
            editProvider.id,
            Array.from(selectedModels),
          );
        }
      } else {
        const input: CloudProviderInput = {
          name: name.trim(),
          baseUrl: (authType === 1 && !baseUrl.trim()) ? "https://chatgpt.com" : baseUrl.trim(),
          apiKey: authType === 1 ? "" : apiKey,
          authType,
        };
        const result = await createMutation.mutateAsync(input);

        if (authType === 1) {
          // Subscription provider — open OAuth flow
          setCreatedProviderId(result.id);
          setOauthModalOpen(true);
          // Don't close the dialog yet — OAuth modal will handle the rest
        } else if (fetchedModels !== null && selectedModels.size > 0) {
          // Save the user's curated selection
          await client.saveCloudProviderModels(
            result.id,
            Array.from(selectedModels),
          );
          queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
          queryClient.invalidateQueries({ queryKey: ["models"] });
          queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
          onClose();
        } else {
          // No models fetched — auto-fetch all so they appear on the Models page
          try {
            await client.fetchCloudProviderModels(result.id);
          } catch {
            // Non-critical — models can be fetched later via Edit
          }
          queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
          queryClient.invalidateQueries({ queryKey: ["models"] });
          queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
          onClose();
        }
      }

      if (isEdit) {
        queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
        queryClient.invalidateQueries({ queryKey: ["models"] });
        queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
        onClose();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t("validation.saveFailed"));
    }
  };

  const handleOAuthSuccess = async () => {
    // OAuth completed — auto-fetch models then close everything
    if (createdProviderId) {
      try {
        await client.fetchCloudProviderModels(createdProviderId);
      } catch {
        // Non-critical
      }
    }
    queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
    queryClient.invalidateQueries({ queryKey: ["models"] });
    queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
    setOauthModalOpen(false);
    setCreatedProviderId(null);
    onClose();
  };

  const isSubscriptionEdit = isEdit && editProvider?.authType === 1;

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <>
      <Dialog open={open} onOpenChange={(o) => !o && onClose()} title={isEdit ? t("editProvider") : t("addProvider")}>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {/* Auth type selector — only in add mode */}
          {!isEdit && (
            <AuthTypeSelector
              value={authType}
              onChange={setAuthType}
              disabled={isEdit}
            />
          )}

          {/* Edit mode: show auth type as read-only */}
          {isSubscriptionEdit && (
            <div className="flex items-center gap-2 p-2.5 rounded-[var(--radius-md)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30">
              <Shield className="size-4 text-[var(--color-primary)]" />
              <span className="text-xs text-[var(--color-text-muted)]">
                {t("fields.chatgptSub")}
              </span>
            </div>
          )}

          <Input
            label={t("fields.name")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            disabled={isEdit}
            placeholder={t("placeholders.name")}
            autoFocus={!isEdit}
          />

          {/* Base URL — hidden for subscription providers in add mode */}
          {(!authType || isEdit) && (
            <Input
              label={t("fields.baseUrl")}
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={isSubscriptionEdit ? "https://chatgpt.com" : "https://api.openai.com/v1"}
              disabled={isSubscriptionEdit}
            />
          )}

          {/* API Key — only for API Key auth type */}
          {authType === 0 && (
            <Input
              label={isEdit ? t("fields.apiKeyKeepExisting") : t("fields.apiKey")}
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={isEdit ? "sk-..." : ""}
              autoComplete="off"
            />
          )}

          {/* Subscription provider edit mode: token status + actions */}
          {isSubscriptionEdit && readProvider && (
            <div className="space-y-3">
              {readProvider.chatgptAccountId && (
                <div className="text-xs text-[var(--color-text-muted)]">
                  {t("fields.account", { id: readProvider.chatgptAccountId })}
                </div>
              )}
              {readProvider.tokenExpiresAt && (
                <div className="flex items-center gap-2">
                  <div className={`size-2 rounded-full ${new Date(readProvider.tokenExpiresAt) > new Date() ? "bg-[var(--color-status-success)]" : "bg-[var(--color-status-error)]"}`} />
                  <span className="text-xs text-[var(--color-text-muted)]">
                    {t("fields.tokenExpiry", { expiry: formatExpiry(readProvider.tokenExpiresAt, t) })}
                  </span>
                </div>
              )}
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={handleRefreshToken}
                  disabled={tokenRefreshing}
                >
                  {tokenRefreshing ? (
                    <Loader2 className="size-3.5 animate-spin" />
                  ) : (
                    <RefreshCw className="size-3.5" />
                  )}
                  {t("refreshToken")}
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    setOauthModalOpen(true);
                  }}
                >
                  <KeyRound className="size-3.5" />
                  {t("reAuthenticate")}
                </Button>
              </div>
            </div>
          )}

          {/* Fetch Models — available in API key mode (add and edit) and subscription edit mode */}
          {authType === 0 && (
            <div className="space-y-2 pt-1">
              <div className="flex items-center gap-3">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={handleFetchModels}
                  disabled={fetchModelsPending || !baseUrl.trim() || (!isEdit && !apiKey)}
                >
                  {fetchModelsPending ? (
                    <Loader2 className="size-3.5 animate-spin" />
                  ) : (
                    <Cloud className="size-3.5" />
                  )}
                  {t("fetchModels")}
                </Button>
                {fetchedModels !== null && (
                  <span className="flex items-center gap-1.5 text-xs text-[var(--color-status-success)]">
                    <Check className="size-3.5" />
                    {t("modelsFound", { count: fetchedModels.length })}
                  </span>
                )}
                {fetchModelsError && (
                  <span className="flex items-center gap-1.5 text-xs text-[var(--color-status-error)]">
                    <AlertCircle className="size-3.5" />
                    {fetchModelsError}
                  </span>
                )}
              </div>
              {fetchedModels !== null && fetchedModels.length > 0 && (
                <div className="space-y-2">
                  {/* Select All / Deselect All + count */}
                  <div className="flex items-center justify-between px-1">
                    <TriCheckbox
                      checked={allModelsSelected}
                      indeterminate={someModelsSelected}
                      onChange={toggleSelectAll}
                      label={allModelsSelected ? t("deselectAllModels") : t("selectAllModels")}
                    />
                    <span className="text-xs text-[var(--color-text-muted)]">
                      {t("modelsSelected", { count: selectedModels.size, total: fetchedModels.length })}
                    </span>
                  </div>

                  {/* Scrollable model list */}
                  <div className="max-h-48 overflow-y-auto rounded-md border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30">
                    <div className="p-2 space-y-0.5">
                      {fetchedModels.map((modelId) => (
                        <label
                          key={modelId}
                          className="flex items-center gap-2 px-2 py-1 text-xs text-[var(--color-text)] hover:bg-[var(--color-bg-muted)]/50 rounded cursor-pointer select-none transition-colors duration-[var(--duration-fast)]"
                        >
                          <input
                            type="checkbox"
                            checked={selectedModels.has(modelId)}
                            onChange={() => toggleModel(modelId)}
                            className="size-3.5 rounded accent-[var(--color-primary)] cursor-pointer"
                          />
                          <span className="font-mono truncate">{modelId}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                </div>
              )}
            </div>
          )}

          {error && (
            <p className="text-sm text-[var(--color-status-error)]">{error}</p>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <Button variant="secondary" size="sm" onClick={onClose} disabled={isPending}>
              {t("cancel")}
            </Button>
            <Button type="submit" variant="primary" size="sm" loading={isPending}>
              {isEdit ? t("saveChanges") : t("addProvider")}
            </Button>
          </div>
        </form>
      </Dialog>

      {/* OAuth Modal — opened after provider creation or re-auth */}
      <OAuthModal
        open={oauthModalOpen}
        onClose={() => {
          setOauthModalOpen(false);
          setCreatedProviderId(null);
          // If we just created a provider and OAuth was cancelled, still close the main dialog
          if (createdProviderId) {
            onClose();
          }
        }}
        providerId={createdProviderId ?? editProvider?.id ?? ""}
        onSuccess={handleOAuthSuccess}
      />
    </>
  );
}

// ─── Main Providers Page ─────────────────────────────────────────

export default function Providers() {
  const { t } = useTranslation("providers");
  const queryClient = useQueryClient();

  const {
    data: providers,
    isLoading,
  } = useQuery({
    queryKey: ["cloud-providers"],
    queryFn: () => client.listCloudProviders(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteCloudProvider(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cloud-providers"] }),
  });

  const [dialogOpen, setDialogOpen] = useState(false);
  const [editTarget, setEditTarget] = useState<CloudProvider | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<CloudProvider | null>(null);

  const handleEdit = (provider: CloudProvider) => {
    setEditTarget(provider);
    setDialogOpen(true);
  };

  const handleAdd = () => {
    setEditTarget(null);
    setDialogOpen(true);
  };

  const handleCloseDialog = () => {
    setDialogOpen(false);
    setEditTarget(null);
  };

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Page header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          {t("cloudProviders")}
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          {t("cloudProvidersDesc")}
        </p>
      </div>

      {/* Providers card */}
      <Card padding="none">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--color-border-subtle)]">
          <div className="flex items-center gap-2">
            <Cloud className="size-4 text-[var(--color-text-muted)]" />
            <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              {t("cloudProviders")}
            </p>
          </div>
          <Button variant="primary" size="sm" onClick={handleAdd}>
            <Plus className="size-3.5" />
            {t("addProvider")}
          </Button>
        </div>

        {/* Content */}
        {isLoading ? (
          <div className="p-4 space-y-3">
            {Array.from({ length: 2 }, (_, i) => (
              <Skeleton key={i} className="h-14 w-full" />
            ))}
          </div>
        ) : providers && providers.length > 0 ? (
          <div>
            {/* Column headers */}
            <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--color-border)] bg-[var(--color-bg-muted)]/30">
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider min-w-0 flex-1">
                {t("colProvider")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[220px]">
                {t("colBaseUrl")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[120px]">
                {t("colApiKey")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[80px] text-right">
                {t("colModels")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[100px] text-right">
                {t("colUpdated")}
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[120px] text-right">
                {t("colActions")}
              </span>
            </div>

            {providers.map((p) => (
              <ProviderRow
                key={p.id}
                provider={p}
                onEdit={handleEdit}
                onDelete={setDeleteTarget}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon={<Cloud className="size-12" strokeWidth={1.5} />}
            title={t("noProviders")}
            description={t("noProvidersDesc")}
            action={
              <Button variant="primary" size="sm" onClick={handleAdd}>
                <Plus className="size-3.5" />
                {t("addProvider")}
              </Button>
            }
          />
        )}
      </Card>

      {/* Add / Edit Dialog */}
      <ProviderDialog
        open={dialogOpen}
        onClose={handleCloseDialog}
        editProvider={editTarget}
      />

      {/* Delete Confirmation */}
      <ConfirmDialog
        open={deleteTarget !== null}
        title={t("deleteProvider")}
        description={t("deleteProviderDesc", { name: deleteTarget?.name ?? "" })}
        confirmLabel={t("delete", { ns: "common" })}
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
    </div>
  );
}
