import { describe, it, expect, beforeEach, vi } from "vitest";
import {
  render,
  screen,
  waitFor,
  within,
  fireEvent,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  MemoryRouter,
  Routes,
  Route,
  useLocation,
} from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { mockClient, setMockLatency } from "../lib/api/mock";
import { createTestQueryClient, TestWrapper } from "./test-utils";
import { ThemeProvider } from "../lib/theme";
import { AuthProvider } from "../lib/auth-context";
import { LocaleProvider } from "../i18n/LocaleContext";
import { Sidebar } from "../components/layout/Sidebar";
import { Topbar, MobileDrawer } from "../components/layout/Topbar";
import { ThemeToggle } from "../components/layout/ThemeToggle";
import { AppShell } from "../components/layout/AppShell";

function LocationDisplay() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
  localStorage.clear();
  document.documentElement.lang = "en";
});

// ─── Sidebar ──────────────────────────────────────────────────────

describe("Sidebar", () => {
  it("renders nav links and toggles collapsed state", async () => {
    const user = userEvent.setup();
    const onToggle = vi.fn();
    render(
      <TestWrapper initialEntries={["/models"]}>
        <Sidebar collapsed={false} onToggle={onToggle} />
      </TestWrapper>,
    );

    expect(screen.getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Models" })).toBeInTheDocument();
    expect(screen.getByText("unswarm")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Collapse sidebar" }));
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it("hides labels and the brand when collapsed", () => {
    render(
      <TestWrapper initialEntries={["/models"]}>
        <Sidebar collapsed onToggle={() => {}} />
      </TestWrapper>,
    );

    expect(screen.getByRole("button", { name: "Expand sidebar" })).toBeInTheDocument();
    expect(screen.queryByText("unswarm")).not.toBeInTheDocument();
    // Labels survive only inside tooltips.
    expect(screen.getAllByRole("tooltip").length).toBe(11);
  });

  it("shows a conflict dot next to Models when a model conflicts", async () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValue([
      { id: "1", status: "conflict" },
    ] as never);

    const { container } = render(
      <TestWrapper initialEntries={["/models"]}>
        <Sidebar collapsed={false} onToggle={() => {}} />
      </TestWrapper>,
    );

    await waitFor(() =>
      expect(container.querySelector(".bg-red-500")).not.toBeNull(),
    );
  });

  it("never shows the conflict dot when collapsed", async () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValue([
      { id: "1", status: "conflict" },
    ] as never);

    const { container } = render(
      <TestWrapper initialEntries={["/models"]}>
        <Sidebar collapsed onToggle={() => {}} />
      </TestWrapper>,
    );

    await waitFor(() => expect(mockClient.listModels).toHaveBeenCalled());
    expect(container.querySelector(".bg-red-500")).toBeNull();
  });
});

// ─── Topbar ───────────────────────────────────────────────────────

describe("Topbar", () => {
  it("renders the page title, proxy status and account controls", () => {
    const onMobileToggle = vi.fn();
    render(
      <TestWrapper initialEntries={["/"]}>
        <Topbar title="My Page" mobileOpen={false} onMobileToggle={onMobileToggle} />
      </TestWrapper>,
    );

    expect(screen.getByRole("heading", { name: "My Page" })).toBeInTheDocument();
    expect(screen.getByText("Proxy active")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /Switch theme/ }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Open navigation" })).toBeInTheDocument();
  });

  it("uses the close label while the mobile drawer is open", () => {
    render(
      <TestWrapper initialEntries={["/"]}>
        <Topbar title="X" mobileOpen onMobileToggle={() => {}} />
      </TestWrapper>,
    );
    expect(screen.getByRole("button", { name: "Close navigation" })).toBeInTheDocument();
  });

  it("shows the user chip and navigates from the dropdown", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });

    render(
      <TestWrapper initialEntries={["/"]}>
        <LocationDisplay />
        <Topbar title="X" mobileOpen={false} onMobileToggle={() => {}} />
      </TestWrapper>,
    );

    await waitFor(() => expect(screen.getByText("admin")).toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "User menu" }));
    await user.click(screen.getByRole("button", { name: "Settings" }));

    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/settings"),
    );
  });

  it("signs out and redirects to /login", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    const logoutSpy = vi.spyOn(mockClient, "logout").mockResolvedValue(undefined);

    render(
      <TestWrapper initialEntries={["/"]}>
        <LocationDisplay />
        <Topbar title="X" mobileOpen={false} onMobileToggle={() => {}} />
      </TestWrapper>,
    );

    await waitFor(() => expect(screen.getByText("admin")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "User menu" }));
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() => expect(logoutSpy).toHaveBeenCalled());
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/login"),
    );
  });

  it("closes the dropdown on outside click", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });

    render(
      <TestWrapper initialEntries={["/"]}>
        <Topbar title="X" mobileOpen={false} onMobileToggle={() => {}} />
      </TestWrapper>,
    );

    await waitFor(() => expect(screen.getByText("admin")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "User menu" }));
    expect(screen.getByRole("button", { name: "Sign out" })).toBeInTheDocument();

    fireEvent.mouseDown(document.body);
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Sign out" })).not.toBeInTheDocument(),
    );
  });
});

