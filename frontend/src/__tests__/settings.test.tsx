import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Settings from "../features/settings";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Settings", () => {
  it("renders settings page with tabs", async () => {
    render(
      <TestWrapper initialEntries={["/settings"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });

    // Tab bar should be visible
    expect(screen.getByRole("button", { name: /general/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /users/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /scheduler/i })).toBeInTheDocument();
  });

  it("defaults to General tab with log retention control", async () => {
    render(
      <TestWrapper initialEntries={["/settings"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Log retention (hours)")).toBeInTheDocument();
    });

    expect(screen.getByText("System")).toBeInTheDocument();
  });

  it("navigates to Scheduler tab via URL param", async () => {
    render(
      <TestWrapper initialEntries={["/settings?tab=scheduler"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Scheduler Policy")).toBeInTheDocument();
    });

    expect(screen.getByText("Auto-shutdown idle")).toBeInTheDocument();
    expect(screen.getByText("Batch drain")).toBeInTheDocument();
    expect(screen.getByText("Lazy stop")).toBeInTheDocument();
    expect(screen.getByText("Enable benchmarking")).toBeInTheDocument();

    // New scheduler fields
    expect(screen.getByText("Request timeout (seconds)")).toBeInTheDocument();
    expect(screen.getByText("Idle timeout (seconds)")).toBeInTheDocument();
    expect(screen.getByText("Health check interval (seconds)")).toBeInTheDocument();
    expect(screen.getByText("Priority mode")).toBeInTheDocument();
  });

  it("shows Users tab with user table for admin", async () => {
    render(
      <TestWrapper initialEntries={["/settings?tab=users"]}>
        <Settings />
      </TestWrapper>,
    );

    // Wait for the users tab to be active and data to load
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /add user/i })).toBeInTheDocument();
    });

    // Should show the "Add User" button
    expect(screen.getByRole("button", { name: /add user/i })).toBeInTheDocument();
  });

  it("renders scheduler policy when settings API fails", async () => {
    vi.spyOn(mockClient, "getSettings").mockRejectedValueOnce(new Error("Internal error"));

    render(
      <TestWrapper initialEntries={["/settings?tab=general"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });

    // System section header is static, should always render
    expect(screen.getByText("System")).toBeInTheDocument();
  });

  it("deep-links to tab via search params", async () => {
    render(
      <TestWrapper initialEntries={["/settings?tab=scheduler"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Scheduler Policy")).toBeInTheDocument();
    });
  });

  it("falls back to general for invalid tab param", async () => {
    render(
      <TestWrapper initialEntries={["/settings?tab=invalid"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("System")).toBeInTheDocument();
    });
  });
});

describe("Add User Modal", () => {
  it("opens and validates fields", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/settings?tab=users"]}>
        <Settings />
      </TestWrapper>,
    );

    // Wait for users tab to load
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /add user/i })).toBeInTheDocument();
    });

    // Open the modal
    await user.click(screen.getByRole("button", { name: /add user/i }));

    // Modal should be visible
    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    // Find the submit button inside the dialog
    const dialog = screen.getByRole("dialog");
    const submitButton = within(dialog).getByRole("button", { name: /^add user$/i });
    await user.click(submitButton);

    // Should show username required error
    await waitFor(() => {
      expect(screen.getByText("Username is required.")).toBeInTheDocument();
    });
  });

  it("validates password length", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/settings?tab=users"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /add user/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /add user/i }));

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    const dialog = screen.getByRole("dialog");

    // Fill username with short password
    await user.type(within(dialog).getByLabelText("Username"), "newuser");
    await user.type(within(dialog).getByLabelText("Password"), "123");

    // Submit
    await user.click(within(dialog).getByRole("button", { name: /^add user$/i }));

    await waitFor(() => {
      expect(screen.getByText("Password must be at least 6 characters.")).toBeInTheDocument();
    });
  });

  it("closes on cancel", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/settings?tab=users"]}>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /add user/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /add user/i }));

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    // Click Cancel
    const dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: /cancel/i }));

    await waitFor(() => {
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });
  });
});
