/**
 * 대시보드 서버 시작 시:
 *  - 관리자 계정이 없으면 설정 코드를 로그에 출력합니다.
 *  - APISIX 에 라우트(/mcp, /skill·/health, 대시보드)를 등록합니다. APISIX 가 준비될 때까지 재시도합니다.
 *  - 1분마다 만료된 키를 허용 목록에서 빼고, 15초마다 서비스 상태를 확인합니다.
 */
export default defineNitroPlugin(() => {
  ensureSetupCode().catch((err) => console.error("setup code:", err));

  const register = async (attempt = 1): Promise<void> => {
    try {
      await ensureRoutes();
      console.log("APISIX routes registered");
      await pushLog("system", "info", "대시보드 시작: APISIX 라우트 등록");
    } catch (err) {
      const delay = Math.min(30_000, attempt * 2_000);
      console.warn(`APISIX route registration failed (attempt ${attempt}), retrying in ${delay / 1000}s:`, err);
      setTimeout(() => register(attempt + 1), delay);
    }
  };
  register();

  setInterval(() => {
    syncWhitelist().catch((err) => console.warn("whitelist sync failed:", err));
  }, 60_000);

  checkHealth();
  setInterval(checkHealth, 15_000);
});
