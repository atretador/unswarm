import {
  useCallback,
  useMemo,
  useRef,
  useState,
} from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  AlertTriangle,
  FileCode,
  Pencil,
  Terminal,
  Trash2,
  Upload,
  X,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { Dialog } from "../../components/ui/Dialog";
import {
  Button,
  Badge,
  EmptyState,
  Input,
  Skeleton,
  ConfirmDialog,
} from "../../components/ui";
import type { ScriptInfo, RegisteredRuntime } from "../../lib/api/types";
import { ApiError } from "../../lib/api/httpClient";

// ─── Constants ────────────────────────────────────────────────────

const MAX_FILE_SIZE = 1_048_576; // 1 MB

// ─── Helpers ──────────────────────────────────────────────────────

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const units = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${units[i]}`;
}

function relativeTime(iso: string | null): string {
  if (!iso) return "—";
  const diff = Date.now() - new Date(iso).getTime();
  const sec = Math.floor(diff / 1000);
  if (sec < 60) return "just now";
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const d = Math.floor(hr / 24);
  return `${d}d ago`;
}

/** Derive a display name from a script file name. */
function displayNameFromScript(name: string): string {
  return name
    .replace(/\.sh$/i, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 32) || "script";
}

// ─── ScriptDropZone ───────────────────────────────────────────────

export function ScriptDropZone({
  onFilesSelected,
  disabled,
}: {
  onFilesSelected: (files: File[]) => void;
  disabled?: boolean;
}) {
  const [isDragOver, setIsDragOver] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);

  const validate = useCallback(
    (files: FileList | File[]) => {
      const valid: File[] = [];
      const rejected: string[] = [];
      for (const file of Array.from(files)) {
        if (!file.name.endsWith(".sh")) {
          rejected.push(`${file.name}: only .sh files are allowed`);
        } else if (file.size > MAX_FILE_SIZE) {
          rejected.push(`${file.name}: exceeds 1 MB limit`);
        } else {
          valid.push(file);
        }
      }
      return { valid, rejected };
    },
    [],
  );

  const handleFiles = useCallback(
    (files: FileList | File[]) => {
      const { valid, rejected } = validate(files);
      setErrors(rejected);
      if (valid.length > 0) {
        onFilesSelected(valid);
      }
    },
    [validate, onFilesSelected],
  );

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragOver(false);
      if (disabled) return;
      if (e.dataTransfer.files.length > 0) {
        handleFiles(e.dataTransfer.files);
      }
    },
    [disabled, handleFiles],
  );

  const handleDragOver = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (!disabled) setIsDragOver(true);
    },
    [disabled],
  );

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragOver(false);
  }, []);

  return (
    <div className="space-y-2">
      <button
        type="button"
        disabled={disabled}
        onClick={() => inputRef.current?.click()}
        onDrop={handleDrop}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        className={`
          flex w-full flex-col items-center gap-2 rounded-[var(--radius-xl)] border-2 border-dashed
          px-4 py-6 text-center transition-all duration-150 cursor-pointer
          focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
          disabled:opacity-50 disabled:cursor-not-allowed
          ${
            isDragOver
              ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)]"
              : "border-[var(--color-border)] bg-[var(--color-bg-muted)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-elevated)]"
          }
        `}
      >
        <div
          className={`
            flex size-9 items-center justify-center rounded-[var(--radius-lg)] transition-colors
            ${isDragOver ? "bg-[var(--color-primary)] text-[var(--color-text-inverse)]" : "bg-[var(--color-bg-surface)] text-[var(--color-text-muted)]"}
          `}
        >
          <Upload className="size-4" />
        </div>
        <div>
          <p className="text-xs font-medium text-[var(--color-text-heading)]">
            {isDragOver ? "Drop to upload" : "Drop .sh files here or click to browse"}
          </p>
          <p className="mt-0.5 text-[10px] text-[var(--color-text-muted)]">
            Multiple files supported · Max 1 MB each
          </p>
        </div>
      </button>

      <input
        ref={inputRef}
        type="file"
        accept=".sh"
        multiple
        className="hidden"
        disabled={disabled}
        onChange={(e) => {
          if (e.target.files && e.target.files.length > 0) {
            handleFiles(e.target.files);
            e.target.value = "";
          }
        }}
      />

      <AnimatePresence>
        {errors.length > 0 && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: "auto" }}
            exit={{ opacity: 0, height: 0 }}
            className="overflow-hidden"
          >
            <div className="space-y-1 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2.5 py-2">
              {errors.map((err) => (
                <div key={err} className="flex items-start gap-1.5 text-[10px] text-[var(--color-status-error)]">
                  <AlertTriangle className="mt-0.5 size-3 shrink-0" />
                  <span>{err}</span>
                </div>
              ))}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

// ─── ScriptCardGrid ───────────────────────────────────────────────

function ScriptCard({
  script,
  isSelected,
  isRegistered,
  onSelect,
  onEdit,
  onDelete,
}: {
  script: ScriptInfo;
  isSelected: boolean;
  isRegistered: boolean;
  onSelect: () => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <div
      className={`
        group relative flex flex-col gap-1.5 overflow-hidden rounded-[var(--radius-xl)] border p-3
        transition-all duration-[var(--duration-fast)]
        ${
          isSelected
            ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)]"
            : isRegistered
              ? "border-[var(--color-border)] bg-[var(--color-bg-muted)] opacity-60"
              : "border-[var(--color-border)] bg-[var(--color-bg-surface)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-elevated)]"
        }
      `}
    >
      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 items-center gap-1.5">
          <FileCode className="size-3.5 shrink-0 text-[var(--color-text-muted)]" />
          <span
            className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]"
            title={script.name}
          >
            {script.name}
          </span>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {isRegistered && (
            <Badge variant="success" className="gap-1 text-[10px]">
              <Terminal className="size-2.5" />
              registered
            </Badge>
          )}
          {!isRegistered && isSelected && (
            <Badge variant="info" className="text-[10px]">selected</Badge>
          )}
        </div>
      </div>

      <div className="flex items-center gap-3 text-[10px] text-[var(--color-text-muted)]">
        <span>{formatBytes(script.sizeBytes)}</span>
        <span>{relativeTime(script.lastModified)}</span>
      </div>

      {/* Actions row — visible on hover or always for registered */}
      <div className="flex items-center gap-1 pt-0.5">
        {!isRegistered && (
          <Button
            variant={isSelected ? "primary" : "secondary"}
            size="sm"
            className="flex-1"
            onClick={(e) => {
              e.stopPropagation();
              onSelect();
            }}
          >
            {isSelected ? "Selected" : "Select"}
          </Button>
        )}
        <Button
          variant="ghost"
          size="sm"
          onClick={(e) => {
            e.stopPropagation();
            onEdit();
          }}
          title="Edit script content"
          className={isRegistered ? "" : "opacity-0 transition-opacity group-hover:opacity-100"}
        >
          <Pencil className="size-3" />
          Edit
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={(e) => {
            e.stopPropagation();
            onDelete();
          }}
          title="Delete script"
          className="text-[var(--color-status-error)] opacity-0 transition-opacity group-hover:opacity-100 hover:bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)]"
        >
          <Trash2 className="size-3" />
        </Button>
      </div>
    </div>
  );
}

export function ScriptCardGrid({
  scripts,
  isLoading,
  error,
  selectedName,
  registeredNames,
  agentName,
  onSelect,
  onEdit,
  onDelete,
  onRetry,
}: {
  scripts: ScriptInfo[] | undefined;
  isLoading: boolean;
  error: Error | null;
  selectedName: string | null;
  registeredNames: Set<string>;
  agentName: string;
  onSelect: (name: string) => void;
  onEdit: (name: string) => void;
  onDelete: (name: string) => void;
  onRetry: () => void;
}) {
  if (isLoading) {
    return (
      <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
        {Array.from({ length: 3 }, (_, i) => (
          <Skeleton key={i} className="h-24 w-full" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <EmptyState
        title="Couldn't list scripts"
        description={`Couldn't reach ${agentName} to list scripts.`}
        action={
          <Button variant="secondary" size="sm" onClick={onRetry}>
            Retry
          </Button>
        }
      />
    );
  }

  if (!scripts || scripts.length === 0) {
    return (
      <EmptyState
        icon={<Terminal className="size-12" strokeWidth={1.5} />}
        title="No scripts uploaded"
        description="Upload .sh files using the drop zone above, or add scripts to the agent's scripts_dir."
      />
    );
  }

  return (
    <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
      {scripts.map((s) => (
        <ScriptCard
          key={s.name}
          script={s}
          isSelected={selectedName === s.name}
          isRegistered={registeredNames.has(s.name)}
          onSelect={() => onSelect(s.name)}
          onEdit={() => onEdit(s.name)}
          onDelete={() => onDelete(s.name)}
        />
      ))}
    </div>
  );
}

