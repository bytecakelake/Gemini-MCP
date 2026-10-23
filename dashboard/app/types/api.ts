// 대시보드 서버 관리 API(/api/admin, dashboard/server) 응답 타입.

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: number;
  expiresAt: number | null;
  disabled: boolean;
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
    errorRate: number;
    avgMs: number;
    p50Ms: number;
    p95Ms: number;
    p99Ms: number;
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
  dashboard: {
    startedAt: number;
    uptimeSeconds: number;
    logRetentionDays: number;
  };
  upstreams: Upstream[];
  keys: { total: number; active: number; disabled: number; expired: number };
}

export interface MediaItem {
  /** 객체 이름 (gemini://media/<name>) */
  name: string;
  size: number;
  mimeType: string;
  /** 저장 시각 (ms) */
  lastModified: number;
  tool?: string;
  model?: string;
  /** 생성 지시문 (S3 메타데이터 한도 때문에 길면 잘려 있음) */
  prompt?: string;
  /** 요청한 API 키의 컨슈머 id (게이트웨이를 거치지 않았으면 없음) */
  consumer?: string;
  /** 삭제된 키면 null */
  keyName: string | null;
  interactionId?: string;
}

export interface MediaPage {
  items: MediaItem[];
  nextCursor: string | null;
}

export interface MediaStats {
  count: number;
  bytes: number;
  store: string;
}

export interface ToolInfo {
  name: string;
  title?: string;
  description?: string;
}
