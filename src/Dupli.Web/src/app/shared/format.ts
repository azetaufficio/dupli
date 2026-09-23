import { Pipe, PipeTransform } from '@angular/core';

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

export function formatBytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  let n = value;
  let unit = 0;
  while (Math.abs(n) >= 1024 && unit < UNITS.length - 1) {
    n /= 1024;
    unit++;
  }
  return `${n.toFixed(unit === 0 || n >= 100 ? 0 : 1)} ${UNITS[unit]}`;
}

export function formatRelative(value: string | null | undefined, now = Date.now()): string {
  if (!value) return 'never';
  const seconds = Math.round((new Date(value).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  if (abs < 60) return rtf.format(seconds, 'second');
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour');
  return rtf.format(Math.round(seconds / 86400), 'day');
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  return new Date(value).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
}

export function formatDuration(
  from: string | null | undefined,
  to: string | null | undefined,
): string {
  if (!from || !to) return '—';
  const seconds = Math.max(
    0,
    Math.round((new Date(to).getTime() - new Date(from).getTime()) / 1000),
  );
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatBytes(value);
  }
}

/** Impure on purpose: "2 minutes ago" must keep moving when the view refreshes. */
@Pipe({ name: 'relative', pure: false })
export class RelativeTimePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return formatRelative(value);
  }
}

@Pipe({ name: 'datetime' })
export class DateTimePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return formatDateTime(value);
  }
}

@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(from: string | null | undefined, to: string | null | undefined): string {
    return formatDuration(from, to);
  }
}
