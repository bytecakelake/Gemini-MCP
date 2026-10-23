/**
 * 게이트웨이 설정. 인증 정보는 환경 변수로 받지 않습니다:
 * API 키와 관리자 계정은 모두 대시보드에서 만들고 Deno KV 에 (해시로) 저장합니다.
 */
export interface GatewayConfig {
  hostname: string;
  port: number;
  /** Gemini MCP 서버 (.NET). /mcp 와 /skill 을 이곳으로 전달합니다. */
  mcpUpstream: string;
  /** Nuxt 대시보드 (Nitro, Deno). /api/admin 과 /mcp 를 제외한 나머지 경로를 전달합니다. */
  dashboardUpstream: string;
  /** Deno KV 파일 경로. ":memory:" 면 메모리에만 저장합니다 (테스트용). */
  kvPath: string;
  /** localhost 외에 허용할 브라우저 Origin. */
  allowedOrigins: Set<string>;
  logRetentionDays: number;
  /** MCP 요청 본문 최대 크기. */
  maxBodyBytes: number;
  healthIntervalMs: number;
}

export function loadConfig(env: (name: string) => string | undefined = Deno.env.get): GatewayConfig {
  const get = (name: string, fallback: string) => env(name)?.trim() || fallback;
  const dataDir = get("GATEWAY_DATA_DIR", "./data");
  return {
    hostname: get("GATEWAY_HOST", "127.0.0.1"),
    port: Number(get("GATEWAY_PORT", "8080")),
    mcpUpstream: get("MCP_UPSTREAM", "http://127.0.0.1:5100").replace(/\/+$/, ""),
    dashboardUpstream: get("DASHBOARD_UPSTREAM", "http://127.0.0.1:3000").replace(/\/+$/, ""),
    kvPath: dataDir === ":memory:" ? ":memory:" : `${dataDir.replace(/[\\/]+$/, "")}/gateway.kv`,
    allowedOrigins: new Set(
      get("MCP_ALLOWED_ORIGINS", "").split(",").map((o) => o.trim().toLowerCase()).filter(Boolean),
    ),
    logRetentionDays: Number(get("LOG_RETENTION_DAYS", "7")),
    maxBodyBytes: Number(get("MAX_BODY_BYTES", String(10 * 1024 * 1024))),
    healthIntervalMs: Number(get("HEALTH_INTERVAL_SECONDS", "15")) * 1000,
  };
}
