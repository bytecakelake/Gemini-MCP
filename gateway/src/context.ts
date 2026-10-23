import { AdminAuth } from "./auth.ts";
import type { GatewayConfig } from "./config.ts";
import { KeyStore } from "./keys.ts";
import { LogStore } from "./logs.ts";
import { Metrics } from "./metrics.ts";
import { HealthMonitor } from "./upstream.ts";

export const VERSION = "1.0.0";

/** 게이트웨이 전역 상태. 테스트에서는 ":memory:" KV 로 만들어 씁니다. */
export interface Context {
  config: GatewayConfig;
  kv: Deno.Kv;
  keys: KeyStore;
  auth: AdminAuth;
  logs: LogStore;
  metrics: Metrics;
  health: HealthMonitor;
  startedAt: number;
  close(): Promise<void>;
}

export async function createContext(
  config: GatewayConfig,
  options: { onSetupCode?: (code: string) => void } = {},
): Promise<Context> {
  if (config.kvPath !== ":memory:") await Deno.mkdir(config.kvPath.replace(/[\\/][^\\/]+$/, ""), { recursive: true });
  const kv = await Deno.openKv(config.kvPath);
  const retentionMs = config.logRetentionDays * 24 * 60 * 60 * 1000;

  const keys = new KeyStore(kv);
  const logs = new LogStore(kv, retentionMs);
  const metrics = new Metrics(kv, retentionMs);
  const auth = new AdminAuth(kv, options.onSetupCode);
  const health = new HealthMonitor(
    [
      { name: "MCP 서버", url: `${config.mcpUpstream}/health` },
      { name: "대시보드", url: `${config.dashboardUpstream}/` },
    ],
    logs,
    config.healthIntervalMs,
  );
  await keys.load();
  await metrics.load();

  return {
    config,
    kv,
    keys,
    auth,
    logs,
    metrics,
    health,
    startedAt: Date.now(),
    async close() {
      health.stop();
      await keys.close();
      await metrics.close();
      kv.close();
    },
  };
}
