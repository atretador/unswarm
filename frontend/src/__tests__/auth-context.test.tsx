import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { renderHook, waitFor, act } from "@testing-library/react";
import type { ReactNode } from "react";
import { mockClient, setMockLatency } from "../lib/api/mock";
import { AuthProvider, useAuth } from "../lib/auth-context";
import i18n from "../i18n";

function wrapper({ children }: { children: ReactNode }) {
  return <AuthProvider>{children}</AuthProvider>;
}

beforeEach(async () => {
  setMockLatency(0);
  vi.restoreAllMocks();
  localStorage.clear();
  await i18n.changeLanguage("en");
  document.documentElement.lang = "en";
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("useAuth", () => {
  it("throws when used outside AuthProvider", () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => renderHook(() => useAuth())).toThrow();
  });
});

describe("AuthProvider", () => {
  it("loads the current user on mount", async () => {
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });

    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.user).toEqual({
      username: "admin",
      isTempPassword: false,
    });
  });

  it("falls back to a null user when the session probe fails", async () => {
    vi.spyOn(mockClient, "getMe").mockRejectedValue(new Error("401"));

    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => expect(result.current.isPending).toBe(false));
    expect(result.current.user).toBeNull();
  });

  it("login sets the user and applies the fetched locale preference", async () => {
    vi.spyOn(mockClient, "getMe").mockRejectedValue(new Error("401"));
    vi.spyOn(mockClient, "login").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({ locale: "pt-BR" }),
      }),
    );

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.isPending).toBe(false));

    await act(async () => {
      await result.current.login("admin", "pw");
    });

    expect(result.current.user?.username).toBe("admin");
    expect(document.documentElement.lang).toBe("pt-BR");
    expect(localStorage.getItem("locale")).toBe("pt-BR");
  });

  it("login ignores a locale response without a locale", async () => {
    vi.spyOn(mockClient, "getMe").mockRejectedValue(new Error("401"));
    vi.spyOn(mockClient, "login").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) }),
    );

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.isPending).toBe(false));

    await act(async () => {
      await result.current.login("admin", "pw");
    });

    expect(document.documentElement.lang).toBe("en");
  });

  it("login still succeeds when the locale fetch throws", async () => {
    vi.spyOn(mockClient, "getMe").mockRejectedValue(new Error("401"));
    vi.spyOn(mockClient, "login").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("network")));

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.isPending).toBe(false));

    await act(async () => {
      await result.current.login("admin", "pw");
    });

    expect(result.current.user?.username).toBe("admin");
  });

  it("logout clears the user", async () => {
    vi.spyOn(mockClient, "getMe").mockResolvedValue({
      username: "admin",
      isTempPassword: false,
    });
    const logoutSpy = vi.spyOn(mockClient, "logout").mockResolvedValue(undefined);

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.user?.username).toBe("admin"));

    await act(async () => {
      await result.current.logout();
    });

    expect(logoutSpy).toHaveBeenCalled();
    expect(result.current.user).toBeNull();
  });

  it("changePassword refreshes the user (clearing isTempPassword)", async () => {
    vi.spyOn(mockClient, "getMe")
      .mockResolvedValueOnce({ username: "admin", isTempPassword: true })
      .mockResolvedValueOnce({ username: "admin", isTempPassword: false });
    vi.spyOn(mockClient, "changePassword").mockResolvedValue(undefined);

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.user?.isTempPassword).toBe(true));

    await act(async () => {
      await result.current.changePassword("old", "newpass");
    });

    expect(result.current.user?.isTempPassword).toBe(false);
  });

  it("changePassword clears the user when the refresh fails", async () => {
    vi.spyOn(mockClient, "getMe")
      .mockResolvedValueOnce({ username: "admin", isTempPassword: true })
      .mockRejectedValueOnce(new Error("boom"));
    vi.spyOn(mockClient, "changePassword").mockResolvedValue(undefined);

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.user).not.toBeNull());

    await act(async () => {
      await result.current.changePassword("old", "newpass");
    });

    expect(result.current.user).toBeNull();
  });
});