// ─── ScriptEditorDialog ───────────────────────────────────────────

export function ScriptEditorDialog({
  scriptName,
  isHost,
  agentName,
  isRunning,
  open,
  onClose,
  onSaved,
}: {
  scriptName: string | null;
  isHost: boolean;
  agentName: string;
  isRunning: boolean;
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const queryClient = useQueryClient();
  const [content, setContent] = useState("");
  const [isDirty, setIsDirty] = useState(false);
  const [showDiscardConfirm, setShowDiscardConfirm] = useState(false);

  // Load script content when dialog opens
  const { isLoading: contentLoading } = useQuery({
    queryKey: ["script-content", agentName, scriptName],
    queryFn: async () => {
      if (!scriptName) return "";
      const text = isHost
        ? await client.getScriptContent(scriptName)
        : await client.getAgentScriptContent(agentName, scriptName);
      setContent(text);
      setIsDirty(false);
      return text;
    },
    enabled: open && !!scriptName,
  });

  // Reset state when dialog opens with new script
  const prevNameRef = useRef<string | null>(null);
  if (open && scriptName !== prevNameRef.current) {
    prevNameRef.current = scriptName;
    if (!contentLoading) {
      // Content will be loaded by the query
    }
  } else if (!open) {
    prevNameRef.current = null;
  }

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!scriptName) return;
      const blob = new Blob([content], { type: "text/x-shellscript" });
      const file = new File([blob], scriptName, { type: "text/x-shellscript" });
      if (isHost) {
        await client.updateHostScript(scriptName, file);
      } else {
        await client.updateAgentScript(agentName, scriptName, file);
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["host-scripts"] });
      queryClient.invalidateQueries({ queryKey: ["agent-scripts", agentName] });
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      onSaved();
      onClose();
    },
  });

  const handleClose = () => {
    if (isDirty) {
      setShowDiscardConfirm(true);
      return;
    }
    onClose();
  };

  return (
    <>
    <Dialog open={open} onOpenChange={(o) => { if (!o) handleClose(); }} title={`Edit ${scriptName ?? "script"}`}>
      <div className="flex flex-col gap-4 p-5">
        {isRunning && (
          <div className="flex items-start gap-2 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-warning)_10%,transparent)] px-3 py-2 text-xs text-[var(--color-status-warning)]">
            <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
            <span>
              This script is currently running. Changes will take effect on next start.
            </span>
          </div>
        )}

        {contentLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-4 w-32" />
            <Skeleton className="h-64 w-full" />
          </div>
        ) : (
          <div className="space-y-1.5">
            <label className="text-xs font-medium text-[var(--color-text-heading)]">
              Script content
            </label>
            <textarea
              value={content}
              onChange={(e) => {
                setContent(e.target.value);
                setIsDirty(true);
              }}
              spellCheck={false}
              className="
                h-72 w-full resize-y rounded-[var(--radius-lg)] border border-[var(--color-border)]
                bg-[var(--color-bg-muted)] p-3 font-mono text-xs leading-relaxed text-[var(--color-text)]
                placeholder:text-[var(--color-text-muted)]
                outline-none transition-colors
                focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]
              "
              placeholder="#!/bin/bash"
            />
          </div>
        )}

        {saveMutation.isError && (
          <div className="flex items-center gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1 text-[10px] text-[var(--color-status-error)]">
            <AlertTriangle className="size-3 shrink-0" />
            <span className="truncate">{saveMutation.error.message}</span>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="ghost" size="sm" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            size="sm"
            loading={saveMutation.isPending}
            disabled={!isDirty || contentLoading}
            onClick={() => saveMutation.mutate()}
          >
            Save
          </Button>
        </div>
      </div>
    </Dialog>

    <ConfirmDialog
      open={showDiscardConfirm}
      title="Discard changes?"
      description="You have unsaved changes. Are you sure you want to close without saving?"
      confirmLabel="Discard"
      variant="danger"
      onConfirm={() => {
        setShowDiscardConfirm(false);
        onClose();
      }}
      onCancel={() => setShowDiscardConfirm(false)}
    />
    </>
  );
}

