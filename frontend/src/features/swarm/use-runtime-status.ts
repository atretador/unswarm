// SSE hook for /api/runtime-status/stream: real-time container/script status updates.
//
// Connection lifecycle:
// - "connecting" → first attempt in flight
// - "open"       → streaming
// - "reconnecting" → connection dropped after having opened; retrying with
//   exponential backoff (1s → 30s cap)
// - "unavailable" → all initial connection attempts failed (up to 3 with
//   backoff); the UI disables the stream but allows retrying

import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { BASE_URL } from "../../lib/api/httpClient";

export type RuntimeStatusStatus =
  | "off"
  | "connecting"
  | "open"
  | "reconnecting"
  | "unavailable";

export interface RuntimeStatusEvent {
  agentName: string;
  containers: Array<{
    id: string;
    name?: string;
    status: string;
    port?: number;
  }>;
  scripts: Array<{
    path?: string;
    status: string;
    registrationId?: string;
    port: number;
  }>;
  timestamp: string;
}

function runtimeStatusSSEUrl(): string {
  const base = BASE_URL || window.location.origin;
  return `${base}/api/runtime-status/stream`;
}

/**
 * Connects to the runtime status SSE stream and invalidates
 * React Query caches when status changes arrive.
 */
export function useRuntimeStatus(enabled: boolean): RuntimeStatusStatus {
  const [status, setStatus] = useState<RuntimeStatusStatus>("off");
  const queryClient = useQueryClient();
  const queryClientRef = useRef(queryClient);
  queryClientRef.current = queryClient;

  useEffect(() => {
    if (!enabled) {
      setStatus("off");
      return;
    }

    let es: EventSource | null = null;
    let disposed = false;
    let everOpened = false;
    let attempts = 0;
    let reconnectTimer: number | undefined;

    const connect = () => {
      if (disposed) return;
      setStatus(everOpened ? "reconnecting" : "connecting");
      try {
        es = new EventSource(runtimeStatusSSEUrl());
      } catch {
        setStatus("unavailable");
        return;
      }

      es.onopen = () => {
        if (disposed) return;
        everOpened = true;
        attempts = 0;
        setStatus("open");
      };

      es.onmessage = (event) => {
        if (disposed || typeof event.data !== "string") return;
        try {
          JSON.parse(event.data) as RuntimeStatusEvent;
          // Invalidate relevant queries so React Query refetches with fresh data
          queryClientRef.current.invalidateQueries({ queryKey: ["agents"] });
          queryClientRef.current.invalidateQueries({ queryKey: ["registered-containers"] });
        } catch {
          // ignore malformed frames
        }
      };

      es.onerror = () => {
        if (disposed) return;
        es?.close();
        es = null;
        if (!everOpened) {
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
      es?.close();
    };
  }, [enabled]);

  return status;
}
