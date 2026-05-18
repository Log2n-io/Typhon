/**
 * Shared display formatters — file sizes, dates, relative ages. Kept dependency-free (no date
 * library) so they stay cheap to import anywhere.
 */

/** Coerce an orval `number | string | null` scalar to a finite number, or `null`. */
function asNumber(v: number | string | null | undefined): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

/**
 * Human-readable file size in binary units (B / KiB / MiB / GiB), unit chosen by magnitude.
 * Returns an empty string for a null/negative input (e.g. a directory entry).
 */
export function formatFileSize(bytes: number | string | null | undefined): string {
  const n = asNumber(bytes);
  if (n == null || n < 0) return '';
  if (n < 1024) return `${n} B`;
  const kib = n / 1024;
  if (kib < 1024) return `${kib.toFixed(kib < 10 ? 1 : 0)} KiB`;
  const mib = kib / 1024;
  if (mib < 1024) return `${mib.toFixed(mib < 10 ? 1 : 0)} MiB`;
  const gib = mib / 1024;
  return `${gib.toFixed(gib < 10 ? 2 : 1)} GiB`;
}

/**
 * Compact absolute date-time (`YYYY-MM-DD HH:MM`, local) for an ISO-8601 string. Empty string on
 * an unparseable input.
 */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (x: number) => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/**
 * Approximate relative age of an ISO-8601 timestamp, on an adaptive scale: `just now`, `N minutes`,
 * `N hours`, `N days`, or `N months`. Picks the coarsest unit that still reads naturally — minutes
 * under an hour, hours under a day, days up to ~2 months, months beyond. Always floored (an
 * approximation, never a precise duration). Empty string on an unparseable input.
 */
export function formatRelativeAge(iso: string | null | undefined): string {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  if (!Number.isFinite(then)) return '';
  const ms = Date.now() - then;
  if (ms < 60_000) return 'just now';
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 60) return minutes === 1 ? '1 minute' : `${minutes} minutes`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return hours === 1 ? '1 hour' : `${hours} hours`;
  const days = Math.floor(hours / 24);
  if (days < 60) return days === 1 ? '1 day' : `${days} days`;
  const months = Math.floor(days / 30);
  return months === 1 ? '1 month' : `${months} months`;
}
