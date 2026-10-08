import { describe, it, expect, beforeEach } from "vitest";
import i18n from "../i18n";
import {
  formatCurrency,
  formatNumber,
  formatCompact,
  formatMs,
  formatTokensPerSec,
  formatLatency,
  formatTokens,
  formatUptime,
  formatBytes,
  formatPercent,
  formatDate,
  formatDateTime,
  formatRelativeTime,
} from "../i18n/format";

beforeEach(async () => {
  await i18n.changeLanguage("en");
});

describe("i18n format helpers (en)", () => {
  it("formats currency in USD", () => {
    expect(formatCurrency(1234.5)).toBe("$1,234.50");
  });

  it("formats numbers and compact notation", () => {
    expect(formatNumber(1234567)).toBe("1,234,567");
    expect(formatCompact(1500)).toBe("1.5K");
  });

  it("formats milliseconds below and above one second", () => {
    expect(formatMs(250)).toBe("250 ms");
    expect(formatMs(1500)).toBe("1.5 s");
  });

  it("formats tokens per second, falling back to n/a", () => {
    expect(formatTokensPerSec(42.34)).toBe("42.3 tok/s");
    expect(formatTokensPerSec(0)).toBe("n/a");
    expect(formatTokensPerSec(-3)).toBe("n/a");
  });

  it("formats latency with an em dash fallback", () => {
    expect(formatLatency(0)).toBe("—");
    expect(formatLatency(250)).toBe("250ms");
    expect(formatLatency(1500)).toBe("1.5s");
  });

  it("formats token counts, falling back to n/a", () => {
    expect(formatTokens(undefined)).toBe("n/a");
    expect(formatTokens(0)).toBe("n/a");
    expect(formatTokens(12345)).toBe("12,345 tok");
  });

  it("formats uptime with and without days", () => {
    expect(formatUptime(3600)).toBe("1h");
    expect(formatUptime(90000)).toBe("1d 1h");
  });

  it("formats bytes across all units", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(2048)).toBe("2 KB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5 MB");
    expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe("3 GB");
  });

  it("formats percentages", () => {
    expect(formatPercent(12.5)).toBe("12.5%");
  });

  it("formats dates and date-times", () => {
    expect(formatDate("2024-01-15T12:00:00Z")).toContain("2024");
    expect(formatDateTime("2024-01-15T12:00:00Z")).toContain("2024");
  });

  it("formats relative time across units", () => {
    const now = Date.now();
    expect(formatRelativeTime(now - 5_000)).toMatch(/second/);
    expect(formatRelativeTime(now - 5 * 60_000)).toMatch(/minute/);
    expect(formatRelativeTime(now - 5 * 3600_000)).toMatch(/hour/);
    expect(formatRelativeTime(now - 3 * 86400_000)).toMatch(/day/);
  });
});

describe("i18n format helpers (pt-BR)", () => {
  beforeEach(async () => {
    await i18n.changeLanguage("pt-BR");
  });

  it("formats currency in BRL", () => {
    expect(formatCurrency(1234.5)).toContain("R$");
  });

  it("still formats bytes/uptime using locale-independent units", () => {
    expect(formatBytes(2048)).toBe("2 KB");
    expect(formatUptime(3600)).toBe("1h");
  });
});
