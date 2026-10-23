import type { LogStore } from "./logs.ts";

export interface HealthState {
  name: string;
  url: string;
  healthy: boolean | null;
  latencyMs: number | null;
  detail: string;
  checkedAt: number | null;
  /** 상태가 마지막으로 바뀐 시각 */
  since: number;
  history: { t: number; ok: boolean; ms: number }[];
}

const HISTORY = 120;

/** 업스트림(MCP 서버, 대시보드)의 상태를 주기적으로 확인하고, 상태가 바뀌면 system 로그를 남깁니다. */
export class HealthMonitor {
  readonly states: HealthState[];
  #timer?: ReturnType<typeof setInterval>;

  constructor(
    targets: { name: string; url: string }[],
    private logs: LogStore,
    private intervalMs: number,
  ) {
    this.states = targets.map((t) => ({
      ...t,
      healthy: null,
      latencyMs: null,
      detail: "",
      checkedAt: null,
      since: Date.now(),
      history: [],
    }));
  }

  start(): void {
    this.checkAll();
    this.#timer = setInterval(() => this.checkAll(), this.intervalMs);
  }

  stop(): void {
    clearInterval(this.#timer);
  }

  async checkAll(): Promise<void> {
    await Promise.all(this.states.map((s) => this.#check(s)));
  }

  async #check(state: HealthState) {
    const started = performance.now();
    let ok = false;
    let detail: string;
    try {
      const res = await fetch(state.url, { signal: AbortSignal.timeout(5_000) });
      detail = (await res.text()).slice(0, 200).trim() || `HTTP ${res.status}`;
      ok = res.ok;
    } catch (err) {
      detail = err instanceof Error ? err.message : String(err);
    }
    const ms = Math.round(performance.now() - started);
    const changed = state.healthy !== ok;

    state.history.push({ t: Date.now(), ok, ms });
    if (state.history.length > HISTORY) state.history.shift();
    if (changed) {
      if (state.healthy !== null || !ok) {
        await this.logs.append({
          kind: "system",
          level: ok ? "info" : "error",
          message: `${state.name} ${ok ? "정상 복구" : "응답 없음"}: ${detail}`,
        });
      }
      state.since = Date.now();
    }
    Object.assign(state, { healthy: ok, latencyMs: ms, detail, checkedAt: Date.now() });
  }
}

export interface ToolInfo {
  name: string;
  title?: string;
  description?: string;
}

/** MCP 서버의 tools/list 를 호출해 도구 목록을 가져옵니다 (키 권한 설정 화면용). stateless 서버이므로 initialize 없이 호출합니다. */
export async function fetchTools(mcpUpstream: string): Promise<ToolInfo[]> {
  const res = await fetch(`${mcpUpstream}/mcp`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json, text/event-stream" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list", params: {} }),
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`tools/list HTTP ${res.status}`);
  const text = await res.text();
  // 응답은 application/json 또는 SSE(data: {...}) 입니다.
  const payload = res.headers.get("content-type")?.includes("text/event-stream")
    ? text.split("\n").filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).join("")
    : text;
  const message = JSON.parse(payload);
  if (message.error) throw new Error(message.error.message ?? "tools/list failed");
  return (message.result?.tools ?? []).map((t: ToolInfo) => ({
    name: t.name,
    title: t.title,
    description: t.description,
  }));
}