// ─── HostScriptUpload (orchestrator for host) ─────────────────────

export function HostScriptUpload({
  registered,
  onClose,
}: {
  registered: RegisteredRuntime[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const agentName = "host";

  // State
  const [selectedScript, setSelectedScript] = useState<string | null>(null);
  const [editingScript, setEditingScript] = useState<string | null>(null);
  const [deletingScript, setDeletingScript] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [port, setPort] = useState("8080");

  // Fetch host scripts
  const { data: scripts, isLoading, error, refetch } = useQuery({
    queryKey: ["host-scripts"],
    queryFn: () => client.listHostScripts(),
    staleTime: 15_000,
  });

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: (file: File) => client.uploadHostScript(file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["host-scripts"] });
    },
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (name: string) => client.deleteHostScript(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["host-scripts"] });
      setDeletingScript(null);
    },
  });

  // Register mutation
  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; launcherPath: string; port: number }) =>
      client.registerRuntime({
        displayName: payload.displayName,
        image: payload.displayName,
        containerPort: payload.port,
        agent: agentName,
        runtimeKind: "script",
        launcherPath: payload.launcherPath,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      onClose();
    },
  });

  // Compute registered script names (by basename from launcherPath)
  const registeredNames = useMemo(() => {
    const names = new Set<string>();
    for (const rc of registered) {
      if (rc.runtimeKind === "script" && rc.launcherPath) {
        // Extract basename from path
        const parts = rc.launcherPath.split("/");
        const basename = parts[parts.length - 1];
        if (basename) names.add(basename);
      }
    }
    return names;
  }, [registered]);

  // Handlers
  const handleFilesSelected = useCallback(
    async (files: File[]) => {
      for (const file of files) {
        await uploadMutation.mutateAsync(file);
      }
    },
    [uploadMutation],
  );

  const handleSelectScript = useCallback(
    (name: string) => {
      if (selectedScript === name) {
        setSelectedScript(null);
        setDisplayName("");
      } else {
        setSelectedScript(name);
        setDisplayName(displayNameFromScript(name));
      }
    },
    [selectedScript],
  );

  const handleRegister = useCallback(() => {
    if (!selectedScript || !displayName.trim()) return;
    // selectedScript is a basename — resolve full path from the scripts list
    const script = scripts?.find((s) => s.name === selectedScript);
    const launcherPath = script?.path ?? selectedScript;
    registerMutation.mutate({
      displayName: displayName.trim(),
      launcherPath,
      port: parseInt(port, 10) || 8080,
    });
  }, [selectedScript, displayName, port, registerMutation, scripts]);

  // Check if a script is currently running
  const isScriptRunning = useCallback(
    (name: string) => {
      for (const rc of registered) {
        if (rc.runtimeKind === "script" && rc.launcherPath) {
          const parts = rc.launcherPath.split("/");
          const basename = parts[parts.length - 1];
          if (basename === name && (rc.status === "healthy" || rc.status === "ready" || rc.status === "starting")) {
            return true;
          }
        }
      }
      return false;
    },
    [registered],
  );

  // Detect Docker mode (backend returns 400)
  const isDockerMode = error instanceof ApiError && error.status === 400;

  return (
    <div className="space-y-4 p-5">
      {isDockerMode ? (
        <div className="flex flex-col items-center gap-3 rounded-[var(--radius-xl)] border border-[var(--color-border)] bg-[var(--color-bg-muted)] py-8 text-center">
          <Terminal className="size-8 text-[var(--color-text-muted)]" strokeWidth={1.5} />
          <div className="space-y-1">
            <p className="text-sm font-medium text-[var(--color-text-heading)]">
              Host scripts are not available in Docker
            </p>
            <p className="text-xs text-[var(--color-text-muted)]">
              Host script management requires bare-metal access. Run with{" "}
              <code className="rounded bg-[var(--color-bg-elevated)] px-1 py-0.5 font-mono text-[var(--color-text-heading)]">
                dotnet run
              </code>{" "}
              or use an agent with a configured scripts directory.
            </p>
          </div>
        </div>
      ) : (
        <>
          <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
            Upload and manage launcher scripts on{" "}
            <span className="font-mono text-[var(--color-text-heading)]">host</span>.
            Upload .sh files, then select one to register as a runtime.
          </p>

          <ScriptDropZone
            onFilesSelected={handleFilesSelected}
            disabled={uploadMutation.isPending}
          />

          {uploadMutation.isPending && (
            <div className="flex items-center gap-2 text-xs text-[var(--color-text-muted)]">
              <div className="animate-spin size-3.5 border-2 border-current border-t-transparent rounded-full" />
              Uploading...
            </div>
          )}

          <ScriptCardGrid
            scripts={scripts?.filter((s) => !registeredNames.has(s.name))}
            isLoading={isLoading}
            error={error}
            selectedName={selectedScript}
            registeredNames={registeredNames}
            agentName={agentName}
            onSelect={handleSelectScript}
            onEdit={setEditingScript}
            onDelete={setDeletingScript}
            onRetry={() => refetch()}
          />

      {/* Inline registration panel */}
      <AnimatePresence>
        {selectedScript && (
          <motion.div
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 8 }}
            transition={{ duration: 0.18 }}
            className="space-y-3 rounded-[var(--radius-xl)] border border-[var(--color-primary)] bg-[var(--color-bg-muted)] p-3.5"
          >
            <div className="flex items-center justify-between gap-2">
              <p className="text-xs font-medium text-[var(--color-text-heading)]">
                Register script
              </p>
              <button
                type="button"
                onClick={() => setSelectedScript(null)}
                aria-label="Cancel selection"
                className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:bg-[var(--color-bg-elevated)]"
              >
                <X className="size-3.5" />
              </button>
            </div>
            <p className="truncate font-mono text-[10px] text-[var(--color-text-muted)]">
              {selectedScript}
            </p>
            <div className="grid gap-2 sm:grid-cols-[1fr_auto] sm:items-end">
              <Input
                label="Display name"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                placeholder="my-script-server"
              />
              <Input
                label="Port"
                type="number"
                value={port}
                onChange={(e) => setPort(e.target.value)}
                placeholder="8080"
              />
            </div>
            <div className="flex justify-end gap-2 pt-1">
              <Button variant="ghost" size="sm" onClick={() => setSelectedScript(null)}>
                Cancel
              </Button>
              <Button
                size="sm"
                loading={registerMutation.isPending}
                disabled={!displayName.trim()}
                onClick={handleRegister}
              >
                <Terminal className="size-3" />
                Register script
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {registerMutation.isError && (
        <p className="text-xs text-[var(--color-status-error)]">
          {registerMutation.error.message}
        </p>
      )}

      {/* Delete confirmation */}
      <ConfirmDialog
        open={deletingScript !== null}
        title="Delete script"
        description={`Are you sure you want to delete "${deletingScript}"? This cannot be undone.`}
        confirmLabel="Delete"
        variant="danger"
        loading={deleteMutation.isPending}
        onConfirm={() => {
          if (deletingScript) deleteMutation.mutate(deletingScript);
        }}
        onCancel={() => setDeletingScript(null)}
      />

      {/* Editor dialog */}
      <ScriptEditorDialog
        scriptName={editingScript}
        isHost={true}
        agentName={agentName}
        isRunning={editingScript !== null && isScriptRunning(editingScript)}
        open={editingScript !== null}
        onClose={() => setEditingScript(null)}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ["host-scripts"] });
        }}
      />
        </>
      )}
    </div>
  );
}

