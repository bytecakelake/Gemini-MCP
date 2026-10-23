import { sortableId } from "./util.ts";

export type LogLevel = "info" | "warn" | "error";

export interface LogEntry {
  id: string;
  ts: number;
  /** request = MCP 요청, audit = 관리 작업(로그인, 키 발급 등), system = 게이트웨이/업스트림 상태 */
  kind: "request" | "audit" | "system";
  level: LogLevel;
  message: string;
  ip?: string;
  keyId?: string;
  keyName?: string;
  /** HTTP 메서드와 상태 코드 */
  method?: string;
  status?: number;
  durationMs?: number;
  /** JSON-RPC 메서드 (tools/call, initialize ...) 와 호출한 도구 이름 */
  rpcMethod?: string;
  tool?: string;
  /** 도구가 isError 결과를 돌려준 경우 */
  toolError?: boolean;
  bytesIn?: number;
  bytesOut?: number;
  userAgent?: string;
}

export type NewLogEntry = Omit<LogEntry, "id" | "ts"> & { ts?: number };

export interface LogQuery {
  kind?: string;
  level?: string;
  keyId?: string;
  tool?: string;
  q?: string;
  /** 이 ID 보다 오래된 항목부터 (페이지네이션) */
  before?: string;
  limit?: number;
}

const MAX_SCAN = 20_000;

/** 로그는 Deno KV 에 보존 기간(expireIn)과 함께 저장하고, 대시보드 실시간 스트림 구독자에게 바로 전달합니다. */
export class LogStore {
  #subscribers = new Set<(entry: LogEntry) => void>();

  constructor(private kv: Deno.Kv, private retentionMs: number) {}

  async append(input: NewLogEntry): Promise<LogEntry> {
    const ts = input.ts ?? Date.now();
    const entry: LogEntry = { ...input, ts, id: sortableId(ts) };
    await this.kv.set(["log", entry.id], entry, { expireIn: this.retentionMs });
    for (const notify of this.#subscribers) notify(entry);
    return entry;
  }

  subscribe(listener: (entry: LogEntry) => void): () => void {
    this.#subscribers.add(listener);
    return () => this.#subscribers.delete(listener);
  }

  async query(query: LogQuery): Promise<{ items: LogEntry[]; nextBefore: string | null }> {
    const limit = Math.min(Math.max(query.limit ?? 100, 1), 500);
    const items: LogEntry[] = [];
    const needle = query.q?.trim().toLowerCase();
    let scanned = 0;
    let more = false;

    const selector: Deno.KvListSelector = query.before
      ? { prefix: ["log"], end: ["log", query.before] }
      : { prefix: ["log"] };
    for await (const { value } of this.kv.list<LogEntry>(selector, { reverse: true, batchSize: 500 })) {
      if (++scanned > MAX_SCAN) break;
      if (value.ts + this.retentionMs < Date.now()) continue;
      if (!matches(value, query, needle)) continue;
      if (items.length === limit) {
        more = true;
        break;
      }
      items.push(value);
    }
    return { items, nextBefore: more ? items[items.length - 1].id : null };
  }

  async clear(): Promise<number> {
    let count = 0;
    for await (const entry of this.kv.list({ prefix: ["log"] })) {
      await this.kv.delete(entry.key);
      count++;
    }
    return count;
  }
}

function matches(entry: LogEntry, query: LogQuery, needle: string | undefined): boolean {
  if (query.kind && entry.kind !== query.kind) return false;
  if (query.level && entry.level !== query.level) return false;
  if (query.keyId && entry.keyId !== query.keyId) return false;
  if (query.tool && entry.tool !== query.tool) return false;
  if (needle) {
    const haystack = [entry.message, entry.keyName, entry.tool, entry.rpcMethod, entry.ip, String(entry.status ?? "")]
      .join(" ")
      .toLowerCase();
    if (!haystack.includes(needle)) return false;
  }
  return true;
}
