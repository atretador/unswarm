import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import {
  AlertTriangle,
  Brain,
  ChevronDown,
  ChevronUp,
  Clock,
  Hash,
  MessageSquare,
  RotateCcw,
  SendHorizontal,
  SlidersHorizontal,
  Square,
  Zap,
} from "lucide-react";
import { Drawer, Badge, Button, Input, StatusDot, Tooltip } from "../../components/ui";
import { client } from "../../lib/query-client";
import type {
  ChatRole,
  Model,
  ModelStatus,
  Settings,
  TestChatTurnResult,
} from "../../lib/api/types";
import { formatModelName } from "../../lib/format-model-name";

// ─── Status palette — identical semantics to the Models page rows ──

const MODEL_STATUS_VARIANT: Record<ModelStatus, "success" | "warning" | "error" | "default"> = {
  ready: "success",
  validating: "warning",
  invalid: "error",
  deprecated: "default",
};

const MODEL_STATUS_LABEL: Record<ModelStatus, string> = {
  ready: "ready",
  validating: "validating…",
  invalid: "invalid",
  deprecated: "deprecated",
};

// ─── Conversation types ─────────────────────────────────────────────

interface ChatTurn {
  id: string;
  role: ChatRole;
  /** Answer text (streamed incrementally for assistant turns). */
  content: string;
  /** Thinking text emitted via reasoning_content (thinking models). */
  reasoning: string | null;
  /** Observed performance stats; present once the turn completes. */
  stats?: {
    latencyMs: number;
    promptTokens: number | null;
    completionTokens: number | null;
  };
  /** Transport/upstream error message for this turn. */
  error?: string;
  /** True when the user pressed Stop mid-stream (partial reply kept). */
  stopped?: boolean;
}

let turnSeq = 0;
function nextTurnId(): string {
  return `t${++turnSeq}`;
}

/** Quick-fire prompts shown on an empty transcript — good connection smoke tests. */
const QUICK_PROMPTS = [
  "Reply with exactly: hello",
  "What model are you?",
  "Write one short sentence about the sea.",
];

export interface TestChatDrawerProps {
  model: Model | null;
  open: boolean;
  settings?: Settings;
  onClose: () => void;
}

/**
 * Right-side drawer for interactively testing one model through the proxy.
 * Conversations are kept per model id (in memory, session-only) so switching
 * between models doesn't lose a transcript, and closing the drawer doesn't either.
 */
