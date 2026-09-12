import { lazy } from "react";
import type { ComponentType, LazyExoticComponent } from "react";

export interface LazyWithRetryOptions {
  /** Number of retries after the initial attempt (default: 3). */
  retries?: number;
  /** Delay before retry `attempt` (1-based), in milliseconds. */
  retryDelayMs?: (attempt: number) => number;
}

/**
 * `React.lazy` with bounded in-place retries.
 *
 * React caches the rejected import promise on the lazy component
 * (facebook/react#14254), so a single transient chunk failure — a 429 from
 * the API rate limiter, a flaky network, an asset swapped by a deploy — would
 * otherwise break the route for the rest of the session: the router updates
 * the URL, but the page never renders until a full reload. Retrying inside
 * the loader keeps the same lazy instance pending instead of caching a
 * rejection; persistent failures still reject so the ErrorBoundary can show
 * a reload affordance.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function lazyWithRetry<T extends ComponentType<any>>(
  importer: () => Promise<{ default: T }>,
  options: LazyWithRetryOptions = {},
): LazyExoticComponent<T> {
  const {
    retries = 3,
    retryDelayMs = (attempt) => 500 * 2 ** (attempt - 1),
  } = options;

  return lazy(() => {
    const load = (attempt: number): Promise<{ default: T }> =>
      importer().catch((error: unknown) => {
        if (attempt > retries) throw error;
        return new Promise<void>((resolve) => {
          setTimeout(resolve, retryDelayMs(attempt));
        }).then(() => load(attempt + 1));
      });

    return load(1);
  });
}
