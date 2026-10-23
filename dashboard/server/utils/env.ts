/** 대시보드 서버 설정. 인증 정보는 받지 않습니다 (관리자 계정은 설정 코드로 만들고 DATA_DIR 에 해시로 저장). */
export const env = {
  /** APISIX Admin API. control 네트워크에서만 열려 있어 관리 키 없이 호출합니다. */
  apisixAdmin: (process.env.APISIX_ADMIN_URL ?? "http://127.0.0.1:9180").replace(/\/+$/, ""),
  loki: (process.env.LOKI_URL ?? "http://127.0.0.1:3100").replace(/\/+$/, ""),
  /** Gemini MCP 서버 (상태 확인, 도구 목록, APISIX 업스트림) */
  mcp: (process.env.MCP_URL ?? "http://127.0.0.1:5100").replace(/\/+$/, ""),
  /** APISIX 가 대시보드로 요청을 넘길 주소 (host:port) */
  dashboardUpstream: process.env.DASHBOARD_UPSTREAM ?? "127.0.0.1:3000",
  dataDir: process.env.DATA_DIR ?? "/data",
  retentionDays: Number(process.env.LOG_RETENTION_DAYS ?? 7),
};

export const DAY_MS = 24 * 60 * 60 * 1000;

/** 대시보드 서버 시작 시각 (상태 화면의 가동 시간) */
export const startedAt = Date.now();
