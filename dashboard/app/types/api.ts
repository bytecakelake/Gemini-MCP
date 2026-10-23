// 게이트웨이 관리 API(/api/admin) 응답 타입. gateway/src 의 정의와 맞춰 둡니다.

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: number;
  expiresAt: number | null;
  disabled: boolean;
  allowedTools: string[] | null;
  lastUsedAt: number | null;
  requestCount: number;
  status: "active" | "disabled" | "expired";
}

export interface IssuedKey {
  key: ApiKey;
  secret: string;
}

export type LogLevel = "info" | "warn" | "error";
export type LogKind = "request" | "audit" | "system";

export interface LogEntry {
  id: string;
  ts: number;
  kind: LogKind;
  level: LogLevel;
  message: string;
  ip?: string;
  keyId?: string;
  keyName?: string;
  method?: string;
  status?: number;
  durationMs?: number;
  rpcMethod?: string;
  tool?: string;
  toolError?: boolean;
  bytesIn?: number;
  bytesOut?: number;
  userAgent?: string;
}

export type RangeName = "1h" | "24h" | "7d";

export interface SeriesPoint {
  t: number;
  requests: number;
  errors: number;
  avgMs: number;
  p95Ms: number;
}

export interface Metrics {
  range: RangeName;
  step: number;
  series: SeriesPoint[];
  totals: {
    requests: number;
    errors: number;
    toolErrors: number;
    errorRate: number;
    avgMs: number;
    p50Ms: number;
    p95Ms: number;
    p99Ms: number;
    maxMs: number;
    bytesOut: number;
  };
  latencyHistogram: { le: number | null; count: number }[];
  tools: { name: string; count: number; errors: number }[];
  keys: { id: string; name: string; count: number }[];
}

export interface Upstream {
  name: string;
  url: string;
  healthy: boolean | null;
  latencyMs: number | null;
  detail: string;
  checkedAt: number | null;
  since: number;
  history: { t: number; ok: boolean; ms: number }[];
}

export interface Status {
  gateway: {
    version: string;
    deno: string;
    startedAt: number;
    uptimeSeconds: number;
    memory: { rss: number; heapUsed: number; heapTotal: number };
    inflight: number;
    logRetentionDays: number;
  };
  upstreams: Upstream[];
  keys: { total: number; active: number; disabled: number; expired: number };
}

export interface ToolInfo {
  name: string;
  title?: string;
  description?: string;
}
