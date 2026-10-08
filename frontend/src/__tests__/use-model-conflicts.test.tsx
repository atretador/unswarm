import { describe, it, expect, beforeEach, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { useState, type ReactNode } from "react";
import { QueryClientProvider, type QueryClient } from "@tanstack/react-query";
import { mockClient, setMockLatency } from "../lib/api/mock";
import { useModelConflicts } from "../lib/use-model-conflicts";
import { createTestQueryClient } from "./test-utils";

function Wrapper({ children }: { children: ReactNode }) {
  const [client] = useState<QueryClient>(() => createTestQueryClient());
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("useModelConflicts", () => {
  it("is false while loading and when there are no models", () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValue([]);
    const { result } = renderHook(() => useModelConflicts(), { wrapper: Wrapper });
    expect(result.current).toBe(false);
  });

  it("is false when no model has conflict status", async () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValue([
      { id: "1", status: "ready" },
      { id: "2", status: "ready" },
    ] as never);

    const { result } = renderHook(() => useModelConflicts(), { wrapper: Wrapper });
    await waitFor(() => expect(mockClient.listModels).toHaveBeenCalled());
    expect(result.current).toBe(false);
  });

  it("is true when any model has conflict status", async () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValue([
      { id: "1", status: "ready" },
      { id: "2", status: "conflict" },
    ] as never);

    const { result } = renderHook(() => useModelConflicts(), { wrapper: Wrapper });
    await waitFor(() => expect(result.current).toBe(true));
  });
});
