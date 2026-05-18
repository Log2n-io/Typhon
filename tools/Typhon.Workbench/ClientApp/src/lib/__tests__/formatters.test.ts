import { describe, expect, it, vi, afterEach } from 'vitest';
import { formatDateTime, formatFileSize, formatRelativeAge } from '../formatters';

describe('formatFileSize', () => {
  it('picks the unit by magnitude (B / KiB / MiB / GiB)', () => {
    expect(formatFileSize(0)).toBe('0 B');
    expect(formatFileSize(512)).toBe('512 B');
    expect(formatFileSize(1024)).toBe('1.0 KiB');
    expect(formatFileSize(1536)).toBe('1.5 KiB');
    expect(formatFileSize(20 * 1024)).toBe('20 KiB');
    expect(formatFileSize(5 * 1024 * 1024)).toBe('5.0 MiB');
    expect(formatFileSize(3 * 1024 * 1024 * 1024)).toBe('3.00 GiB');
  });

  it('accepts orval string scalars and returns empty for null/negative', () => {
    expect(formatFileSize('2048')).toBe('2.0 KiB');
    expect(formatFileSize(null)).toBe('');
    expect(formatFileSize(undefined)).toBe('');
    expect(formatFileSize(-1)).toBe('');
  });
});

describe('formatDateTime', () => {
  it('formats an ISO string as local YYYY-MM-DD HH:MM', () => {
    expect(formatDateTime('2026-05-17T08:05:00')).toBe('2026-05-17 08:05');
  });

  it('returns empty for null/garbage input', () => {
    expect(formatDateTime(null)).toBe('');
    expect(formatDateTime('not-a-date')).toBe('');
  });
});

describe('formatRelativeAge', () => {
  afterEach(() => vi.useRealTimers());

  it('picks the coarsest natural unit — minutes, hours, days, then months', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-05-17T12:00:00Z'));
    expect(formatRelativeAge('2026-05-17T11:59:30Z')).toBe('just now');
    expect(formatRelativeAge('2026-05-17T11:59:00Z')).toBe('1 minute');
    expect(formatRelativeAge('2026-05-17T11:35:00Z')).toBe('25 minutes');
    expect(formatRelativeAge('2026-05-17T11:00:00Z')).toBe('1 hour');
    expect(formatRelativeAge('2026-05-17T09:00:00Z')).toBe('3 hours');
    expect(formatRelativeAge('2026-05-16T09:00:00Z')).toBe('1 day');
    expect(formatRelativeAge('2026-05-04T12:00:00Z')).toBe('13 days');
    expect(formatRelativeAge('2026-02-01T12:00:00Z')).toBe('3 months');
  });

  it('returns empty for null/garbage input', () => {
    expect(formatRelativeAge(null)).toBe('');
    expect(formatRelativeAge('nope')).toBe('');
  });
});
