import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import ApiKeys from "../features/api-keys";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("API Keys page", () => {
  it("renders the page and its guidance", async () => {
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("API Keys")).toBeInTheDocument();
    });

    expect(
      screen.getByText(/not login credentials/, { exact: false }),
    ).toBeInTheDocument();
  });

  it("shows seeded keys with scope badges", async () => {
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    // The seeded "Local dashboard test" key is inference-scoped.
    expect(screen.getByText("Inference")).toBeInTheDocument();
    expect(screen.getByText("Agent")).toBeInTheDocument();
  });

  it("creates a new inference key and reveals the secret once", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Create Key" })).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Key name"), "CI runner");
    await user.click(screen.getByRole("button", { name: "Create Key" }));

    // The freshly created key appears in the list.
    await waitFor(() => {
      expect(screen.getByText("CI runner")).toBeInTheDocument();
    });

    // The secret banner is shown immediately after creation.
    await waitFor(() => {
      expect(
        screen.getByText("Key created — copy your secret now."),
      ).toBeInTheDocument();
    });
  });

  it("revoke disables the revoke button", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);

    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    // Revoke the seeded "Go agent" key — confirm dialog is stubbed to allow.
    await userEvent.click(screen.getByRole("button", { name: "Revoke Go agent" }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: "Revoke Go agent" }),
      ).toBeDisabled();
    });
  });

  it("rotates a key and reveals a new secret", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Rotate Go agent" }));

    await waitFor(() => {
      expect(
        screen.getByText("Key rotated — copy your new secret now."),
      ).toBeInTheDocument();
    });
  });
});
