/** 지연 시간 히스토그램 경계 (ms). 마지막 칸은 그 이상. */
export const LATENCY_BOUNDS = [50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000, 60_000, 120_000];

const MINUTE = 60_000;
const FLUSH_MS = 10_000;

interface Bucket {
  /** 분 단위 시작 시각 (epoch ms) */
  t: number;
  requests: number;
  errors: number;
  toolErrors: number;
  latencySum: number;
  latencyMax: number;
  hist: number[];
  bytesOut: number;
  tools: Record<string, number>;
  toolFailures: Record<string, number>;
  keys: Record<string, number>;
}

export interface Sample {
  ts: number;
  status: number;
  durationMs: number;
  toolError: boolean;
  tool?: string;
  keyId?: string;
  bytesOut: number;
}

export const RANGES = {
  "1h": { span: 60 * MINUTE, step: MINUTE },
  "24h": { span: 24 * 60 * MINUTE, step: 15 * MINUTE },
  "7d": { span: 7 * 24 * 60 * MINUTE, step: 2 * 60 * MINUTE },
} as const;
export type RangeName = keyof typeof RANGES;

export interface MetricsSummary {
  range: RangeName;
  step: number;
  series: { t: number; requests: number; errors: number; avgMs: number; p95Ms: number }[];
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
  keys: { id: string; count: number }[];
}

function emptyBucket(t: number): Bucket {
  return {
    t,
    requests: 0,
    errors: 0,
    toolErrors: 0,
    latencySum: 0,
    latencyMax: 0,
    hist: new Array(LATENCY_BOUNDS.length + 1).fill(0),
    bytesOut: 0,
    tools: {},
    toolFailures: {},
    keys: {},
  };
}

function merge(into: Bucket, from: Bucket) {
  into.requests += from.requests;
  into.errors += from.errors;
  into.toolErrors += from.toolErrors;
  into.latencySum += from.latencySum;
  into.latencyMax = Math.max(into.latencyMax, from.latencyMax);
  into.bytesOut += from.bytesOut;
  from.hist.forEach((n, i) => into.hist[i] += n);
  for (const [k, n] of Object.entries(from.tools)) into.tools[k] = (into.tools[k] ?? 0) + n;
  for (const [k, n] of Object.entries(from.toolFailures)) into.toolFailures[k] = (into.toolFailures[k] ?? 0) + n;
  for (const [k, n] of Object.entries(from.keys)) into.keys[k] = (into.keys[k] ?? 0) + n;
}

/** 히스토그램에서 백분위수를 선형 보간으로 추정합니다. */
export function percentile(hist: number[], p: number, max: number): number {
  const total = hist.reduce((a, b) => a + b, 0);
  if (total === 0) return 0;
  const rank = p * total;
  let seen = 0;
  for (let i = 0; i < hist.length; i++) {
    if (seen + hist[i] >= rank) {
      const lower = i === 0 ? 0 : LATENCY_BOUNDS[i - 1];
      const upper = i < LATENCY_BOUNDS.length ? LATENCY_BOUNDS[i] : Math.max(max, lower);
      const value = lower + (upper - lower) * ((rank - seen) / hist[i]);
      return Math.round(Math.min(value, max));
    }
    seen += hist[i];
  }
  return Math.round(max);
}

/**
 * MCP 요청 지표를 분 단위 버킷으로 집계합니다. 버킷은 메모리에 두고(최대 보존 기간만큼)
 * 바뀐 버킷만 주기적으로 KV 에 기록해 재시작 후에도 그래프가 이어지게 합니다.
 */
export class Metrics {
  #buckets = new Map<number, Bucket>();
  #dirty = new Set<number>();
  #timer?: ReturnType<typeof setInterval>;
  #inflight = 0;

  constructor(private kv: Deno.Kv, private retentionMs: number) {}

  get inflight(): number {
    return this.#inflight;
  }

  begin(): () => void {
    this.#inflight++;
    let done = false;
    return () => {
      if (!done) this.#inflight--;
      done = true;
    };
  }

