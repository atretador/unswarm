// Shared formatting helpers for the Metrics page.

import {
  formatCompact,
  formatMs as i18nFormatMs,
  formatCurrency as i18nFormatCurrency,
  formatDateTime,
} from "../../i18n/format";

export function formatTokens(n: number): string {
  return formatCompact(n);
}

export function formatMs(ms: number): string {
  return i18nFormatMs(ms);
}

export function formatCurrency(n: number, opts?: Intl.NumberFormatOptions): string {
  return i18nFormatCurrency(n, opts);
}

export function formatTimestamp(iso: string): string {
  return formatDateTime(iso);
}
