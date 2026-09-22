import { AudioLines, FileText, Image, Lock, Video, type LucideIcon } from "lucide-react";
import { useTranslation } from "react-i18next";

/** Canonical order of input-modality tokens — the backend uses this same order. */
export const INPUT_MODALITY_ORDER = ["text", "image", "video", "audio", "pdf"] as const;

/** Toggleable modalities; `text` is baseline and never toggled off. */
const TOGGLE_MODALITIES: { key: "image" | "video" | "audio" | "pdf"; icon: LucideIcon }[] = [
  { key: "image", icon: Image },
  { key: "video", icon: Video },
  { key: "audio", icon: AudioLines },
  { key: "pdf", icon: FileText },
];

/**
 * Normalizes any modality list to lowercase, dedupe, canonical order, and always
 * includes `text` — matching the backend's missing/empty → text-only default.
 */
export function normalizeInputModalities(value?: string[] | null): string[] {
  const set = new Set((value ?? []).map((token) => token.trim().toLowerCase()));
  set.add("text");
  return INPUT_MODALITY_ORDER.filter((token) => set.has(token));
}

/**
 * Compact chip control for a model's input modalities. `text` renders as a
 * locked baseline chip; the remaining formats are keyboard-friendly toggles.
 */
export function InputModalitiesField({
  value,
  onChange,
  disabled = false,
}: {
  value: string[];
  onChange: (next: string[]) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation("models");
  const selected = new Set(normalizeInputModalities(value));

  const toggle = (key: string) => {
    const next = new Set(selected);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    onChange(normalizeInputModalities([...next]));
  };

  const chipBase =
    "inline-flex h-7 items-center gap-1 rounded-[var(--radius-md)] border px-2.5 text-xs font-medium transition-colors";

  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-xs font-medium text-[var(--color-text-muted)]">
        {t("form.inputModalities")}
      </span>
      <div
        role="group"
        aria-label={t("form.inputModalities")}
        className="flex flex-wrap items-center gap-1.5"
      >
        {/* text is baseline — shown as a locked chip rather than a toggle */}
        <span
          title={t("form.modalityTextLocked")}
          className={`${chipBase} border-[var(--color-border)] bg-[var(--color-bg-muted)] text-[var(--color-text-muted)]`}
        >
          <Lock className="size-3" aria-hidden />
          {t("form.modalities.text")}
        </span>
        {TOGGLE_MODALITIES.map(({ key, icon: Icon }) => {
          const active = selected.has(key);
          return (
            <button
              key={key}
              type="button"
              aria-pressed={active}
              disabled={disabled}
              onClick={() => toggle(key)}
              className={`${chipBase} focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] disabled:cursor-not-allowed disabled:opacity-50 ${
                active
                  ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
                  : "border-[var(--color-border)] bg-[var(--color-bg-surface)] text-[var(--color-text-muted)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
              }`}
            >
              <Icon className="size-3" aria-hidden />
              {t(`form.modalities.${key}`)}
            </button>
          );
        })}
      </div>
      <p className="text-xs text-[var(--color-text-muted)]">{t("form.inputModalitiesHint")}</p>
    </div>
  );
}