// ─── MobileDrawer ─────────────────────────────────────────────────

describe("MobileDrawer", () => {
  it("renders nothing when closed", () => {
    render(
      <TestWrapper initialEntries={["/"]}>
        <MobileDrawer open={false} onClose={() => {}} />
      </TestWrapper>,
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("closes on the close button and on Escape", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(
      <TestWrapper initialEntries={["/"]}>
        <MobileDrawer open onClose={onClose} />
      </TestWrapper>,
    );

    const dialog = screen.getByRole("dialog", { name: "Navigation menu" });
    expect(within(dialog).getByRole("link", { name: "Models" })).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Close navigation" }));
    expect(onClose).toHaveBeenCalledTimes(1);

    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("shows the signed-in user and signs out", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    const logoutSpy = vi.spyOn(mockClient, "logout").mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <TestWrapper initialEntries={["/"]}>
        <LocationDisplay />
        <MobileDrawer open onClose={onClose} />
      </TestWrapper>,
    );

    await waitFor(() => expect(screen.getByText("admin")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() => expect(logoutSpy).toHaveBeenCalled());
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/login"),
    );
  });
});

// ─── ThemeToggle ──────────────────────────────────────────────────

describe("ThemeToggle", () => {
  it("cycles the theme and updates its accessible label", async () => {
    const user = userEvent.setup();
    localStorage.setItem("unswarm-theme", "light");

    render(
      <ThemeProvider>
        <ThemeToggle />
      </ThemeProvider>,
    );

    expect(
      screen.getByRole("button", { name: "Switch theme (currently Light)" }),
    ).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Switch theme (currently Light)" }),
    );
    expect(
      screen.getByRole("button", { name: "Switch theme (currently Dark)" }),
    ).toBeInTheDocument();
  });
});

// ─── AppShell ─────────────────────────────────────────────────────

function Boom(): never {
  throw new Error("render failure");
}

function renderShell(initialPath: string) {
  const client = createTestQueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialPath]}>
        <ThemeProvider>
          <AuthProvider>
            <LocaleProvider>
              <Routes>
                <Route element={<AppShell />}>
                  <Route path="/" element={<div>dashboard body</div>} />
                  <Route path="/models" element={<div>models body</div>} />
                  <Route path="/boom" element={<Boom />} />
                </Route>
              </Routes>
            </LocaleProvider>
          </AuthProvider>
        </ThemeProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("AppShell", () => {
  it("renders the routed page, sidebar and page title", async () => {
    renderShell("/models");
    await waitFor(() => expect(screen.getByText("models body")).toBeInTheDocument());
    expect(screen.getByRole("heading", { name: "Models" })).toBeInTheDocument();
    expect(screen.getByRole("navigation")).toBeInTheDocument();
  });

  it("shows the temp-password banner for a temp-password user", async () => {
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: true,
    });

    renderShell("/");
    await waitFor(() =>
      expect(
        screen.getByText(/using a temporary password/i),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("link", { name: "Change it in Profile" }),
    ).toBeInTheDocument();
  });

  it("opens the mobile drawer from the hamburger", async () => {
    const user = userEvent.setup();
    renderShell("/");
    await waitFor(() => expect(screen.getByText("dashboard body")).toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "Open navigation" }));
    expect(
      screen.getByRole("dialog", { name: "Navigation menu" }),
    ).toBeInTheDocument();

    const dialog = screen.getByRole("dialog", { name: "Navigation menu" });
    await user.click(within(dialog).getByRole("button", { name: "Close navigation" }));
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
  });

  it("catches a routed render error with the ErrorBoundary", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    renderShell("/boom");

    await waitFor(() =>
      expect(
        screen.getByText("Something went wrong"),
      ).toBeInTheDocument(),
    );
  });
});
