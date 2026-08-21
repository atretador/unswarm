import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Profile from "../features/profile";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Profile", () => {
  it("renders profile page with account and change password sections", async () => {
    render(
      <TestWrapper initialEntries={["/profile"]}>
        <Profile />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Profile")).toBeInTheDocument();
    });

    expect(screen.getByText("Account")).toBeInTheDocument();
    // Change Password section header
    expect(screen.getByText("Change Password", { selector: "p" })).toBeInTheDocument();
  });

  it("renders account identity details", async () => {
    render(
      <TestWrapper initialEntries={["/profile"]}>
        <Profile />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Account")).toBeInTheDocument();
    });

    // Account details
    expect(screen.getByText(/Account details and security/)).toBeInTheDocument();
    // Default "Unknown" when no user logged in via mock
    expect(screen.getByText("Unknown")).toBeInTheDocument();
    expect(screen.getByText("Account active")).toBeInTheDocument();
  });

  it("renders change password form fields", async () => {
    render(
      <TestWrapper initialEntries={["/profile"]}>
        <Profile />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Current password")).toBeInTheDocument();
    });

    expect(screen.getByLabelText("New password")).toBeInTheDocument();
    expect(screen.getByLabelText("Confirm new password")).toBeInTheDocument();
    // Submit button
    expect(screen.getByRole("button", { name: /change password/i })).toBeInTheDocument();
  });

  it("validates password change: too short", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/profile"]}>
        <Profile />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Current password")).toBeInTheDocument();
    });

    // Fill with short password
    await user.type(screen.getByLabelText("Current password"), "oldpass");
    await user.type(screen.getByLabelText("New password"), "123");
    await user.type(screen.getByLabelText("Confirm new password"), "123");

    // Submit
    await user.click(screen.getByRole("button", { name: /change password/i }));

    await waitFor(() => {
      expect(screen.getByText("New password must be at least 6 characters.")).toBeInTheDocument();
    });
  });

  it("validates password change: mismatch", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/profile"]}>
        <Profile />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText("Current password")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Current password"), "oldpass");
    await user.type(screen.getByLabelText("New password"), "newpass123");
    await user.type(screen.getByLabelText("Confirm new password"), "different123");

    await user.click(screen.getByRole("button", { name: /change password/i }));

    await waitFor(() => {
      expect(screen.getByText("New passwords do not match.")).toBeInTheDocument();
    });
  });
});