  async load(): Promise<void> {
    const since = Date.now() - this.retentionMs;
    for await (const { value } of this.kv.list<Bucket>({ prefix: ["metric"], start: ["metric", since] })) {
      this.#buckets.set(value.t, value);
    }
    this.#timer = setInterval(() => this.flush(), FLUSH_MS);
  }

  async close(): Promise<void> {
    clearInterval(this.#timer);
    await this.flush();
  }

  record(sample: Sample): void {
    const t = Math.floor(sample.ts / MINUTE) * MINUTE;
    let bucket = this.#buckets.get(t);
    if (!bucket) this.#buckets.set(t, bucket = emptyBucket(t));

    bucket.requests++;
    if (sample.status >= 400 || sample.toolError) bucket.errors++;
    if (sample.toolError) bucket.toolErrors++;
    bucket.latencySum += sample.durationMs;
    bucket.latencyMax = Math.max(bucket.latencyMax, sample.durationMs);
    const slot = LATENCY_BOUNDS.findIndex((b) => sample.durationMs <= b);
    bucket.hist[slot === -1 ? LATENCY_BOUNDS.length : slot]++;
    bucket.bytesOut += sample.bytesOut;
    if (sample.tool) {
      bucket.tools[sample.tool] = (bucket.tools[sample.tool] ?? 0) + 1;
      if (sample.toolError || sample.status >= 400) {
        bucket.toolFailures[sample.tool] = (bucket.toolFailures[sample.tool] ?? 0) + 1;
      }
    }
    if (sample.keyId) bucket.keys[sample.keyId] = (bucket.keys[sample.keyId] ?? 0) + 1;
    this.#dirty.add(t);
  }

  async flush(): Promise<void> {
    const cutoff = Date.now() - this.retentionMs;
    for (const t of this.#buckets.keys()) if (t < cutoff) this.#buckets.delete(t);

    const dirty = [...this.#dirty];
    this.#dirty.clear();
    for (const t of dirty) {
      const bucket = this.#buckets.get(t);
      if (bucket) await this.kv.set(["metric", t], bucket, { expireIn: this.retentionMs });
    }
  }

  summary(range: RangeName, now = Date.now()): MetricsSummary {
    const { span, step } = RANGES[range];
    const end = Math.floor(now / step) * step + step;
    const start = end - span;
    const total = emptyBucket(start);
    const slots = new Map<number, Bucket>();
    for (let t = start; t < end; t += step) slots.set(t, emptyBucket(t));

    for (const bucket of this.#buckets.values()) {
      if (bucket.t < start || bucket.t >= end) continue;
      merge(slots.get(Math.floor((bucket.t - start) / step) * step + start)!, bucket);
      merge(total, bucket);
    }

    const avg = (b: Bucket) => (b.requests ? Math.round(b.latencySum / b.requests) : 0);
    return {
      range,
      step,
      series: [...slots.values()].map((b) => ({
        t: b.t,
        requests: b.requests,
        errors: b.errors,
        avgMs: avg(b),
        p95Ms: percentile(b.hist, 0.95, b.latencyMax),
      })),
      totals: {
        requests: total.requests,
        errors: total.errors,
        toolErrors: total.toolErrors,
        errorRate: total.requests ? total.errors / total.requests : 0,
        avgMs: avg(total),
        p50Ms: percentile(total.hist, 0.5, total.latencyMax),
        p95Ms: percentile(total.hist, 0.95, total.latencyMax),
        p99Ms: percentile(total.hist, 0.99, total.latencyMax),
        maxMs: total.latencyMax,
        bytesOut: total.bytesOut,
      },
      latencyHistogram: total.hist.map((count, i) => ({ le: LATENCY_BOUNDS[i] ?? null, count })),
      tools: Object.entries(total.tools)
        .map(([name, count]) => ({ name, count, errors: total.toolFailures[name] ?? 0 }))
        .sort((a, b) => b.count - a.count),
      keys: Object.entries(total.keys).map(([id, count]) => ({ id, count })).sort((a, b) => b.count - a.count),
    };
  }
}
