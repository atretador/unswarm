import { useState, useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocale } from '../i18n/LocaleContext';
import { Globe } from 'lucide-react';

const LOCALES = [
  { code: 'en', label: 'English' },
  { code: 'pt-BR', label: 'Português Brasileiro' },
] as const;

export function LocaleSwitcher() {
  const { i18n, t } = useTranslation("common");
  const { setLocale } = useLocale();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  const current = LOCALES.find(l => l.code === i18n.language) ?? LOCALES[0];

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen(!open)}
        className="
          flex items-center gap-1.5
          rounded-[var(--radius-md)] px-2.5 py-1.5
          text-sm font-medium text-[var(--color-text-muted)]
          hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
          border border-[var(--color-border)]
          transition-colors duration-[var(--duration-fast)]
          cursor-pointer
        "
        title={t("languageSwitcherTitle")}
        aria-label={t("languageSwitcherLabel")}
        aria-expanded={open}
      >
        <Globe className="size-4" />
        <span className="text-xs font-medium">{current.label}</span>
      </button>

      {open && (
        <div
          className="
            absolute right-0 top-full mt-1.5 z-50 w-40
            rounded-[var(--radius-lg)] border border-[var(--color-border)]
            bg-[var(--color-bg-surface)] shadow-lg
            py-1
          "
          style={{ animation: "fadeInDown 120ms ease-out" }}
        >
          {LOCALES.map(locale => (
            <button
              key={locale.code}
              onClick={() => {
                setLocale(locale.code);
                setOpen(false);
              }}
              className={`
                w-full text-left px-3 py-2 text-sm
                transition-colors duration-[var(--duration-fast)]
                ${
                  locale.code === i18n.language
                    ? 'bg-[var(--color-primary-soft)] text-[var(--color-primary)]'
                    : 'text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]'
                }
              `}
            >
              {locale.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
