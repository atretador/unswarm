// Live tail for /ws/metrics: one JSON usage event per recorded request.
//
// Connection lifecycle:
// - "connecting" → first attempt in flight
// - "open"       → streaming
// - "reconnecting" → socket dropped after having opened; retrying with
//   exponential backoff (1s → 30s cap) while the toggle stays on
// - "unavailable" → the first connection attempt failed outright; the UI
//   disables the toggle rather than spinning forever

import { useEffect, useRef, useState } from "react";
import { metricsWsUrl } from "./metrics-api";
import type { UsageRecordResponse } from "../../lib/api/types";

export type LiveTailStatus =
  | "off"
  | "connecting"
  | "open"
  | "reconnecting"
  | "unavailable";

export function useLiveTail(
  enabled: boolean,
  onEvent: (event: UsageRecordResponse) => void,
): LiveTailStatus {
  const [status, setStatus] = useState<LiveTailStatus>("off");
  // Latest onEvent without re-running the connection effect on re-renders.
  const onEventRef = useRef(onEvent);
  onEventRef.current = onEvent;

  useEffect(() => {
    if (!enabled) {
      setStatus("off");
      return;
    }

    let ws: WebSocket | null = null;
    let disposed = false;
    let everOpened = false;
    let attempts = 0;
    let reconnectTimer: number | undefined;

    const connect = () => {
      if (disposed) return;
      setStatus(everOpened ? "reconnecting" : "connecting");
      try {
        ws = new WebSocket(metricsWsUrl());
      } catch {
        setStatus("unavailable");
        return;
      }

      ws.onopen = () => {
        if (disposed) return;
        everOpened = true;
        attempts = 0;
        setStatus("open");
      };

      ws.onmessage = (messageEvent) => {
        if (disposed || typeof messageEvent.data !== "string") return;
        try {
          onEventRef.current(JSON.parse(messageEvent.data) as UsageRecordResponse);
        } catch {
          // ignore malformed frames
        }
      };

      ws.onerror = () => {
        // State transitions are driven by onclose, which always follows.
      };

      ws.onclose = () => {
        if (disposed) return;
        ws = null;
        if (!everOpened) {
          // Never managed to connect — give up and let the UI disable the toggle.
          setStatus("unavailable");
          return;
        }
        attempts += 1;
        setStatus("reconnecting");
        const delay = Math.min(30_000, 1_000 * 2 ** Math.min(attempts, 5));
        reconnectTimer = window.setTimeout(connect, delay);
      };
    };

    connect();

    return () => {
      disposed = true;
      if (reconnectTimer !== undefined) window.clearTimeout(reconnectTimer);
      ws?.close();
    };
  }, [enabled]);

  return status;
}
