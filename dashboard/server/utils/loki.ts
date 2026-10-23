/**
 * Grafana Loki 연동. APISIX loki-logger 가 보낸 요청 로그(job="apisix")와 대시보드가 남기는 관리/시스템 로그
 * (job="dashboard")를 조회하고, 요청 지표(요청 수, 오류, 지연 시간, 도구·키별 호출)를 LogQL 로 계산합니다.
 * 로그 한 건 = 요청 한 건이므로 집계가 정확합니다.
 */

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

interface RequestLine {
  msec?: string;
  method?: string;
  status?: string;
  latency?: string;
  consumer?: string;
  rpc?: string;
  tool?: string;
  ip?: string;
  ua?: string;
  bytes_in?: string;
  bytes_out?: string;
}

/** MCP 요청 로그. stateless 서버가 405 로 답하는 GET(SSE 스트림 시도)은 클라이언트의 정상 동작이라 지표에서 뺍니다. */
const REQUESTS = `{job="apisix"} | json | __error__="" | method != "GET" or status != "405"`;

/** 지연 시간 분포 구간 경계 (ms). 마지막 칸은 그 이상. */
const LATENCY_BOUNDS = [50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000, 60_000, 120_000];

const MINUTE = 60_000;
export const RANGES = {
  "1h": { span: 60 * MINUTE, step: MINUTE },
  "24h": { span: 24 * 60 * MINUTE, step: 15 * MINUTE },
  "7d": { span: 7 * 24 * 60 * MINUTE, step: 2 * 60 * MINUTE },
} as const;
export type RangeName = keyof typeof RANGES;

const ns = (ms: number) => `${BigInt(Math.round(ms)) * 1_000_000n}`;

async function loki<T>(path: string, params: Record<string, string | number>): Promise<T> {
  const query = new URLSearchParams(Object.entries(params).map(([k, v]) => [k, String(v)]));
  const res = await fetch(`${env.loki}/loki/api/v1/${path}?${query}`, { signal: AbortSignal.timeout(20_000) });
  const text = await res.text();
  if (!res.ok) throw new Error(`Loki ${path} → ${res.status}: ${text.slice(0, 300)}`);
  return JSON.parse(text).data as T;
}

type Vector = { metric: Record<string, string>; value: [number, string] }[];
type Matrix = { metric: Record<string, string>; values: [number, string][] }[];
type Streams = { stream: Record<string, string>; values: [string, string][] }[];

const num = (v: string | undefined) => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};

async function instant(query: string, time: number): Promise<Vector> {
  return (await loki<{ result: Vector }>("query", { query, time: ns(time) })).result;
}

async function scalar(query: string, time: number): Promise<number> {
  return num((await instant(query, time))[0]?.value[1]);
}

/** 관리/시스템 로그를 Loki 에 기록합니다. Loki 가 잠시 내려가 있어도 요청 처리는 막지 않습니다. */
export async function pushLog(kind: "audit" | "system", level: LogLevel, message: string, ip = ""): Promise<void> {
  try {
    await fetch(`${env.loki}/loki/api/v1/push`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        streams: [{
          stream: { job: "dashboard", kind, level },
          values: [[ns(Date.now()), JSON.stringify({ message, ip })]],
        }],
      }),
      signal: AbortSignal.timeout(5_000),
    });
  } catch (err) {
    console.error("Loki push failed:", err);
  }
}

// ---------- 로그 조회 ----------

export interface LogQuery {
  kind?: string;
  level?: string;
  keyId?: string;
  tool?: string;
  q?: string;
  /** 이 시각(ns 문자열)보다 오래된 항목부터 */
  before?: string;
  limit: number;
}

/** 검색어를 LogQL 원시 문자열(backtick) 안의 대소문자 무시 정규식으로 만듭니다. */
function lineFilter(q: string | undefined): string {
  if (!q?.trim()) return "";
  const escaped = q.trim().replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/`/g, "");
  return ` |~ \`(?i)${escaped}\``;
}

function quote(value: string) {
  return JSON.stringify(value);
}

