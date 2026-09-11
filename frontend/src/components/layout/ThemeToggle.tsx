import { Sun, Moon, Monitor } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useTheme } from "../../lib/theme";
import { Tooltip } from "../ui/Tooltip";

const ICONS = {
  light: Sun,
  dark: Moon,
  system: Monitor,
};

export function ThemeToggle() {
  const { choice, cycle } = useTheme();
  const { t } = useTranslation("common");
  const Icon = ICONS[choice];

  return (
    <Tooltip content={t('themeTooltip', { theme: t(`theme.${choice}`) })} side="bottom">
      <button
        onClick={cycle}
        className="
          flex items-center justify-center size-7 rounded-[var(--radius-md)]
          text-[var(--color-text-muted)] hover:text-[var(--color-text)]
          hover:bg-[var(--color-bg-muted)]
          transition-colors duration-[var(--duration-fast)]
          cursor-pointer
        "
        aria-label={t('themeSwitchAriaLabel', { theme: t(`theme.${choice}`) })}
      >
        <Icon className="size-4" strokeWidth={1.5} />
      </button>
    </Tooltip>
  );
}
