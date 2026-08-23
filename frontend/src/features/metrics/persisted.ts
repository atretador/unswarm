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

export interface MetricsPreset {
  name: string;
  provider: string;
  model: string;
  range: string;
}

const PRESETS_KEY = "unswarm-metrics-presets";

export function useMetricsPresets() {
  const [presets, setPresets] = useState<MetricsPreset[]>(() =>
    readJson<MetricsPreset[]>(PRESETS_KEY, []),
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
