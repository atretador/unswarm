// localStorage-backed UI state for the Metrics page: saved filter presets.
// (Budgets moved to server-side persistence via /api/settings — see
// budgets.tsx.) Degrades gracefully when storage is unavailable or contains
// corrupt data.

import { useCallback, useState } from "react";

function readJson<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (raw) return JSON.parse(raw) as T;
  } catch {
    // corrupt data — fall through to default
  }
  return fallback;
}

function writeJson(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // storage full or unavailable — silently ignore
  }
}

// ─── Saved filter presets ────────────────────────────────────────

/**
 * A saved filter combination. v2 stores multi-select arrays; entries saved
 * before multi-select existed carry singular `provider`/`model` strings and
 * are migrated on read (see normalizePreset).
 */
export interface MetricsPreset {
  name: string;
  providers: string[];
  models: string[];
  range: string;
}

/** Legacy v1 shape (single provider/model strings). */
interface MetricsPresetV1 {
  name: string;
  provider?: string;
  model?: string;
  range?: string;
}

function normalizePreset(raw: unknown): MetricsPreset | null {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Partial<MetricsPreset & MetricsPresetV1>;
  if (typeof r.name !== "string" || r.name.length === 0) return null;
  return {
    name: r.name,
    // v1 → v2 migration: singular strings become one-element arrays.
    providers:
      r.providers ??
      (typeof r.provider === "string" && r.provider !== "" ? [r.provider] : []),
    models:
      r.models ?? (typeof r.model === "string" && r.model !== "" ? [r.model] : []),
    range: typeof r.range === "string" ? r.range : "7d",
  };
}

const PRESETS_KEY = "unswarm-metrics-presets";

export function useMetricsPresets() {
  const [presets, setPresets] = useState<MetricsPreset[]>(() =>
    readJson<unknown[]>(PRESETS_KEY, [])
      .map(normalizePreset)
      .filter((p): p is MetricsPreset => p !== null),
  );

  const savePreset = useCallback((preset: MetricsPreset) => {
    setPresets((prev) => {
      // Replace an existing preset with the same name instead of duplicating.
      const next = [...prev.filter((p) => p.name !== preset.name), preset];
      writeJson(PRESETS_KEY, next);
      return next;
    });
  }, []);

  const deletePreset = useCallback((name: string) => {
    setPresets((prev) => {
      const next = prev.filter((p) => p.name !== name);
      writeJson(PRESETS_KEY, next);
      return next;
    });
  }, []);

  return { presets, savePreset, deletePreset };
}
