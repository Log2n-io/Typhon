import { describe, it, expect } from 'vitest';
import { formatBytes } from '../formatBytes';

describe('formatBytes', () => {
  it('formats raw bytes below 1 KiB without a decimal', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1023)).toBe('1023 B');
  });

  it('uses IEC binary units (KiB/MiB/GiB/TiB), not decimal labels', () => {
    expect(formatBytes(2048)).toBe('2.0 KiB');
    expect(formatBytes(13_000_000)).toBe('12.40 MiB');
    expect(formatBytes(2 * 1024 * 1024 * 1024)).toBe('2.00 GiB');
    expect(formatBytes(3 * 1024 ** 4)).toBe('3.00 TiB');
  });
});
