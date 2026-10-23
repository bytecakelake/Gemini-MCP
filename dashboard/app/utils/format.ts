const numberFormat = new Intl.NumberFormat("ko-KR");
const dateTimeFormat = new Intl.DateTimeFormat("ko-KR", {
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});
const relativeFormat = new Intl.RelativeTimeFormat("ko-KR", { numeric: "auto" });

export const formatNumber = (n: number) => numberFormat.format(n);

export function formatCompact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 10_000) return `${Math.round(n / 1000)}k`;
  if (n >= 1_000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

export function formatMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)}s`;
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
}

export function formatDuration(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d) return `${d}일 ${h}시간`;
  if (h) return `${h}시간 ${m}분`;
  return `${m}분 ${seconds % 60}초`;
}

export const formatDateTime = (ts: number) => dateTimeFormat.format(ts);

export function formatRelative(ts: number | null, now = Date.now()): string {
  if (!ts) return "—";
  const diff = (ts - now) / 1000;
  const abs = Math.abs(diff);
  if (abs < 60) return relativeFormat.format(Math.round(diff), "second");
  if (abs < 3600) return relativeFormat.format(Math.round(diff / 60), "minute");
  if (abs < 86400) return relativeFormat.format(Math.round(diff / 3600), "hour");
  return relativeFormat.format(Math.round(diff / 86400), "day");
}

export const formatPercent = (ratio: number) => `${(ratio * 100).toFixed(ratio > 0 && ratio < 0.01 ? 2 : 1)}%`;

export function formatStep(ms: number): string {
  return ms >= 3_600_000 ? `${ms / 3_600_000}시간` : `${ms / 60_000}분`;
}