export function TestChatDrawer({ model, open, settings, onClose }: TestChatDrawerProps) {
  const [conversations, setConversations] = useState<Record<string, ChatTurn[]>>({});
  const [input, setInput] = useState("");
  const [pending, setPending] = useState(false);
  const [paramsOpen, setParamsOpen] = useState(false);
  const [system, setSystem] = useState("");
  const [maxTokensText, setMaxTokensText] = useState("");

  const abortRef = useRef<AbortController | null>(null);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  // Stable identity across renders (memoized) so effect/dep arrays below behave.
  const turns = useMemo(
    () => (model ? conversations[model.id] ?? [] : []),
    [model, conversations],
  );

  // Auto-scroll the transcript as it grows or streams.
  useEffect(() => {
    if (!open) return;
    bottomRef.current?.scrollIntoView?.({ block: "end" });
  }, [open, pending, turns]);

  const appendTurn = useCallback(
    (modelId: string, turn: ChatTurn) => {
      setConversations((prev) => ({
        ...prev,
        [modelId]: [...(prev[modelId] ?? []), turn],
      }));
    },
    [],
  );

  const patchLastAssistantTurn = useCallback(
    (modelId: string, patch: (turn: ChatTurn) => ChatTurn) => {
      setConversations((prev) => {
        const list = prev[modelId];
        if (!list || list.length === 0) return prev;
        return { ...prev, [modelId]: [...list.slice(0, -1), patch(list[list.length - 1])] };
      });
    },
    [],
  );

  const handleSend = useCallback(
    async (rawText?: string) => {
      if (!model || pending) return;
      const text = (rawText ?? input).trim();
      if (!text) return;

      const history = [
        ...turns
          .filter((t) => !t.error)
          .map((t): { role: ChatRole; content: string } => ({
            role: t.role,
            content: t.content,
          })),
        { role: "user" as const, content: text },
      ];

      setInput("");
      appendTurn(model.id, { id: nextTurnId(), role: "user", content: text, reasoning: null });
      appendTurn(model.id, { id: nextTurnId(), role: "assistant", content: "", reasoning: null });
      setPending(true);

      const controller = new AbortController();
      abortRef.current = controller;

      try {
        const parsedMaxTokens = Number.parseInt(maxTokensText, 10);
        const result: TestChatTurnResult = await client.sendTestChat(
          model.id,
          history,
          {
            system: system.trim() || undefined,
            maxTokens: Number.isFinite(parsedMaxTokens) && parsedMaxTokens > 0
              ? parsedMaxTokens
              : undefined,
            signal: controller.signal,
            onDelta: (delta) => {
              patchLastAssistantTurn(model.id, (turn) => ({
                ...turn,
                content: delta.content ? turn.content + delta.content : turn.content,
                reasoning: delta.reasoning
                  ? (turn.reasoning ?? "") + delta.reasoning
                  : turn.reasoning,
              }));
            },
          },
        );
        patchLastAssistantTurn(model.id, (turn) => ({
          ...turn,
          content: result.content || turn.content,
          reasoning: result.reasoning ?? turn.reasoning,
          stats: {
            latencyMs: result.latencyMs,
            promptTokens: result.promptTokens,
            completionTokens: result.completionTokens,
          },
        }));
      } catch (err) {
        if ((err as Error)?.name === "AbortError") {
          patchLastAssistantTurn(model.id, (turn) => ({ ...turn, stopped: true }));
        } else {
          patchLastAssistantTurn(model.id, (turn) => ({
            ...turn,
            error: (err as Error)?.message || "Request failed",
          }));
        }
      } finally {
        abortRef.current = null;
        setPending(false);
      }
    },
    [model, pending, input, turns, system, maxTokensText, appendTurn, patchLastAssistantTurn],
  );

  const handleStop = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const handleClear = useCallback(() => {
    if (!model) return;
    setConversations((prev) => ({ ...prev, [model.id]: [] }));
  }, [model]);

  if (!model) return null;

  const displayName = formatModelName(
    model.name,
    model.origin === "cloud"
      ? model.providerName ?? "cloud"
      : model.sourceRuntimeAgent ?? "local",
    settings?.hideOriginPrefix ?? false,
    settings?.agentDisplayNames ?? {},
  );
  const subtitle =
    model.origin === "cloud"
      ? `${model.providerName ?? "cloud"} · ${model.contextWindow.toLocaleString()} context`
      : `${model.family} · ${model.parameterSize} · ${model.quantization}`;

  // ── Composer — plain JSX, NOT an inner component: re-creating the element
  // type every render would remount the textarea on each keystroke and reset
  // the caret. This is just a rendered footer node.
  const composer = (
    <div className="space-y-2 p-4">
      <div className="flex items-end gap-2">
        <textarea
          aria-label="Message"
          value={input}
          autoFocus
          rows={1}
          placeholder={`Message ${displayName}…`}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void handleSend();
            }
          }}
          className="max-h-32 min-h-[38px] w-full resize-none rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] outline-none transition-colors focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]"
        />
        {pending ? (
          <Button
            variant="secondary"
            size="md"
            onClick={handleStop}
            aria-label="Stop generating"
            className="shrink-0"
          >
            <Square className="size-3.5 fill-current" />
            Stop
          </Button>
        ) : (
          <Button
            variant="primary"
            size="md"
            onClick={() => void handleSend()}
            disabled={input.trim().length === 0}
            aria-label="Send message"
            className="shrink-0"
          >
            <SendHorizontal className="size-3.5" />
            Send
          </Button>
        )}
      </div>
      <p className="text-[10px] text-[var(--color-text-muted)]">
        Enter to send · Shift+Enter for newline · requests go through the proxy like real /v1 traffic
      </p>
    </div>
  );

  return (
    <Drawer
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={displayName}
      subtitle={
        <span className="flex items-center gap-2">
          <span className="truncate">{subtitle}</span>
          <Badge variant={MODEL_STATUS_VARIANT[model.status]} size="sm">
            {MODEL_STATUS_LABEL[model.status]}
          </Badge>
        </span>
      }
      footer={composer}
      className="max-w-xl"
    >
      <div className="flex h-full flex-col">
        {/* Toolbar */}
        <div className="flex items-center justify-between gap-2 px-4 pt-3">
          <button
            type="button"
            onClick={() => setParamsOpen((v) => !v)}
            aria-expanded={paramsOpen}
            className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] px-2 py-1 text-[11px] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
          >
            <SlidersHorizontal className="size-3" />
            Parameters
            {paramsOpen ? <ChevronUp className="size-3" /> : <ChevronDown className="size-3" />}
          </button>
          <Tooltip content="Clear this conversation">
            <button
              type="button"
              onClick={handleClear}
              disabled={pending || turns.length === 0}
              aria-label="Clear conversation"
              className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] disabled:pointer-events-none disabled:opacity-40"
            >
              <RotateCcw className="size-3.5" />
            </button>
          </Tooltip>
        </div>

        {/* Optional generation parameters */}
        {paramsOpen && (
          <div className="grid grid-cols-[1fr_7rem] items-end gap-3 border-b border-[var(--color-border-subtle)] px-4 pb-3 pt-1">
            <Input
              label="System prompt"
              value={system}
              onChange={(e) => setSystem(e.target.value)}
              placeholder="Optional instructions for the model…"
              disabled={pending}
            />
            <Input
              label="Max tokens"
              value={maxTokensText}
              onChange={(e) => setMaxTokensText(e.target.value)}
              placeholder="default"
              inputMode="numeric"
              disabled={pending}
            />
          </div>
        )}

        {/* Transcript */}
        <div
          className="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-4"
          data-testid="test-chat-transcript"
        >
          {turns.length === 0 ? (
            <EmptyTranscript onPick={handleSend} disabled={pending} />
          ) : (
            turns.map((turn) => <TurnBubble key={turn.id} turn={turn} />)
          )}
          <div ref={bottomRef} />
        </div>
      </div>
    </Drawer>
  );
}

