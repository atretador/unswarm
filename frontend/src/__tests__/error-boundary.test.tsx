import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ErrorBoundary } from "../components/ErrorBoundary";
import { TestWrapper } from "./test-utils";

/** Mutable flag so the retry test can stop the child from throwing. */
let shouldThrow = true;

function Boom() {
  if (shouldThrow) throw new Error("kaboom");
  return <div>recovered</div>;
}

beforeEach(() => {
  shouldThrow = true;
  // React logs the caught render error; keep test output clean.
  vi.spyOn(console, "error").mockImplementation(() => {});
});

describe("ErrorBoundary", () => {
  it("renders children when there is no error", () => {
    render(
      <TestWrapper>
        <ErrorBoundary>
          <div>all good</div>
        </ErrorBoundary>
      </TestWrapper>,
    );

    expect(screen.getByText("all good")).toBeInTheDocument();
  });

  it("catches a render error and shows the translated fallback", () => {
    render(
      <TestWrapper>
        <ErrorBoundary>
          <Boom />
        </ErrorBoundary>
      </TestWrapper>,
    );

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();
    expect(
      screen.getByText("This page failed to load. Try again, or reload the page."),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload page" })).toBeInTheDocument();
  });

  it("retry clears the error state and re-renders the subtree", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ErrorBoundary>
          <Boom />
        </ErrorBoundary>
      </TestWrapper>,
    );

    expect(screen.getByRole("alert")).toBeInTheDocument();

    // Stop the child from throwing so the retry succeeds.
    shouldThrow = false;
    await user.click(screen.getByRole("button", { name: "Retry" }));

    await waitFor(() => {
      expect(screen.getByText("recovered")).toBeInTheDocument();
    });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("reload button triggers window.location.reload", async () => {
    const user = userEvent.setup();
    const originalLocation = window.location;
    const reload = vi.fn();

    Object.defineProperty(window, "location", {
      configurable: true,
      value: { ...originalLocation, reload },
    });

    try {
      render(
        <TestWrapper>
          <ErrorBoundary>
            <Boom />
          </ErrorBoundary>
        </TestWrapper>,
      );

      await user.click(screen.getByRole("button", { name: "Reload page" }));
      expect(reload).toHaveBeenCalledTimes(1);
    } finally {
      Object.defineProperty(window, "location", {
        configurable: true,
        value: originalLocation,
      });
    }
  });
});
