import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider, useTheme, type ThemeChoice } from "../lib/theme";

// ─── Test helper ──────────────────────────────────────────────────

function TestConsumer() {
  const { choice, resolved, cycle, setChoice } = useTheme();
  return (
    <div>
      <span data-testid="choice">{choice}</span>
      <span data-testid="resolved">{resolved}</span>
      <button onClick={cycle}>cycle</button>
      <button onClick={() => setChoice("light")}>set-light</button>
      <button onClick={() => setChoice("dark")}>set-dark</button>
      <button onClick={() => setChoice("system")}>set-system</button>
    </div>
  );
}

function renderTheme(initial?: ThemeChoice) {
  // Seed localStorage before render so useState reads it
  if (initial) localStorage.setItem("unswarm-theme", initial);
  return render(
    <ThemeProvider>
      <TestConsumer />
    </ThemeProvider>,
  );
}

// ─── matchMedia mock ──────────────────────────────────────────────

function mockMatchMedia(matchesDark: boolean) {
  const listeners: Array<(e: MediaQueryListEvent) => void> = [];
  const mq = {
    matches: matchesDark,
    addEventListener: vi.fn((_: string, handler: (e: MediaQueryListEvent) => void) => {
      listeners.push(handler);
    }),
    removeEventListener: vi.fn((_: string, handler: (e: MediaQueryListEvent) => void) => {
      const idx = listeners.indexOf(handler);
      if (idx >= 0) listeners.splice(idx, 1);
    }),
  };

  vi.stubGlobal("matchMedia", vi.fn(() => mq));

  return {
    mq,
    /** Simulate OS preference change */
    changePreference(dark: boolean) {
      mq.matches = dark;
      const event = { matches: dark } as MediaQueryListEvent;
      listeners.forEach((fn) => fn(event));
    },
  };
}

// ─── Tests ────────────────────────────────────────────────────────

describe("ThemeProvider", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.unstubAllGlobals();
    document.documentElement.removeAttribute("data-theme");
  });

  it("defaults to system when localStorage is empty", () => {
    mockMatchMedia(false);
    renderTheme();
    expect(screen.getByTestId("choice")).toHaveTextContent("system");
    expect(screen.getByTestId("resolved")).toHaveTextContent("light");
  });

  it("reads initial choice from localStorage", () => {
    mockMatchMedia(false);
    renderTheme("dark");
    expect(screen.getByTestId("choice")).toHaveTextContent("dark");
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
  });

  it("cycles light -> dark -> system -> light", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    renderTheme("light");

    expect(screen.getByTestId("choice")).toHaveTextContent("light");

    await user.click(screen.getByText("cycle"));
    expect(screen.getByTestId("choice")).toHaveTextContent("dark");

    await user.click(screen.getByText("cycle"));
    expect(screen.getByTestId("choice")).toHaveTextContent("system");

    await user.click(screen.getByText("cycle"));
    expect(screen.getByTestId("choice")).toHaveTextContent("light");
  });

  it("persists choice to localStorage", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    renderTheme("light");

    await user.click(screen.getByText("set-dark"));
    expect(localStorage.getItem("unswarm-theme")).toBe("dark");

    await user.click(screen.getByText("set-system"));
    expect(localStorage.getItem("unswarm-theme")).toBe("system");
  });

  it("applies resolved theme as data-theme attribute on <html>", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    renderTheme("light");

    expect(document.documentElement.getAttribute("data-theme")).toBe("light");

    await user.click(screen.getByText("set-dark"));
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");

    await user.click(screen.getByText("set-system"));
    expect(document.documentElement.getAttribute("data-theme")).toBe("light");
  });

  it("in system mode, matchMedia change updates resolved theme", async () => {
    const { changePreference } = mockMatchMedia(false);
    renderTheme("system");

    expect(screen.getByTestId("resolved")).toHaveTextContent("light");

    await act(async () => {
      changePreference(true);
    });

    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");

    await act(async () => {
      changePreference(false);
    });

    expect(screen.getByTestId("resolved")).toHaveTextContent("light");
  });
});