// ─── Transcript pieces ───────────────────────────────────────────────

function EmptyTranscript({
  onPick,
  disabled,
}: {
  onPick: (prompt: string) => void;
  disabled: boolean;
}) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4 py-10 text-center">
      <div className="flex size-10 items-center justify-center rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-primary)_10%,transparent)]">
        <MessageSquare className="size-5 text-[var(--color-primary)]" />
      </div>
      <div>
        <p className="text-sm font-medium text-[var(--color-text-heading)]">Test this model</p>
        <p className="mt-1 max-w-xs text-xs text-[var(--color-text-muted)]">
          Messages are sent through the proxy exactly like external /v1 clients — a reply proves the model and connection work.
        </p>
      </div>
      <div className="flex flex-wrap justify-center gap-2" data-testid="test-chat-quick-prompts">
        {QUICK_PROMPTS.map((p) => (
          <button
            key={p}
            type="button"
            disabled={disabled}
            onClick={() => onPick(p)}
            className="rounded-full border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-1.5 text-xs text-[var(--color-text-muted)] transition-colors hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] disabled:pointer-events-none disabled:opacity-50"
          >
            {p}
          </button>
        ))}
      </div>
    </div>
  );
}

function TurnBubble({ turn }: { turn: ChatTurn }) {
  if (turn.role === "user") {
    return (
      <div className="flex justify-end" data-role="user">
        <div className="max-w-[85%] whitespace-pre-wrap rounded-[var(--radius-lg)] rounded-br-sm bg-[color-mix(in_srgb,var(--color-primary)_12%,transparent)] px-3 py-2 text-sm text-[var(--color-text)]">
          {turn.content}
        </div>
      </div>
    );
  }

  const tps =
    turn.stats?.completionTokens &&
    turn.stats.latencyMs > 0 &&
    turn.stats.completionTokens > 0
      ? turn.stats.completionTokens / (turn.stats.latencyMs / 1000)
      : null;

  return (
    <div className="flex justify-start" data-role="assistant">
      <div className="max-w-[90%] space-y-2">
        {turn.reasoning && (
          <details className="group rounded-[var(--radius-md)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-3 py-2">
            <summary className="flex cursor-pointer select-none items-center gap-1.5 text-[11px] font-medium text-[var(--color-text-muted)] marker:hidden [&::-webkit-details-marker]:hidden">
              <Brain className="size-3" />
              Thinking
            </summary>
            <p className="mt-1.5 whitespace-pre-wrap text-xs leading-relaxed text-[var(--color-text-muted)]">
              {turn.reasoning}
            </p>
          </details>
        )}

        {turn.error ? (
          <div
            role="alert"
            data-testid="test-chat-error"
            className="flex items-start gap-2 rounded-[var(--radius-md)] border border-[color-mix(in_srgb,var(--color-status-error)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-3 py-2"
          >
            <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-[var(--color-status-error)]" />
            <div className="min-w-0">
              <p className="text-xs font-medium text-[var(--color-status-error)]">
                Request failed
              </p>
              <p className="mt-0.5 break-words text-xs text-[var(--color-text-muted)]">
                {turn.error}
              </p>
            </div>
          </div>
        ) : (
          <div className="whitespace-pre-wrap rounded-[var(--radius-lg)] rounded-bl-sm border border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)] px-3 py-2 text-sm text-[var(--color-text)]">
            {turn.content || <span className="italic text-[var(--color-text-muted)]">…</span>}
            {turn.stopped && turn.content && (
              <span className="ml-1 align-middle text-[10px] italic text-[var(--color-text-muted)]">
                (stopped)
              </span>
            )}
          </div>
        )}

        {turn.stats && !turn.error && (
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 pl-1 text-[10px] text-[var(--color-text-muted)]">
            <span className="inline-flex items-center gap-1" title="Round-trip latency">
              <Clock className="size-2.5" />
              {Math.round(turn.stats.latencyMs)}ms
            </span>
            {tps !== null && tps > 0 && (
              <span className="inline-flex items-center gap-1" title="Generation speed (estimated)">
                <Zap className="size-2.5" />
                {tps.toFixed(1)} tok/s
              </span>
            )}
            {turn.stats.promptTokens != null && turn.stats.promptTokens > 0 && (
              <span className="inline-flex items-center gap-1" title="Prompt tokens">
                <Hash className="size-2.5 rotate-90" />
                {turn.stats.promptTokens.toLocaleString()} prompt
              </span>
            )}
            {turn.stats.completionTokens != null && turn.stats.completionTokens > 0 && (
              <span className="inline-flex items-center gap-1" title="Completion tokens">
                <Hash className="size-2.5" />
                {turn.stats.completionTokens.toLocaleString()} out
              </span>
            )}
          </div>
        )}

        {!turn.stats && !turn.error && turn.content && (
          <div className="flex items-center gap-1 pl-1 text-[10px] text-[var(--color-text-muted)]">
            <StatusDot status="validating" size="sm" /> generating…
          </div>
        )}
      </div>
    </div>
  );
}
