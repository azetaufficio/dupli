import { formatBytes, formatDuration, formatRelative } from './format';

describe('format', () => {
  it('formats bytes with binary units', () => {
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(5 * 1024 ** 3)).toBe('5.0 GB');
  });

  it('formats durations', () => {
    expect(formatDuration('2026-01-01T00:00:00Z', '2026-01-01T00:00:42Z')).toBe('42s');
    expect(formatDuration('2026-01-01T00:00:00Z', '2026-01-01T01:05:00Z')).toBe('1h 5m');
    expect(formatDuration(null, '2026-01-01T00:00:00Z')).toBe('—');
  });

  it('formats relative times', () => {
    const now = new Date('2026-01-01T12:00:00Z').getTime();
    expect(formatRelative(null, now)).toBe('never');
    expect(formatRelative('2026-01-01T11:58:00Z', now)).toMatch(/2/);
  });
});
