import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

// Provide a real ApiError class (the global setup mock only exposes httpClient
// and BASE_URL) while keeping the client alias behaviour intact.
vi.mock("../lib/api/httpClient", async () => {
  const { mockClient } = await import("../lib/api/mock");
  class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.name = "ApiError";
      this.status = status;
    }
  }
  return { ApiError, httpClient: mockClient, BASE_URL: "http://localhost:5014" };
});

import { ApiError, BASE_URL } from "../lib/api/httpClient";
import {
  getProviderModelCatalog,
  getApiKeyAccess,
  putApiKeyAccess,
  getApiKeyUsage,
} from "../features/api-keys/api-keys-api";

function jsonResponse(body: unknown, status = 200, statusText = "") {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText,
    json: async () => body,
    text: async () => (body === undefined ? "" : JSON.stringify(body)),
  } as unknown as Response;
}

function stubFetch(res: Response) {
  const fn = vi.fn().mockResolvedValue(res);
  vi.stubGlobal("fetch", fn);
  return fn;
}

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("api-keys-api", () => {
  it("fetches the provider/model catalog from the right path with credentials", async () => {
    const payload = [{ provider: "cloud", model: "gpt-4o" }];
    const fetchMock = stubFetch(jsonResponse(payload));

    await expect(getProviderModelCatalog()).resolves.toEqual(payload);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`${BASE_URL}/api/provider-model-catalog`);
    expect(init).toMatchObject({ credentials: "include" });
    expect((init as RequestInit).headers).toMatchObject({
      Accept: "application/json",
    });
  });

  it("URL-encodes the key id when loading access grants", async () => {
    const payload = { providers: [], models: [] };
    const fetchMock = stubFetch(jsonResponse(payload));

    await expect(getApiKeyAccess("id/with space")).resolves.toEqual(payload);

    expect(fetchMock.mock.calls[0][0]).toBe(
      `${BASE_URL}/api/api-keys/id%2Fwith%20space/access`,
    );
  });

  it("PUTs the access payload as JSON", async () => {
    const access = { providers: ["p"], models: ["m"] } as never;
    const fetchMock = stubFetch(jsonResponse(access));

    await expect(putApiKeyAccess("k1", access)).resolves.toEqual(access);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`${BASE_URL}/api/api-keys/k1/access`);
    expect(init).toMatchObject({ method: "PUT" });
    expect(JSON.parse((init as RequestInit).body as string)).toEqual(access);
  });

  it("serializes from/to window for usage queries", async () => {
    const fetchMock = stubFetch(jsonResponse({ totals: {}, models: [] }));
    await getApiKeyUsage("k1", { from: "2024-01-01", to: "2024-02-01" });

    const url = fetchMock.mock.calls[0][0] as string;
    expect(url).toContain("/api/metrics/api-keys/k1/usage?");
    expect(url).toContain("from=2024-01-01");
    expect(url).toContain("to=2024-02-01");
  });

  it("omits the query string when no window is given", async () => {
    const fetchMock = stubFetch(jsonResponse({ totals: {}, models: [] }));
    await getApiKeyUsage("k1");
    expect(fetchMock.mock.calls[0][0]).toBe(
      `${BASE_URL}/api/metrics/api-keys/k1/usage`,
    );
  });

  it("returns undefined for an empty response body", async () => {
    stubFetch(jsonResponse(undefined));
    await expect(getApiKeyAccess("k1")).resolves.toBeUndefined();
  });

  it("throws ApiError with the message field from a JSON error body", async () => {
    stubFetch(jsonResponse({ message: "Not allowed" }, 403, "Forbidden"));

    const err = await getApiKeyAccess("k1").catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(403);
    expect(err.message).toBe("Not allowed");
  });

  it("falls back to the error field from a JSON error body", async () => {
    stubFetch(jsonResponse({ error: "boom" }, 500));

    await expect(getApiKeyAccess("k1")).rejects.toMatchObject({
      status: 500,
      message: "boom",
    });
  });

  it("falls back to the status text when the error body is not JSON", async () => {
    const res = {
      ok: false,
      status: 502,
      statusText: "Bad Gateway",
      json: async () => {
        throw new Error("not json");
      },
      text: async () => "",
    } as unknown as Response;
    stubFetch(res);

    await expect(getApiKeyAccess("k1")).rejects.toMatchObject({
      status: 502,
      message: "Bad Gateway",
    });
  });
});
