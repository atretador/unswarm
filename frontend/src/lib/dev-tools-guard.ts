/**
 * Guard against Chrome DevTools / React 19 internal instrumentation noise.
 *
 * Two known issues (neither fixable from application code):
 *
 * 1. Chrome DevTools' embedded web-vitals library crashes during SPA navigation
 *    when performance entries are cleared between observer callbacks.
 *    `et.reportAllChanges` → `Cannot read properties of undefined (reading 'startTime')`
 *    Tracked: https://github.com/GoogleChrome/web-vitals/issues/792
 *
 * 2. React 19's dev-only Component Performance Tracks can emit negative timestamps
 *    when notFound()/redirect() interrupts rendering.
 *    `Failed to execute 'measure' on 'Performance': 'X' cannot have a negative time stamp`
 *    Tracked: https://github.com/vercel/next.js/issues/86060
 *
 * This module is safe to import unconditionally — the guards only activate in development.
 */
export function installDevToolsGuard(): void {
  if (import.meta.env.MODE !== "development") return;

  // Guard 1: Swallow the Chrome DevTools web-vitals reportAllChanges crash
  const originalError = console.error;
  console.error = (...args: unknown[]) => {
    const msg = args.map(String).join(" ");
    if (
      msg.includes("reportAllChanges") &&
      msg.includes("startTime")
    ) {
      return; // Chrome DevTools instrumentation noise
    }
    originalError.apply(console, args);
  };

  // Guard 2: Swallow React dev-track negative timestamp errors from performance.measure
  const originalMeasure = performance.measure.bind(performance);
  performance.measure = ((...args: Parameters<typeof originalMeasure>) => {
    try {
      return originalMeasure(...args);
    } catch (e) {
      if (
        e instanceof DOMException &&
        e.name === "SyntaxError" &&
        e.message.includes("negative time stamp")
      ) {
        return undefined as unknown as PerformanceMeasure;
      }
      throw e;
    }
  }) as typeof performance.measure;
}
