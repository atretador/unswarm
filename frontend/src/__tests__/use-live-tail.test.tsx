import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useLiveTail } from "../features/metrics/use-live-tail";

type OpenHandler = (() => void) | null;
type MessageHandler = ((e: { data: unknown }) => void) | null;

class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  static throwOnConstruct = false;

  url: string;
  onopen: OpenHandler = null;
  onmessage: MessageHandler = null;
  onclose: OpenHandler = null;
  onerror: OpenHandler = null;
  closed = false;

  constructor(url: string) {
    if (FakeWebSocket.throwOnConstruct) throw new Error("no websocket");
    this.url = url;
    FakeWebSocket.instances.push(this);
  }

  close() {
    this.closed = true;
  }
}

const latest = () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1];

beforeEach(() => {
  FakeWebSocket.instances = [];
  FakeWebSocket.throwOnConstruct = false;
  vi.useFakeTimers();
  vi.stubGlobal("WebSocket", FakeWebSocket as unknown as typeof WebSocket);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("useLiveTail", () => {
  it("stays 'off' and opens no socket when disabled", () => {
    const { result } = renderHook(() => useLiveTail(false, () => {}));
    expect(result.current).toBe("off");
    expect(FakeWebSocket.instances).toHaveLength(0);
  });

  it("goes connecting -> open and targets the metrics ws endpoint", () => {
    const { result } = renderHook(() => useLiveTail(true, () => {}));
    expect(result.current).toBe("connecting");
    expect(FakeWebSocket.instances).toHaveLength(1);
    expect(latest().url).toContain("/ws/metrics");
    expect(latest().url.startsWith("ws:")).toBe(true);

    act(() => latest().onopen?.());
    expect(result.current).toBe("open");
  });

  it("forwards parsed frames and ignores malformed / non-string frames", () => {
    const onEvent = vi.fn();
    const { result } = renderHook(() => useLiveTail(true, onEvent));
    act(() => latest().onopen?.());
    expect(result.current).toBe("open");

    act(() => latest().onmessage?.({ data: JSON.stringify({ id: "r1" }) }));
    expect(onEvent).toHaveBeenCalledWith({ id: "r1" });

    act(() => latest().onmessage?.({ data: "not-json" }));
    act(() => latest().onmessage?.({ data: { id: "nope" } }));
    expect(onEvent).toHaveBeenCalledTimes(1);
  });

  it("reconnects with backoff after an open socket drops", () => {
    const { result } = renderHook(() => useLiveTail(true, () => {}));
    act(() => latest().onopen?.());

    act(() => latest().onclose?.());
    expect(result.current).toBe("reconnecting");

    act(() => vi.advanceTimersByTime(2_000));
    expect(FakeWebSocket.instances).toHaveLength(2);

    act(() => latest().onopen?.());
    expect(result.current).toBe("open");
  });

  it("gives up as 'unavailable' after three failed initial attempts", () => {
    const { result } = renderHook(() => useLiveTail(true, () => {}));
    expect(result.current).toBe("connecting");

    // attempt 1 fails -> retry scheduled
    act(() => latest().onclose?.());
    expect(result.current).toBe("connecting");
    act(() => vi.advanceTimersByTime(2_000));
    expect(FakeWebSocket.instances).toHaveLength(2);

    // attempt 2 fails -> retry scheduled
    act(() => latest().onclose?.());
    act(() => vi.advanceTimersByTime(4_000));
    expect(FakeWebSocket.instances).toHaveLength(3);

    // attempt 3 fails -> unavailable
    act(() => latest().onclose?.());
    expect(result.current).toBe("unavailable");
  });

  it("reports 'unavailable' when the WebSocket constructor throws", () => {
    FakeWebSocket.throwOnConstruct = true;
    const { result } = renderHook(() => useLiveTail(true, () => {}));
    expect(result.current).toBe("unavailable");
  });

  it("closes the socket on unmount", () => {
    const { unmount } = renderHook(() => useLiveTail(true, () => {}));
    const ws = latest();
    unmount();
    expect(ws.closed).toBe(true);
  });
});