function requestEntry(tsNs: string, line: string, names: Map<string, string>): LogEntry {
  let r: RequestLine = {};
  try {
    r = JSON.parse(line);
  } catch {
    // 형식이 다른 줄은 원문을 메시지로 보여 줍니다.
  }
  const status = num(r.status);
  const durationMs = Math.round(num(r.latency) * 1000);
  const probe = r.method === "GET" && status === 405;
  const level: LogLevel = status >= 500 ? "error" : status >= 400 && !probe ? "warn" : "info";
  const what = r.tool ? `${r.rpc} ${r.tool}` : r.rpc || (probe ? "GET (SSE 스트림 미지원)" : r.method ?? "");
  const message = status === 401
    ? "인증 실패: API 키 없음·잘못된 키·비활성/만료 키 → 401"
    : `${what} → ${status} ${durationMs}ms`;
  return {
    id: `${tsNs}-${line.length}`,
    ts: Number(BigInt(tsNs) / 1_000_000n),
    kind: "request",
    level,
    message: r.status ? message : line,
    ip: r.ip?.split(",")[0]?.trim() || undefined,
    keyId: r.consumer || undefined,
    keyName: r.consumer ? names.get(r.consumer) ?? r.consumer : undefined,
    method: r.method,
    status,
    durationMs,
    rpcMethod: r.rpc || undefined,
    tool: r.tool || undefined,
    bytesIn: num(r.bytes_in),
    bytesOut: num(r.bytes_out),
    userAgent: r.ua || undefined,
  };
}

function dashboardEntry(tsNs: string, line: string, stream: Record<string, string>): LogEntry {
  let body: { message?: string; ip?: string } = {};
  try {
    body = JSON.parse(line);
  } catch {
    body = { message: line };
  }
  return {
    id: `${tsNs}-${line.length}`,
    ts: Number(BigInt(tsNs) / 1_000_000n),
    kind: (stream.kind as LogKind) ?? "system",
    level: (stream.level as LogLevel) ?? "info",
    message: body.message ?? line,
    ip: body.ip || undefined,
  };
}

export async function queryLogs(query: LogQuery, names: Map<string, string>) {
  const endNs = query.before && /^\d+$/.test(query.before) ? `${BigInt(query.before) - 1n}` : ns(Date.now());
  const start = Date.now() - env.retentionDays * DAY_MS;
  const params = { start: ns(start), end: endNs, limit: query.limit, direction: "backward" };
  const filter = lineFilter(query.q);
  const tasks: Promise<LogEntry[]>[] = [];

  if (!query.kind || query.kind === "request") {
    let logql = `{job="apisix"}${filter} | json | __error__=""`;
    if (query.keyId) logql += ` | consumer=${quote(query.keyId)}`;
    if (query.tool) logql += ` | tool=${quote(query.tool)}`;
    if (query.level === "error") logql += ` | status >= 500`;
    if (query.level === "warn") logql += ` | status >= 400 | status < 500 | method != "GET" or status != "405"`;
    if (query.level === "info") logql += ` | status < 400 or (method = "GET" and status = "405")`;
    tasks.push(
      loki<{ result: Streams }>("query_range", { ...params, query: logql }).then((d) =>
        d.result.flatMap((s) => s.values.map(([t, line]) => requestEntry(t, line, names)))
      ),
    );
  }

  // 관리/시스템 로그에는 키·도구가 없으므로 그 필터가 있으면 조회하지 않습니다.
  if (query.kind !== "request" && !query.keyId && !query.tool) {
    const labels = [`job="dashboard"`];
    if (query.kind) labels.push(`kind=${quote(query.kind)}`);
    if (query.level) labels.push(`level=${quote(query.level)}`);
    tasks.push(
      loki<{ result: Streams }>("query_range", { ...params, query: `{${labels.join(",")}}${filter}` }).then((d) =>
        d.result.flatMap((s) => s.values.map(([t, line]) => dashboardEntry(t, line, s.stream)))
      ),
    );
  }

  const merged = (await Promise.all(tasks)).flat().sort((a, b) => b.ts - a.ts || (a.id < b.id ? 1 : -1));
  const items = merged.slice(0, query.limit);
  const last = items.at(-1);
  return {
    items,
    nextBefore: merged.length >= query.limit && last ? last.id.split("-")[0]! : null,
  };
}

// ---------- 지표 ----------

