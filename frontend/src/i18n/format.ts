import i18n from './index';

export function formatCurrency(n: number, opts?: Intl.NumberFormatOptions): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.NumberFormat(locale, { style: 'currency', currency: locale === 'pt-BR' ? 'BRL' : 'USD', ...opts }).format(n);
}

export function formatNumber(n: number, opts?: Intl.NumberFormatOptions): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.NumberFormat(locale, opts).format(n);
}

export function formatCompact(n: number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(n);
}

export function formatMs(ms: number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  if (ms >= 1000) {
    return new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(ms / 1000) + ' ' + i18n.t('units.s', { ns: 'common' });
  }
  return Math.round(ms) + ' ' + i18n.t('units.ms', { ns: 'common' });
}

export function formatTokensPerSec(v: number): string {
  if (!v || v <= 0) return i18n.t('na', { ns: 'common' });
  return `${v.toFixed(1)} ${i18n.t('units.tokPerSec', { ns: 'common' })}`;
}

export function formatLatency(ms: number): string {
  if (!ms || ms <= 0) return '—';
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}${i18n.t('units.s', { ns: 'common' })}`;
  return `${Math.round(ms)}${i18n.t('units.ms', { ns: 'common' })}`;
}

export function formatTokens(n: number | undefined): string {
  if (!n || n <= 0) return i18n.t('na', { ns: 'common' });
  return `${new Intl.NumberFormat(i18n.language).format(n)} ${i18n.t('units.tok', { ns: 'common' })}`;
}

export function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  if (d > 0) return `${d}${i18n.t('units.d', { ns: 'common' })} ${h}${i18n.t('units.h', { ns: 'common' })}`;
  return `${h}${i18n.t('units.h', { ns: 'common' })}`;
}

export function formatBytes(bytes: number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  const fmt = (n: number) => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(n);
  if (bytes >= 1_073_741_824) return `${fmt(bytes / 1_073_741_824)} ${i18n.t('units.bytes.GB', { ns: 'common' })}`;
  if (bytes >= 1_048_576) return `${fmt(bytes / 1_048_576)} ${i18n.t('units.bytes.MB', { ns: 'common' })}`;
  if (bytes >= 1024) return `${fmt(bytes / 1024)} ${i18n.t('units.bytes.KB', { ns: 'common' })}`;
  return `${bytes} ${i18n.t('units.bytes.B', { ns: 'common' })}`;
}

export function formatPercent(n: number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.NumberFormat(locale, { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(n / 100);
}

export function formatDate(d: Date | string | number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(d));
}

export function formatDateTime(d: Date | string | number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(d));
}

export function formatRelativeTime(d: Date | string | number): string {
  const locale = i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US';
  const now = Date.now();
  const then = new Date(d).getTime();
  const diffSec = Math.floor((now - then) / 1000);

  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  if (diffSec < 60) return rtf.format(-diffSec, 'second');
  if (diffSec < 3600) return rtf.format(-Math.floor(diffSec / 60), 'minute');
  if (diffSec < 86400) return rtf.format(-Math.floor(diffSec / 3600), 'hour');
  return rtf.format(-Math.floor(diffSec / 86400), 'day');
}
