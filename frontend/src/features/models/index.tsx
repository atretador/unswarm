import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { Plus, Trash2, ChevronDown, ChevronUp, Beaker } from "lucide-react";
import { client } from "../../lib/query-client";
import type { Model, ModelStatus } from "../../lib/api/types";
import { Card, Badge, StatusDot, Button, Skeleton, EmptyState, Input, Switch } from "../../components/ui";

const STATUS_VARIANT: Record<ModelStatus, "success" | "info" | "error" | "warning"> = {
  ready: "success",
  validating: "info",
  invalid: "error",
  deprecated: "warning",
};

function ModelRow({
  model,
  onDelete,
  deletePending,
}: {
  model: Model;
  onDelete: () => void;
  deletePending: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const bench = model.lastBenchmark;

  return (
    <motion.div layout initial={{ opacity: 0 }} animate={{ opacity: 1 }}>
      <div
        className="flex items-center gap-4 px-4 py-3 border-b border-[var(--color-border-subtle)] last:border-0 cursor-pointer hover:bg-[var(--color-bg-muted)] transition-colors"
        onClick={() => setExpanded((p) => !p)}
      >
        <StatusDot status={model.status} size="sm" />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-mono text-xs text-[var(--color-text-heading)] truncate">
              {model.name}
            </span>
            <Badge variant={STATUS_VARIANT[model.status]}>{model.status}</Badge>
          </div>
          <p className="text-[10px] text-[var(--color-text-muted)] mt-0.5">
            {model.family} · {model.parameterSize} · {model.quantization}
          </p>
        </div>
        <div className="text-right shrink-0">
          {bench ? (
            <p className="font-mono text-xs text-[var(--color-text-heading)]">
              {bench.tokensPerSec} tok/s
            </p>
          ) : (
            <p className="text-[10px] text-[var(--color-text-muted)]">no benchmark</p>
          )}
        </div>
        <div className="flex items-center gap-1 shrink-0">
          <Button
            variant="ghost"
            size="sm"
            onClick={(e) => { e.stopPropagation(); onDelete(); }}
            disabled={deletePending}
          >
            <Trash2 className="size-3" />
          </Button>
          {expanded ? <ChevronUp className="size-3 text-[var(--color-text-muted)]" /> : <ChevronDown className="size-3 text-[var(--color-text-muted)]" />}
        </div>
      </div>
      <AnimatePresence>
        {expanded && (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.2 }}
            className="overflow-hidden"
          >
            <div className="px-4 py-3 bg-[var(--color-bg-muted)] border-b border-[var(--color-border-subtle)]">
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-xs">
                <div>
                  <p className="text-[var(--color-text-muted)] mb-0.5">Image</p>
                  <p className="font-mono text-[var(--color-text-heading)] break-all">{model.containerImage}</p>
                </div>
                <div>
                  <p className="text-[var(--color-text-muted)] mb-0.5">Context window</p>
                  <p className="font-mono text-[var(--color-text-heading)]">{model.contextWindow.toLocaleString()}</p>
                </div>
                <div>
                  <p className="text-[var(--color-text-muted)] mb-0.5">Created</p>
                  <p className="font-mono text-[var(--color-text-heading)]">
                    {new Date(model.createdAt).toLocaleDateString()}
                  </p>
                </div>
                <div>
                  <p className="text-[var(--color-text-muted)] mb-0.5">Last benchmark</p>
                  {bench ? (
                    <p className="font-mono text-[var(--color-text-heading)]">
                      {bench.tokensPerSec} tok/s · {bench.latencyMs}ms TTFT
                    </p>
                  ) : (
                    <p className="text-[var(--color-text-muted)]">—</p>
                  )}
                </div>
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  );
}

function RegisterForm({ onDone }: { onDone: () => void }) {
  const queryClient = useQueryClient();
  const [form, setForm] = useState({
    name: "",
    family: "",
    parameterSize: "",
    quantization: "Q4_K_M",
    containerImage: "",
    contextWindow: "32000",
    runValidation: true,
  });

  const createMutation = useMutation({
    mutationFn: () =>
      client.createModel({
        name: form.name,
        family: form.family,
        parameterSize: form.parameterSize,
        quantization: form.quantization,
        status: form.runValidation ? "validating" : "ready",
        lastBenchmark: null,
        contextWindow: Number(form.contextWindow),
        containerImage: form.containerImage,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["models"] });
      onDone();
    },
  });

  const updateStatusMutation = useMutation({
    mutationFn: (modelId: string) =>
      client.updateModel(modelId, { status: "ready" }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["models"] });
    },
  });

  const handleSubmit = async () => {
    const model = await createMutation.mutateAsync();
    if (form.runValidation) {
      // Simulate validation completing
      await new Promise((r) => setTimeout(r, 500));
      await updateStatusMutation.mutateAsync(model.id);
    }
  };

  return (
    <Card padding="lg" className="space-y-4">
      <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
        Register Model
      </p>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Input label="Model name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="my-model-7b" />
        <Input label="Family" value={form.family} onChange={(e) => setForm({ ...form, family: e.target.value })} placeholder="Llama" />
        <Input label="Parameter size" value={form.parameterSize} onChange={(e) => setForm({ ...form, parameterSize: e.target.value })} placeholder="7B" />
        <Input label="Context window" value={form.contextWindow} onChange={(e) => setForm({ ...form, contextWindow: e.target.value })} type="number" />
        <Input label="Container image" value={form.containerImage} onChange={(e) => setForm({ ...form, containerImage: e.target.value })} placeholder="org/image:tag" className="md:col-span-2" />
      </div>
      <div className="flex items-center justify-between">
        <Switch
          checked={form.runValidation}
          onCheckedChange={(v) => setForm({ ...form, runValidation: v })}
          label="Run validation + benchmark on register"
        />
        <div className="flex gap-2">
          <Button variant="ghost" size="sm" onClick={onDone}>Cancel</Button>
          <Button
            size="sm"
            onClick={handleSubmit}
            loading={createMutation.isPending}
            disabled={!form.name || !form.containerImage}
          >
            Register
          </Button>
        </div>
      </div>
    </Card>
  );
}

