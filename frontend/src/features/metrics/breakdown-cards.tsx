// Presentational cards for Phase-2 metrics depth: latency distribution
// histogram and per-API-key usage attribution. Queries live in index.tsx so
// they participate in auto-refresh and the keyboard-refresh shortcut.

import { Activity, KeyRound } from "lucide-react";
import { Card } from "../../components/ui";
import type { ApiKeyUsageRow, LatencyBand } from "./metrics-api";
import { LatencyBandsChart } from "./charts";
import { formatTokens } from "./format";

export function LatencyBandsCard({
  bands,
  loading,
}: {
  bands: LatencyBand[] | undefined;
  loading: boolean;
}) {
  const total = (bands ?? []).reduce((sum, b) => sum + b.count, 0);
  return (
    <Card padding="lg">
      <div className="flex items-center justify-between mb-4">
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          Latency distribution
        </p>
        {total > 0 && (
          <span className="inline-flex items-center gap-1 text-xs text-[var(--color-text-muted)]">
            <Activity className="size-3" />
            {total.toLocaleString()} request{total === 1 ? "" : "s"}
          </span>
        )}
      </div>
      {loading && (
        <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
          Loading…
        </p>
      )}
      {!loading && total === 0 && (
        <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
          No requests in this window.
        </p>
      )}
      {!loading && total > 0 && <LatencyBandsChart bands={bands ?? []} />}
    </Card>
  );
}

export function ApiKeysCard({
  rows,
  loading,
}: {
  rows: ApiKeyUsageRow[] | undefined;
  loading: boolean;
}) {
  return (
    <Card padding="lg">
      <div className="flex items-center justify-between mb-4">
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          Usage by API key
        </p>
        <KeyRound className="size-3.5 text-[var(--color-text-muted)]" />
      </div>

      {loading && (
        <p className="text-sm text-[var(--color-text-muted)] py-6 text-center">
          Loading…
        </p>
      )}

      {!loading && (rows?.length ?? 0) === 0 && (
        <p className="text-sm text-[var(--color-text-muted)] py-6 text-center">
          No API-key usage in this window.
        </p>
      )}

      {(rows?.length ?? 0) > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[var(--color-border)]">
                <th className="text-left py-2 pr-4 text-xs font-medium text-[var(--color-text-muted)]">
                  Key
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)]">
                  Requests
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                  Tokens In
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                  Tokens Out
                </th>
                <th className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                  Cached
                </th>
              </tr>
            </thead>
            <tbody>
              {rows?.map((row) => (
                <tr
                  key={row.apiKeyId}
                  className="border-b border-[var(--color-border)] last:border-0"
                >
                  <td
                    className="py-2.5 pr-4 font-medium text-[var(--color-text-heading)] max-w-[200px] truncate"
                    title={row.keyName}
                  >
                    {row.keyName || (
                      <span className="text-[var(--color-text-muted)] italic">
                        unnamed key
                      </span>
                    )}
                  </td>
                  <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)]">
                    {row.requestCount.toLocaleString()}
                  </td>
                  <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                    {formatTokens(row.promptTokens)}
                  </td>
                  <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                    {formatTokens(row.completionTokens)}
                  </td>
                  <td className="py-2.5 pl-4 text-right font-mono text-[var(--color-status-warning)] hidden md:table-cell">
                    {row.cachedTokens > 0 ? formatTokens(row.cachedTokens) : "\u2014"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
