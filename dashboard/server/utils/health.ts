export interface HealthState {
  name: string;
  url: string;
  healthy: boolean | null;
  latencyMs: number | null;
  detail: string;
  checkedAt: number | null;
  since: number;
  history: { t: number; ok: boolean; ms: number }[];
}

const HISTORY = 120;

const targets: { name: string; url: string; check: () => Promise<string> }[] = [
  {
    name: "API 게이트웨이 (APISIX)",
    url: `${env.apisixAdmin}/apisix/admin`,
    check: async () => {
      await pingAdmin();
      return "Admin API 응답";
    },
  },
  {
    name: "MCP 서버",
    url: `${env.mcp}/health`,
    check: async () => {
      const res = await fetch(`${env.mcp}/health`, { signal: AbortSignal.timeout(5_000) });
      const text = (await res.text()).trim();
      if (!res.ok) throw new Error(text || `HTTP ${res.status}`);
      return text;
    },
  },
  { name: "로그 저장소 (Loki)", url: `${env.loki}/ready`, check: lokiReady },
];

/** 서비스 상태. 바뀔 때마다 시스템 로그를 남깁니다. */
export const healthStates: HealthState[] = targets.map((t) => ({
  name: t.name,
  url: t.url,
  healthy: null,
  latencyMs: null,
  detail: "",
  checkedAt: null,
  since: Date.now(),
  history: [],
}));

export async function checkHealth(): Promise<void> {
  await Promise.all(targets.map(async (target, i) => {
    const state = healthStates[i]!;
    const started = performance.now();
    let ok = false;
    let detail: string;
    try {
      detail = (await target.check()).slice(0, 200);
      ok = true;
    } catch (err) {
      detail = err instanceof Error ? err.message : String(err);
    }
    const ms = Math.round(performance.now() - started);
    state.history.push({ t: Date.now(), ok, ms });
    if (state.history.length > HISTORY) state.history.shift();
    if (state.healthy !== ok) {
      if (state.healthy !== null || !ok) {
        await pushLog("system", ok ? "info" : "error", `${state.name} ${ok ? "정상 복구" : "응답 없음"}: ${detail}`);
      }
      state.since = Date.now();
    }
    Object.assign(state, { healthy: ok, latencyMs: ms, detail, checkedAt: Date.now() });
  }));
}