export async function metrics(range: RangeName) {
  const { span, step } = RANGES[range];
  const now = Date.now();
  const end = Math.floor(now / step) * step + step;
  const start = end - span;
  const w = `${step / 1000}s`;
  const all = `${span / 1000}s`;

  const series = async (query: string) => {
    const result = (await loki<{ result: Matrix }>("query_range", {
      query,
      start: ns(start + step),
      end: ns(end),
      step: `${step / 1000}s`,
    })).result;
    const map = new Map<number, number>();
    for (const s of result) for (const [t, v] of s.values) map.set(Math.round(t * 1000) - step, num(v));
    return map;
  };

  const [requests, errors, p95, avg] = await Promise.all([
    series(`sum(count_over_time(${REQUESTS} [${w}]))`),
    series(`sum(count_over_time(${REQUESTS} | status >= 400 [${w}]))`),
    series(`quantile_over_time(0.95, ${REQUESTS} | unwrap latency | __error__="" [${w}]) by (job)`),
    series(`avg_over_time(${REQUESTS} | unwrap latency | __error__="" [${w}]) by (job)`),
  ]);

  const latency = (q: number) =>
    scalar(`quantile_over_time(${q}, ${REQUESTS} | unwrap latency | __error__="" [${all}]) by (job)`, now);
  const [total, totalErrors, p50All, p95All, p99All, avgAll, bytesOut, tools, toolErrors, keys, cumulative] =
    await Promise.all([
      scalar(`sum(count_over_time(${REQUESTS} [${all}]))`, now),
      scalar(`sum(count_over_time(${REQUESTS} | status >= 400 [${all}]))`, now),
      latency(0.5),
      latency(0.95),
      latency(0.99),
      scalar(`avg_over_time(${REQUESTS} | unwrap latency | __error__="" [${all}]) by (job)`, now),
      scalar(`sum(sum_over_time(${REQUESTS} | unwrap bytes_out | __error__="" [${all}]))`, now),
      instant(`sum by (tool) (count_over_time(${REQUESTS} | tool != "" [${all}]))`, now),
      instant(`sum by (tool) (count_over_time(${REQUESTS} | tool != "" | status >= 400 [${all}]))`, now),
      instant(`sum by (consumer) (count_over_time(${REQUESTS} | consumer != "" [${all}]))`, now),
      Promise.all(LATENCY_BOUNDS.map((b) =>
        scalar(`sum(count_over_time(${REQUESTS} | latency <= ${b / 1000} [${all}]))`, now)
      )),
    ]);

  const toolErrorMap = new Map(toolErrors.map((r) => [r.metric.tool, num(r.value[1])]));
  const histogram = [...cumulative, total].map((c, i, arr) => ({
    le: LATENCY_BOUNDS[i] ?? null,
    count: Math.max(0, c - (i ? arr[i - 1]! : 0)),
  }));

  const points = [];
  for (let t = start; t < end; t += step) {
    points.push({
      t,
      requests: requests.get(t) ?? 0,
      errors: errors.get(t) ?? 0,
      avgMs: Math.round((avg.get(t) ?? 0) * 1000),
      p95Ms: Math.round((p95.get(t) ?? 0) * 1000),
    });
  }

  return {
    range,
    step,
    series: points,
    totals: {
      requests: total,
      errors: totalErrors,
      errorRate: total ? totalErrors / total : 0,
      avgMs: Math.round(avgAll * 1000),
      p50Ms: Math.round(p50All * 1000),
      p95Ms: Math.round(p95All * 1000),
      p99Ms: Math.round(p99All * 1000),
      bytesOut,
    },
    latencyHistogram: histogram,
    tools: tools
      .map((r) => ({ name: r.metric.tool!, count: num(r.value[1]), errors: toolErrorMap.get(r.metric.tool) ?? 0 }))
      .sort((a, b) => b.count - a.count),
    keys: keys.map((r) => ({ id: r.metric.consumer!, count: num(r.value[1]) })).sort((a, b) => b.count - a.count),
  };
}

/** 키별 보존 기간 내 요청 수와 마지막 사용 시각. */
export async function keyUsage(): Promise<Map<string, { requestCount: number; lastUsedAt: number | null }>> {
  const all = `${env.retentionDays}d`;
  const now = Date.now();
  const [counts, last] = await Promise.all([
    instant(`sum by (consumer) (count_over_time({job="apisix"} | json | consumer != "" [${all}]))`, now),
    instant(
      `max by (consumer) (max_over_time({job="apisix"} | json | consumer != "" | unwrap msec | __error__="" [${all}]))`,
      now,
    ),
  ]);
  const usage = new Map<string, { requestCount: number; lastUsedAt: number | null }>();
  for (const r of counts) usage.set(r.metric.consumer!, { requestCount: num(r.value[1]), lastUsedAt: null });
  for (const r of last) {
    const entry = usage.get(r.metric.consumer!) ?? { requestCount: 0, lastUsedAt: null };
    entry.lastUsedAt = Math.round(num(r.value[1]) * 1000);
    usage.set(r.metric.consumer!, entry);
  }
  return usage;
}

export async function lokiReady(): Promise<string> {
  const res = await fetch(`${env.loki}/ready`, { signal: AbortSignal.timeout(5_000) });
  const text = (await res.text()).trim();
  if (!res.ok) throw new Error(text || `HTTP ${res.status}`);
  return text;
}
