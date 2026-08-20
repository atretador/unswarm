import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

afterEach(() => cleanup());

// ── Wire httpClient to mockClient in tests ─────────────────────
// Components import `client` from query-client.ts which now points to httpClient.
// Tests spy on mockClient methods, so we alias the httpClient export to the same
// object reference so vi.spyOn works as before.
vi.mock("../lib/api/httpClient", async () => {
  const { mockClient } = await import("../lib/api/mock");
  return { httpClient: mockClient, BASE_URL: "http://localhost:5014" };
});

// ── localStorage polyfill (jsdom 30+ doesn't auto-provide) ──
if (typeof globalThis.localStorage === "undefined") {
  const store = new Map<string, string>();
  globalThis.localStorage = {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => store.set(key, String(value)),
    removeItem: (key: string) => store.delete(key),
    clear: () => store.clear(),
    get length() { return store.size; },
    key: (index: number) => [...store.keys()][index] ?? null,
  } as Storage;
}

// ── matchMedia polyfill (jsdom doesn't provide it) ──
if (typeof globalThis.matchMedia === "undefined") {
  globalThis.matchMedia = function matchMedia(query: string) {
    return {
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    } as unknown as MediaQueryList;
  };
}
