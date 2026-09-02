import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  GitBranch,
  Plus,
  Pencil,
  Trash2,
  X,
  ChevronRight,
  Zap,
  RotateCcw,
  Check,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Skeleton,
  Input,
  Button,
  Badge,
  Switch,
  EmptyState,
  ConfirmDialog,
  Dialog,
  Drawer,
  Select,
} from "../../components/ui";
import type {
  RouterProfile,
  RouterProfileInput,
  RouterProfileEntryInput,
  Model,
} from "../../lib/api/types";

// ─── Helpers ─────────────────────────────────────────────────────

function formatRelativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

// ─── Model Search Autocomplete ────────────────────────────────────

function ModelSearchInput({
  value,
  onChange,
  models,
  modelsLoading,
}: {
  value: string;
  onChange: (value: string) => void;
  models: Model[];
  modelsLoading: boolean;
}) {
  const [query, setQuery] = useState(value);
  const [isOpen, setIsOpen] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  // Sync external value changes (e.g. when dialog resets)
  useEffect(() => {
    setQuery(value);
  }, [value]);

  const filtered = useMemo(() => {
    if (query.length < 2) return [];
    const lower = query.toLowerCase();
    return models.filter(
      (m) =>
        m.id.toLowerCase().includes(lower) ||
        m.name.toLowerCase().includes(lower),
    );
  }, [query, models]);

  const showDropdown = isOpen && query.length >= 2;

  const handleSelect = useCallback(
    (modelId: string) => {
      setQuery(modelId);
      onChange(modelId);
      setIsOpen(false);
      setHighlightedIndex(-1);
      inputRef.current?.blur();
    },
    [onChange],
  );

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (!showDropdown) return;

      switch (e.key) {
        case "ArrowDown":
          e.preventDefault();
          setHighlightedIndex((prev) =>
            prev < filtered.length - 1 ? prev + 1 : 0,
          );
          break;
        case "ArrowUp":
          e.preventDefault();
          setHighlightedIndex((prev) =>
            prev > 0 ? prev - 1 : filtered.length - 1,
          );
          break;
        case "Enter":
          e.preventDefault();
          if (highlightedIndex >= 0 && highlightedIndex < filtered.length) {
            handleSelect(filtered[highlightedIndex].id);
          }
          break;
        case "Escape":
          e.preventDefault();
          setIsOpen(false);
          setHighlightedIndex(-1);
          break;
      }
    },
    [showDropdown, filtered, highlightedIndex, handleSelect],
  );

  // Scroll highlighted item into view
  useEffect(() => {
    if (highlightedIndex >= 0 && listRef.current) {
      const item = listRef.current.children[highlightedIndex] as HTMLElement;
      if (item) {
        item.scrollIntoView({ block: "nearest" });
      }
    }
  }, [highlightedIndex]);

  // Close dropdown on outside click
  useEffect(() => {
    if (!isOpen) return;
    const handleClickOutside = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
        setHighlightedIndex(-1);
      }
    };
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, [isOpen]);

  const modelSubtitle = (m: Model) => {
    const parts: string[] = [];
    if (m.family) parts.push(m.family);
    if (m.parameterSize) parts.push(m.parameterSize);
    if (m.quantization) parts.push(m.quantization);
    return parts.join(" · ");
  };

  return (
    <div ref={containerRef} className="relative flex-1 min-w-0">
      <input
        ref={inputRef}
        type="text"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setHighlightedIndex(-1);
          setIsOpen(true);
          // If user clears the input, propagate empty value
          if (e.target.value === "") {
            onChange("");
          }
        }}
        onFocus={() => {
          if (query.length >= 2) setIsOpen(true);
        }}
        onKeyDown={handleKeyDown}
        placeholder="e.g. cloud/openai/gpt-4o"
        className={`
          h-7 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)]
          px-3 w-full text-xs text-[var(--color-text)]
          border-[var(--color-border)]
          placeholder:text-[var(--color-text-muted)]
          focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)]
          transition-colors duration-[var(--duration-fast)]
        `}
      />
      {showDropdown && (
        <div
          ref={listRef}
          className="absolute left-0 right-0 top-full mt-1 z-50 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)] shadow-lg max-h-48 overflow-y-auto"
        >
          {modelsLoading ? (
            <div className="px-3 py-2 text-xs text-[var(--color-text-muted)]">
              Loading models…
            </div>
          ) : filtered.length > 0 ? (
            filtered.map((m, i) => (
              <button
                key={m.id}
                type="button"
                className={`w-full text-left px-3 py-1.5 text-xs cursor-pointer transition-colors duration-[var(--duration-fast)] ${
                  i === highlightedIndex
                    ? "bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
                    : "text-[var(--color-text)] hover:bg-[var(--color-bg-muted)]"
                }`}
                onMouseDown={(e) => {
                  e.preventDefault();
                  handleSelect(m.id);
                }}
                onMouseEnter={() => setHighlightedIndex(i)}
              >
                <span className="font-medium truncate block">{m.id}</span>
                {modelSubtitle(m) && (
                  <span className="text-[var(--color-text-muted)] truncate block">
                    {modelSubtitle(m)}
                  </span>
                )}
              </button>
            ))
          ) : (
            <div className="px-3 py-2 text-xs text-[var(--color-text-muted)]">
              No matching models
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ─── Profile Row ─────────────────────────────────────────────────

function ProfileRow({
  profile,
  onEdit,
  onDelete,
  onView,
}: {
  profile: RouterProfile;
  onEdit: (profile: RouterProfile) => void;
  onDelete: (profile: RouterProfile) => void;
  onView: (profile: RouterProfile) => void;
}) {
  return (
    <div
      className="group flex items-center gap-4 px-4 py-3 border-b border-[var(--color-border-subtle)] last:border-b-0 hover:bg-[var(--color-bg-muted)]/50 transition-colors duration-[var(--duration-fast)] cursor-pointer"
      onClick={() => onView(profile)}
    >
      {/* Name */}
      <div className="flex items-center gap-3 min-w-0 flex-1">
        <div className="flex items-center justify-center size-8 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] shrink-0">
          <GitBranch className="size-4" />
        </div>
        <span className="text-sm text-[var(--color-text)] truncate font-medium">
          {profile.name}
        </span>
      </div>

      {/* Mode */}
      <div className="shrink-0 w-[90px]">
        <Badge variant={profile.mode === "Auto" ? "info" : "warning"}>
          {profile.mode}
        </Badge>
      </div>

      {/* Entries count */}
      <div className="shrink-0 w-[100px] text-right">
        <span className="text-xs text-[var(--color-text-muted)]">
          {profile.entries.length} {profile.entries.length === 1 ? "model" : "models"}
        </span>
      </div>

      {/* Created */}
      <div className="shrink-0 w-[100px] text-right">
        <span className="text-xs text-[var(--color-text-muted)]">
          {formatRelativeTime(profile.createdAt)}
        </span>
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1 shrink-0" onClick={(e) => e.stopPropagation()}>
        <Button variant="ghost" size="sm" onClick={() => onEdit(profile)}>
          <Pencil className="size-3.5" />
          Edit
        </Button>
        <Button variant="danger" size="sm" onClick={() => onDelete(profile)}>
          <Trash2 className="size-3.5" />
        </Button>
        <ChevronRight className="size-4 text-[var(--color-text-muted)] opacity-0 group-hover:opacity-100 transition-opacity ml-1" />
      </div>
    </div>
  );
}

// ─── Entry Row (inside the dialog form) ──────────────────────────

function EntryRow({
  entry,
  index,
  onUpdate,
  onRemove,
  models,
  modelsLoading,
}: {
  entry: RouterProfileEntryInput;
  index: number;
  onUpdate: (index: number, patch: Partial<RouterProfileEntryInput>) => void;
  onRemove: (index: number) => void;
  models: Model[];
  modelsLoading: boolean;
}) {
  return (
    <div className="flex items-center gap-2 px-3 py-2 rounded-md bg-[var(--color-bg-muted)]/40 border border-[var(--color-border-subtle)]">
      <ModelSearchInput
        value={entry.modelId}
        onChange={(modelId) => onUpdate(index, { modelId })}
        models={models}
        modelsLoading={modelsLoading}
      />
      <div className="w-20 shrink-0">
        <Input
          type="number"
          value={entry.priority}
          onChange={(e) =>
            onUpdate(index, { priority: parseInt(e.target.value, 10) || 0 })
          }
          placeholder="Priority"
          className="h-7 text-xs"
        />
      </div>
      <div className="shrink-0">
        <Switch
          checked={entry.isEnabled}
          onCheckedChange={(checked) => onUpdate(index, { isEnabled: checked })}
        />
      </div>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        onClick={() => onRemove(index)}
        className="shrink-0 text-[var(--color-status-error)] hover:text-[var(--color-status-error)]"
      >
        <X className="size-3.5" />
      </Button>
    </div>
  );
}

// ─── Profile Detail Panel (Drawer) ──────────────────────────────

function ProfileDetailPanel({
  profile,
  open,
  onClose,
}: {
  profile: RouterProfile | null;
  open: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  const setActiveMutation = useMutation({
    mutationFn: ({ activeModelId }: { activeModelId: string | null }) =>
      client.setActiveEntry(profile!.id, activeModelId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["router-profiles"] });
    },
  });

  // Merge server data with latest cache for up-to-date activeModelId
  const profiles = queryClient.getQueryData<RouterProfile[]>(["router-profiles"]);
  const liveProfile = profiles?.find((p) => p.id === profile?.id) ?? profile;

  const activeModelId = liveProfile?.activeModelId ?? null;

  // Compute the effective active model: manual override or first enabled by priority
  const effectiveActiveModelId = useMemo(() => {
    if (activeModelId) return activeModelId;
    if (!liveProfile) return null;
    const enabled = liveProfile.entries
      .filter((e) => e.isEnabled)
      .sort((a, b) => a.priority - b.priority);
    return enabled[0]?.modelId ?? null;
  }, [activeModelId, liveProfile]);

  // Sort entries by priority descending for display
  const sortedEntries = useMemo(() => {
    if (!liveProfile) return [];
    return [...liveProfile.entries].sort((a, b) => b.priority - a.priority);
  }, [liveProfile]);

  if (!liveProfile) return null;

  return (
    <Drawer
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={liveProfile.name}
      subtitle={
        <div className="flex items-center gap-2">
          <Badge variant={liveProfile.mode === "Auto" ? "info" : "warning"}>
            {liveProfile.mode}
          </Badge>
          <span>{liveProfile.entries.length} {liveProfile.entries.length === 1 ? "model" : "models"}</span>
        </div>
      }
    >
      <div className="p-5 space-y-5">
        {/* Active entry status */}
        <div className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30 px-4 py-3">
          <div className="flex items-center gap-2 mb-1.5">
            <Zap className="size-3.5 text-[var(--color-primary)]" />
            <span className="text-xs font-medium text-[var(--color-text-heading)]">
              Active Model
            </span>
          </div>
          {effectiveActiveModelId ? (
            <div className="flex items-center gap-2">
              <span className="size-2 rounded-full bg-[var(--color-status-success)] shrink-0" />
              <span className="text-sm font-medium text-[var(--color-text)]">
                {effectiveActiveModelId}
              </span>
              {activeModelId !== null ? (
                <Badge variant="success" className="ml-auto">Manual</Badge>
              ) : (
                <Badge variant="info" className="ml-auto">Auto</Badge>
              )}
            </div>
          ) : (
            <div className="flex items-center gap-2">
              <span className="size-2 rounded-full bg-[var(--color-text-muted)] shrink-0" />
              <span className="text-sm text-[var(--color-text-muted)]">
                No enabled models
              </span>
            </div>
          )}
        </div>

        {/* Entries list */}
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Models
            </span>
            <span className="text-[10px] text-[var(--color-text-muted)]">
                      Sorted by priority (lowest first)
            </span>
          </div>

          <div className="space-y-1.5">
            {sortedEntries.map((entry) => {
              const isActive = effectiveActiveModelId === entry.modelId;
              return (
                <div
                  key={entry.modelId}
                  className={`flex items-center gap-3 px-3 py-2.5 rounded-[var(--radius-md)] border transition-colors duration-[var(--duration-fast)] ${
                    isActive
                      ? "border-[var(--color-status-success)]/30 bg-[var(--color-status-success)]/5"
                      : "border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)]"
                  }`}
                >
                  {/* Active indicator */}
                  <div className="shrink-0 w-6 flex justify-center">
                    {isActive ? (
                      <span className="size-2 rounded-full bg-[var(--color-status-success)]" />
                    ) : (
                      <span className="size-2 rounded-full border border-[var(--color-border)]" />
                    )}
                  </div>

                  {/* Model + priority */}
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className={`text-sm font-medium truncate ${isActive ? "text-[var(--color-status-success)]" : "text-[var(--color-text)]"}`}>
                        {entry.modelId}
                      </span>
                      {!entry.isEnabled && (
                        <Badge variant="outline" className="shrink-0">Disabled</Badge>
                      )}
                    </div>
                    <span className="text-[10px] text-[var(--color-text-muted)]">
                      Priority {entry.priority}
                    </span>
                  </div>

                  {/* Actions */}
                  {!isActive ? (
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={setActiveMutation.isPending || !entry.isEnabled}
                      onClick={() =>
                        setActiveMutation.mutate({ activeModelId: entry.modelId })
                      }
                      className="shrink-0 text-[var(--color-primary)]"
                    >
                      Set Active
                    </Button>
                  ) : (
                    <span className="flex items-center gap-1 text-xs text-[var(--color-status-success)] shrink-0 px-2">
                      <Check className="size-3" />
                      Active
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Footer */}
      <div className="px-5 py-3">
        <Button
          variant="secondary"
          size="sm"
          disabled={activeModelId === null || setActiveMutation.isPending}
          onClick={() => setActiveMutation.mutate({ activeModelId: null })}
          className="w-full"
        >
          <RotateCcw className="size-3.5" />
          Reset to Default
        </Button>
      </div>
    </Drawer>
  );
}

// ─── Add / Edit Dialog ──────────────────────────────────────────

function ProfileDialog({
  open,
  onClose,
  editProfile,
}: {
  open: boolean;
  onClose: () => void;
  editProfile: RouterProfile | null;
}) {
  const queryClient = useQueryClient();
  const isEdit = editProfile !== null;

  const [name, setName] = useState("");
  const [mode, setMode] = useState<"Auto" | "Manual">("Auto");
  const [entries, setEntries] = useState<RouterProfileEntryInput[]>([]);
  const [error, setError] = useState<string | null>(null);

  // Fetch models once when dialog opens
  const { data: models = [], isLoading: modelsLoading } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
    enabled: open,
    staleTime: 60_000,
  });

  // Reset state on open / editProfile change
  useEffect(() => {
    if (open) {
      if (editProfile) {
        setName(editProfile.name);
        setMode(editProfile.mode);
        setEntries(
          editProfile.entries.map((e) => ({ ...e })),
        );
      } else {
        setName("");
        setMode("Auto");
        setEntries([{ modelId: "", priority: 0, isEnabled: true }]);
      }
      setError(null);
    }
  }, [open, editProfile]);

  const createMutation = useMutation({
    mutationFn: (data: RouterProfileInput) => client.createRouterProfile(data),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: RouterProfileInput }) =>
      client.updateRouterProfile(id, data),
  });

  const handleUpdateEntry = (index: number, patch: Partial<RouterProfileEntryInput>) => {
    setEntries((prev) =>
      prev.map((e, i) => (i === index ? { ...e, ...patch } : e)),
    );
  };

  const handleRemoveEntry = (index: number) => {
    setEntries((prev) => prev.filter((_, i) => i !== index));
  };

  const handleAddEntry = () => {
    const nextPriority = entries.length > 0
      ? Math.max(...entries.map((e) => e.priority)) + 1
      : 0;
    setEntries((prev) => [
      ...prev,
      { modelId: "", priority: nextPriority, isEnabled: true },
    ]);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!name.trim()) {
      setError("Profile name is required.");
      return;
    }

    if (entries.length === 0) {
      setError("Add at least one model entry.");
      return;
    }

    for (let i = 0; i < entries.length; i++) {
      if (!entries[i].modelId.trim()) {
        setError(`Model ID is required for entry ${i + 1}.`);
        return;
      }
    }

    const payload: RouterProfileInput = {
      name: name.trim(),
      mode,
      entries: entries.map((e) => ({
        modelId: e.modelId.trim(),
        priority: e.priority,
        isEnabled: e.isEnabled,
      })),
    };

    try {
      if (isEdit && editProfile) {
        await updateMutation.mutateAsync({ id: editProfile.id, data: payload });
      } else {
        await createMutation.mutateAsync(payload);
      }

      queryClient.invalidateQueries({ queryKey: ["router-profiles"] });
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    }
  };

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={isEdit ? "Edit Profile" : "Add Profile"}
    >
      <form onSubmit={handleSubmit} className="p-5 space-y-4">
        <Input
          label="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="e.g. GPT-4o Fallback Chain"
          autoFocus={!isEdit}
        />

        <Select
          label="Mode"
          value={mode}
          onChange={(e) => setMode(e.target.value as "Auto" | "Manual")}
          options={[
            { value: "Auto", label: "Auto" },
            { value: "Manual", label: "Manual" },
          ]}
        />

        {/* Entries section */}
        <div className="space-y-2">
          <p className="text-xs font-medium text-[var(--color-text-muted)]">
            Model Entries <span className="font-normal">(lower priority number = tried first)</span>
          </p>
          <div className="space-y-2 rounded-md border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/20 p-3">
            {entries.length > 0 && (
              <div className="flex items-center gap-2 px-1 pb-1">
                <span className="flex-1 text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
                  Model ID
                </span>
                <span className="w-20 text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider" title="Lower number = tried first">
                  Priority
                </span>
                <span className="w-9 text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider text-center">
                  On
                </span>
                <span className="w-8" />
              </div>
            )}

            {entries.map((entry, i) => (
              <EntryRow
                key={i}
                entry={entry}
                index={i}
                onUpdate={handleUpdateEntry}
                onRemove={handleRemoveEntry}
                models={models}
                modelsLoading={modelsLoading}
              />
            ))}

            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleAddEntry}
              className="w-full mt-1"
            >
              <Plus className="size-3.5" />
              Add Entry
            </Button>
          </div>
        </div>

        {error && (
          <p className="text-sm text-[var(--color-status-error)]">{error}</p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={onClose} disabled={isPending}>
            Cancel
          </Button>
          <Button type="submit" variant="primary" size="sm" loading={isPending}>
            {isEdit ? "Save Changes" : "Add Profile"}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

// ─── Main Router Profiles Page ───────────────────────────────────

export default function RouterProfiles() {
  const queryClient = useQueryClient();

  const {
    data: profiles,
    isLoading,
  } = useQuery({
    queryKey: ["router-profiles"],
    queryFn: () => client.listRouterProfiles(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteRouterProfile(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["router-profiles"] }),
  });

  const [dialogOpen, setDialogOpen] = useState(false);
  const [editTarget, setEditTarget] = useState<RouterProfile | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<RouterProfile | null>(null);
  const [detailProfile, setDetailProfile] = useState<RouterProfile | null>(null);

  const handleEdit = (profile: RouterProfile) => {
    setEditTarget(profile);
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
          Router Profiles
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Chain models with automatic fallback when errors occur.
        </p>
      </div>

      {/* Profiles card */}
      <Card padding="none">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--color-border-subtle)]">
          <div className="flex items-center gap-2">
            <GitBranch className="size-4 text-[var(--color-text-muted)]" />
            <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Profiles
            </p>
          </div>
          <Button variant="primary" size="sm" onClick={handleAdd}>
            <Plus className="size-3.5" />
            Add Profile
          </Button>
        </div>

        {/* Content */}
        {isLoading ? (
          <div className="p-4 space-y-3">
            {Array.from({ length: 2 }, (_, i) => (
              <Skeleton key={i} className="h-14 w-full" />
            ))}
          </div>
        ) : profiles && profiles.length > 0 ? (
          <div>
            {/* Column headers */}
            <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--color-border)] bg-[var(--color-bg-muted)]/30">
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider min-w-0 flex-1">
                Profile
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[90px]">
                Mode
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[100px] text-right">
                Models
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[100px] text-right">
                Created
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[120px] text-right">
                Actions
              </span>
            </div>

            {profiles.map((p) => (
              <ProfileRow
                key={p.id}
                profile={p}
                onEdit={handleEdit}
                onDelete={setDeleteTarget}
                onView={setDetailProfile}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon={<GitBranch className="size-12" strokeWidth={1.5} />}
            title="No router profiles yet"
            description="Create a profile to chain multiple models together."
            action={
              <Button variant="primary" size="sm" onClick={handleAdd}>
                <Plus className="size-3.5" />
                Add Profile
              </Button>
            }
          />
        )}
      </Card>

      {/* Add / Edit Dialog */}
      <ProfileDialog
        open={dialogOpen}
        onClose={handleCloseDialog}
        editProfile={editTarget}
      />

      {/* Delete Confirmation */}
      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete profile"
        description={`Delete profile "${deleteTarget?.name ?? ""}"? This cannot be undone.`}
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

      {/* Profile Detail Drawer */}
      <ProfileDetailPanel
        profile={detailProfile}
        open={detailProfile !== null}
        onClose={() => setDetailProfile(null)}
      />
    </div>
  );
}