export default function Models() {
  const queryClient = useQueryClient();
  const [showRegister, setShowRegister] = useState(false);

  const { data: models, isLoading, error, refetch, isRefetching } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteModel(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["models"] }),
  });

  if (isLoading) {
    return (
      <div className="p-6 space-y-4 max-w-5xl">
        <Skeleton className="h-6 w-40" />
        <Card padding="none">
          {Array.from({ length: 4 }, (_, i) => (
            <div key={i} className="px-4 py-3 border-b border-[var(--color-border-subtle)]">
              <Skeleton className="h-4 w-48" />
            </div>
          ))}
        </Card>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-5xl">
        <EmptyState
          title="Failed to load models"
          description={error.message}
          action={<Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>Retry</Button>}
        />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
            Model Registry
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Manage registered models, validation status, and benchmarks.
          </p>
        </div>
        <Button size="sm" onClick={() => setShowRegister((p) => !p)}>
          <Plus className="size-3.5" />
          Register
        </Button>
      </div>

      <AnimatePresence>
        {showRegister && (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }}>
            <RegisterForm onDone={() => setShowRegister(false)} />
          </motion.div>
        )}
      </AnimatePresence>

      <Card padding="none">
        <div className="px-4 py-2.5 border-b border-[var(--color-border)] flex items-center justify-between">
          <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            {models?.length ?? 0} models
          </span>
          <div className="flex items-center gap-2 text-[10px] text-[var(--color-text-muted)]">
            <Beaker className="size-3" />
            <span>Benchmark: tok/s · TTFT</span>
          </div>
        </div>
        {models && models.length > 0 ? (
          models.map((model) => (
            <ModelRow
              key={model.id}
              model={model}
              onDelete={() => deleteMutation.mutate(model.id)}
              deletePending={deleteMutation.isPending}
            />
          ))
        ) : (
          <EmptyState
            title="No models registered"
            description="Register your first model to get started."
          />
        )}
      </Card>
    </div>
  );
}
