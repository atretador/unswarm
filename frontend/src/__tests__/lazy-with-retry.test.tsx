import { Component, Suspense, type ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { lazyWithRetry } from "../lib/lazy-with-retry";

function Healthy() {
  return <div>chunk loaded</div>;
}

class TestBoundary extends Component<
  { children: ReactNode },
  { failed: boolean }
> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? (
      <div>boundary caught failure</div>
    ) : (
      this.props.children
    );
  }
}

const noDelay = { retryDelayMs: () => 0 };

describe("lazyWithRetry", () => {
  it("loads the module on the first attempt", async () => {
    const importer = vi.fn(() => Promise.resolve({ default: Healthy }));
    const LazyHealthy = lazyWithRetry(importer, noDelay);

    render(
      <Suspense fallback={<div>loading</div>}>
        <LazyHealthy />
      </Suspense>,
    );

    expect(await screen.findByText("chunk loaded")).toBeInTheDocument();
    expect(importer).toHaveBeenCalledTimes(1);
  });

  it("recovers from transient import failures by retrying", async () => {
    let attempts = 0;
    const importer = vi.fn(() => {
      attempts += 1;
      if (attempts < 3) return Promise.reject(new Error("chunk load failed"));
      return Promise.resolve({ default: Healthy });
    });
    const LazyHealthy = lazyWithRetry(importer, noDelay);

    render(
      <Suspense fallback={<div>loading</div>}>
        <LazyHealthy />
      </Suspense>,
    );

    expect(await screen.findByText("chunk loaded")).toBeInTheDocument();
    expect(importer).toHaveBeenCalledTimes(3);
  });

  it("rejects after retries are exhausted", async () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const importer = vi.fn(() => Promise.reject(new Error("chunk load failed")));
    const LazyBroken = lazyWithRetry(importer, {
      retries: 2,
      retryDelayMs: () => 0,
    });

    render(
      <Suspense fallback={<div>loading</div>}>
        <TestBoundary>
          <LazyBroken />
        </TestBoundary>
      </Suspense>,
    );

    expect(await screen.findByText("boundary caught failure")).toBeInTheDocument();
    expect(importer).toHaveBeenCalledTimes(3); // initial attempt + 2 retries
    consoleError.mockRestore();
  });
});
