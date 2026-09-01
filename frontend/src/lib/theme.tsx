import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";

export type ThemeChoice = "light" | "dark" | "system";
export type ResolvedTheme = "light" | "dark";

interface ThemeContextValue {
  /** The user's stored choice (light | dark | system) */
  choice: ThemeChoice;
  /** The resolved theme currently applied to the DOM */
  resolved: ResolvedTheme;
  /** Cycle through: light → dark → system → light */
  cycle: () => void;
  /** Set a specific choice */
  setChoice: (c: ThemeChoice) => void;
}

const STORAGE_KEY = "unswarm-theme";

const ThemeContext = createContext<ThemeContextValue | null>(null);

function resolveSystem(): ResolvedTheme {
  return window.matchMedia("(prefers-color-scheme: dark)").matches
    ? "dark"
    : "light";
}

function applyTheme(resolved: ResolvedTheme) {
  document.documentElement.setAttribute("data-theme", resolved);
}

const CYCLE_ORDER: ThemeChoice[] = ["light", "dark", "system"];

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [choice, setChoiceState] = useState<ThemeChoice>(() => {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored === "light" || stored === "dark" || stored === "system") {
        return stored;
      }
    } catch {
      // localStorage unavailable (private browsing, quota, lockdown)
    }
    return "system";
  });

  // Track the system preference separately for "system" mode
  const [systemPreference, setSystemPreference] = useState<ResolvedTheme>(() =>
    resolveSystem(),
  );

  // Derive resolved from choice + system preference (no set-state-in-effect)
  const resolved: ResolvedTheme =
    choice === "system" ? systemPreference : choice === "dark" ? "dark" : "light";

  // Sync DOM attribute + localStorage whenever resolved/choice changes
  useEffect(() => {
    applyTheme(resolved);
    try {
      localStorage.setItem(STORAGE_KEY, choice);
    } catch {
      // localStorage unavailable — skip persistence silently
    }
  }, [resolved, choice]);

  // Listen for OS preference changes when in "system" mode
  useEffect(() => {
    if (choice !== "system") return;
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const handler = (e: MediaQueryListEvent) => {
      setSystemPreference(e.matches ? "dark" : "light");
    };
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, [choice]);

  const cycle = useCallback(() => {
    setChoiceState((prev) => {
      const idx = CYCLE_ORDER.indexOf(prev);
      return CYCLE_ORDER[(idx + 1) % CYCLE_ORDER.length];
    });
  }, []);

  const setChoice = useCallback((c: ThemeChoice) => {
    setChoiceState(c);
  }, []);

  const value = useMemo(
    () => ({ choice, resolved, cycle, setChoice }),
    [choice, resolved, cycle, setChoice],
  );

  return (
    <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used within ThemeProvider");
  return ctx;
}
