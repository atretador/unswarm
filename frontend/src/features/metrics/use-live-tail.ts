// Live tail for /ws/metrics: one JSON usage event per recorded request.
//
// Connection lifecycle:
// - "connecting" → first attempt in flight
// - "open"       → streaming
// - "reconnecting" → socket dropped after having opened; retrying with
//   exponential backoff (1s → 30s cap) while the toggle stays on
// - "unavailable" → all initial connection attempts failed (up to 3 with
//   backoff); the UI disables live mode but allows retrying

import { useEffect, useRef, useState } from "react";
import type { UsageRecordResponse } from "../../lib/api/types";
import { BASE_URL } from "../../lib/api/httpClient";

/**
 * WebSocket URL for `/ws/metrics`, derived from the same base-URL logic as
 * HTTP calls: VITE_API_URL when set (http→ws rewrite), otherwise same-origin.
 */
function metricsWsUrl(): string {
  const base = BASE_URL || window.location.origin;
  const url = new URL(`${base}/ws/metrics`, window.location.origin);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString();
}

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
          // Never managed to connect — retry a few times with backoff
          // before giving up.
          attempts += 1;
          if (attempts < 3) {
            setStatus("connecting");
            const delay = Math.min(30_000, 1_000 * 2 ** attempts);
            reconnectTimer = window.setTimeout(connect, delay);
          } else {
            setStatus("unavailable");
          }
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