// ─── AgentScriptUpload (orchestrator for remote agents) ────────────

export function AgentScriptUpload({
  agentName,
  registered,
  onClose,
}: {
  agentName: string;
  registered: RegisteredRuntime[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  // State
  const [selectedScript, setSelectedScript] = useState<string | null>(null);
  const [editingScript, setEditingScript] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [port, setPort] = useState("8080");

  // Fetch available scripts (existing flow)
  const { data: availableScripts, isLoading, error } = useQuery({
    queryKey: ["agent-available-scripts", agentName],
    queryFn: () => client.listAvailableScripts(agentName),
    staleTime: 15_000,
  });

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: (file: File) => client.uploadAgentScript(agentName, file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["agent-available-scripts", agentName] });
    },
  });

  // Register mutation
  const registerMutation = useMutation({
    mutationFn: (payload: { displayName: string; launcherPath: string; port: number }) =>
      client.registerRuntime({
        displayName: payload.displayName,
        image: payload.displayName,
        containerPort: payload.port,
        agent: agentName,
        runtimeKind: "script",
        launcherPath: payload.launcherPath,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["registered-containers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      onClose();
    },
  });

  // Handlers
  const handleFilesSelected = useCallback(
    async (files: File[]) => {
      for (const file of files) {
        await uploadMutation.mutateAsync(file);
      }
    },
    [uploadMutation],
  );

  const handleSelectScript = useCallback(
    (path: string) => {
      if (selectedScript === path) {
        setSelectedScript(null);
        setDisplayName("");
      } else {
        setSelectedScript(path);
        // Derive display name from path
        const basename = path.split("/").pop() ?? path;
        setDisplayName(displayNameFromScript(basename));
      }
    },
    [selectedScript],
  );

  const handleRegister = useCallback(() => {
    if (!selectedScript || !displayName.trim()) return;
    registerMutation.mutate({
      displayName: displayName.trim(),
      launcherPath: selectedScript,
      port: parseInt(port, 10) || 8080,
    });
  }, [selectedScript, displayName, port, registerMutation]);

  const isScriptRegistered = useCallback(
    (path: string) =>
      registered.some(
        (rc) =>
          rc.runtimeKind === "script" &&
          rc.launcherPath?.toLowerCase() === path.toLowerCase(),
      ),
    [registered],
  );

  // Check if a script is currently running
  const isScriptRunning = useCallback(
    (path: string) => {
      for (const rc of registered) {
        if (
          rc.runtimeKind === "script" &&
          rc.launcherPath?.toLowerCase() === path.toLowerCase() &&
          (rc.status === "healthy" || rc.status === "ready" || rc.status === "starting")
        ) {
          return true;
        }
      }
      return false;
    },
    [registered],
  );

  return (
    <div className="space-y-4 p-5">
      <p className="text-xs leading-relaxed text-[var(--color-text-muted)]">
        Upload and manage launcher scripts on{" "}
        <span className="font-mono text-[var(--color-text-heading)]">{agentName}</span>.
        Upload .sh files, then select one to register as a runtime.
      </p>

      <ScriptDropZone
        onFilesSelected={handleFilesSelected}
        disabled={uploadMutation.isPending}
      />

      {uploadMutation.isPending && (
        <div className="flex items-center gap-2 text-xs text-[var(--color-text-muted)]">
          <div className="animate-spin size-3.5 border-2 border-current border-t-transparent rounded-full" />
          Uploading...
        </div>
      )}

      {isLoading ? (
        <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))}
        </div>
      ) : error ? (
        <EmptyState
          title="Couldn't list scripts"
          description={`Couldn't reach ${agentName} to list scripts.`}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() =>
                queryClient.invalidateQueries({ queryKey: ["agent-available-scripts", agentName] })
              }
            >
              Retry
            </Button>
          }
        />
      ) : (availableScripts ?? []).length === 0 ? (
        <EmptyState
          icon={<Terminal className="size-12" strokeWidth={1.5} />}
          title="No scripts found"
          description={`No scripts found on ${agentName}. Add .sh files to the agent's scripts_dir.`}
        />
      ) : (
        <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-3">
          {(availableScripts ?? []).map((s) => {
            const already = isScriptRegistered(s.path);
            const selected = selectedScript === s.path;
            return (
              <button
                key={s.path}
                type="button"
                onClick={() => !already && handleSelectScript(s.path)}
                disabled={already}
                aria-pressed={selected}
                className={`
                  group relative flex flex-col gap-2 overflow-hidden rounded-[var(--radius-xl)] border p-3 text-left
                  transition-all duration-[var(--duration-fast)]
                  focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]
                  ${
                    selected
                      ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)] cursor-pointer"
                      : already
                        ? "cursor-not-allowed border-[var(--color-border)] bg-[var(--color-bg-muted)] opacity-55"
                        : "cursor-pointer border-[var(--color-border)] bg-[var(--color-bg-surface)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-elevated)]"
                  }
                `}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate font-mono text-xs text-[var(--color-text-heading)]" title={s.name}>
                    {s.name}
                  </span>
                  {already ? (
                    <Badge variant="success" className="shrink-0 gap-1">
                      <Terminal className="size-2.5" />
                      registered
                    </Badge>
                  ) : selected ? (
                    <Badge variant="info" className="shrink-0">
                      selected
                    </Badge>
                  ) : (
                    <Badge
                      variant="outline"
                      className="shrink-0 opacity-0 transition-opacity group-hover:opacity-100"
                    >
                      register
                    </Badge>
                  )}
                </div>
                <p className="truncate text-[10px] text-[var(--color-text-muted)]" title={s.path}>
                  {s.path}
                </p>

                {/* Edit button for non-registered scripts */}
                {!already && (
                  <div className="flex items-center gap-1 pt-0.5">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={(e) => {
                        e.stopPropagation();
                        // Extract filename from path for editing
                        const filename = s.path.split("/").pop() ?? s.name;
                        setEditingScript(filename);
                      }}
                      title="Edit script content"
                      className="opacity-0 transition-opacity group-hover:opacity-100"
                    >
                      <Pencil className="size-3" />
                      Edit
                    </Button>
                  </div>
                )}
              </button>
            );
          })}
        </div>
      )}

      {/* Inline registration panel */}
      <AnimatePresence>
        {selectedScript && (
          <motion.div
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 8 }}
            transition={{ duration: 0.18 }}
            className="space-y-3 rounded-[var(--radius-xl)] border border-[var(--color-primary)] bg-[var(--color-bg-muted)] p-3.5"
          >
            <div className="flex items-center justify-between gap-2">
              <p className="text-xs font-medium text-[var(--color-text-heading)]">
                Register script
              </p>
              <button
                type="button"
                onClick={() => setSelectedScript(null)}
                aria-label="Cancel selection"
                className="flex size-6 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:bg-[var(--color-bg-elevated)]"
              >
                <X className="size-3.5" />
              </button>
            </div>
            <p className="truncate font-mono text-[10px] text-[var(--color-text-muted)]">
              {selectedScript}
            </p>
            <div className="grid gap-2 sm:grid-cols-[1fr_auto] sm:items-end">
              <Input
                label="Display name"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                placeholder="my-script-server"
              />
              <Input
                label="Port"
                type="number"
                value={port}
                onChange={(e) => setPort(e.target.value)}
                placeholder="8080"
              />
            </div>
            <div className="flex justify-end gap-2 pt-1">
              <Button variant="ghost" size="sm" onClick={() => setSelectedScript(null)}>
                Cancel
              </Button>
              <Button
                size="sm"
                loading={registerMutation.isPending}
                disabled={!displayName.trim()}
                onClick={handleRegister}
              >
                <Terminal className="size-3" />
                Register on {agentName}
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {registerMutation.isError && (
        <p className="text-xs text-[var(--color-status-error)]">
          {registerMutation.error.message}
        </p>
      )}

      {/* Editor dialog */}
      <ScriptEditorDialog
        scriptName={editingScript}
        isHost={false}
        agentName={agentName}
        isRunning={editingScript !== null && isScriptRunning(editingScript)}
        open={editingScript !== null}
        onClose={() => setEditingScript(null)}
        onSaved={() => {
          queryClient.invalidateQueries({ queryKey: ["agent-available-scripts", agentName] });
        }}
      />
    </div>
  );
}
