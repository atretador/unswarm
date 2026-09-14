import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLocation } from "react-router-dom";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import LoginPage from "../features/login";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

/** Tiny sentinel that prints the current pathname so tests can assert navigation. */
function LocationDisplay() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

describe("LoginPage", () => {
  // ── 1. Renders login form ──────────────────────────────────────
  it("renders login form with heading, inputs, and sign-in button", async () => {
    render(
      <TestWrapper initialEntries={["/login"]}>
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /sign in to unswarm/i }),
      ).toBeInTheDocument();
    });

    expect(screen.getByLabelText("Username")).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /sign in/i }),
    ).toBeInTheDocument();

    // Marketing copy visible on the brand panel (desktop)
    expect(
      screen.getByText(/one console for your entire agent swarm/i),
    ).toBeInTheDocument();
  });

  // ── 2. Password show / hide toggle ─────────────────────────────
  it("toggles password input type between password and text", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/login"]}>
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Password")).toBeInTheDocument();
    });

    const passwordInput = screen.getByLabelText("Password");
    expect(passwordInput).toHaveAttribute("type", "password");

    // Click "Show password"
    const showBtn = screen.getByRole("button", { name: /show password/i });
    await user.click(showBtn);
    expect(passwordInput).toHaveAttribute("type", "text");

    // Click "Hide password"
    const hideBtn = screen.getByRole("button", { name: /hide password/i });
    await user.click(hideBtn);
    expect(passwordInput).toHaveAttribute("type", "password");
  });

  // ── 3. Successful login → navigate to / ────────────────────────
  it("calls mockClient.login and navigates to / on success", async () => {
    const user = userEvent.setup();
    const loginSpy = vi.spyOn(mockClient, "login").mockResolvedValueOnce({
      username: "admin",
      isTempPassword: false,
    });

    render(
      <TestWrapper initialEntries={["/login"]}>
        <LocationDisplay />
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Username")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Username"), "admin");
    await user.type(screen.getByLabelText("Password"), "s3cret");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(loginSpy).toHaveBeenCalledWith("admin", "s3cret");
    });

    await waitFor(() => {
      expect(screen.getByTestId("location")).toHaveTextContent("/");
    });
  });

  // ── 4. Failed login → error message ────────────────────────────
  it("shows error alert when login fails", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "login").mockRejectedValueOnce(
      new Error("Invalid credentials"),
    );

    render(
      <TestWrapper initialEntries={["/login"]}>
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Username")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Username"), "admin");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.getByRole("alert")).toBeInTheDocument();
    });

    // The i18n key "invalidCredentials" maps to this English string
    expect(
      screen.getByText("Invalid username or password"),
    ).toBeInTheDocument();
  });

  // ── 5. Redirect via location state ─────────────────────────────
  it("navigates to state.from after successful login", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "login").mockResolvedValueOnce({
      username: "admin",
      isTempPassword: false,
    });

    render(
      <TestWrapper
        initialEntries={[
          { pathname: "/login", state: { from: "/dashboard" } } as any,
        ]}
      >
        <LocationDisplay />
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Username")).toBeInTheDocument();
    });

    // The location should start at /login
    expect(screen.getByTestId("location")).toHaveTextContent("/login");

    await user.type(screen.getByLabelText("Username"), "admin");
    await user.type(screen.getByLabelText("Password"), "s3cret");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.getByTestId("location")).toHaveTextContent("/dashboard");
    });
  });

  // ── 6. Loading state ───────────────────────────────────────────
  it("disables the submit button while login is in progress", async () => {
    const user = userEvent.setup();

    // Hold the login promise open so we can observe the loading state
    let resolveLogin!: (value: {
      username: string;
      isTempPassword: boolean;
    }) => void;
    vi.spyOn(mockClient, "login").mockReturnValueOnce(
      new Promise((r) => {
        resolveLogin = r;
      }),
    );

    render(
      <TestWrapper initialEntries={["/login"]}>
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Username")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Username"), "admin");
    await user.type(screen.getByLabelText("Password"), "s3cret");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    // Button should be disabled during the pending login
    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /sign in/i }),
      ).toBeDisabled();
    });

    // Resolve the login and verify the button re-enables
    resolveLogin({ username: "admin", isTempPassword: false });

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /sign in/i }),
      ).not.toBeDisabled();
    });
  });

  // ── 7. Empty form submission ───────────────────────────────────
  it("does not call login when submitting empty required fields", async () => {
    const user = userEvent.setup();
    const loginSpy = vi.spyOn(mockClient, "login");

    render(
      <TestWrapper initialEntries={["/login"]}>
        <LoginPage />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /sign in/i })).toBeInTheDocument();
    });

    // Click submit without filling any fields — native validation should block it
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    // Give the event loop a tick to confirm no login was triggered
    await new Promise((r) => setTimeout(r, 50));

    expect(loginSpy).not.toHaveBeenCalled();
  });
});
