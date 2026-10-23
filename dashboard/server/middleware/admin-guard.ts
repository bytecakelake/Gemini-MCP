const PUBLIC = new Set(["/api/admin/auth/state", "/api/admin/auth/setup", "/api/admin/auth/login"]);

/**
 * 모든 응답에 보안 헤더를 붙이고, /api/admin/* 는 세션 쿠키(HttpOnly, SameSite=Strict)로 인증합니다.
 * 상태를 바꾸는 요청은 같은 출처(Origin)와 JSON 본문만 받아 CSRF 를 막습니다.
 */
export default defineEventHandler(async (event) => {
  setResponseHeaders(event, {
    "x-content-type-options": "nosniff",
    "x-frame-options": "DENY",
    "referrer-policy": "no-referrer",
  });

  const path = event.path.split("?")[0]!;
  if (!path.startsWith("/api/admin/")) return;
  setResponseHeader(event, "cache-control", "no-store");

  if (event.method !== "GET" && event.method !== "HEAD") {
    const origin = getRequestHeader(event, "origin");
    const host = getRequestHeader(event, "x-forwarded-host") ?? getRequestHeader(event, "host");
    if (origin && URL.parse(origin)?.host !== host) fail(403, "Cross-origin request rejected");
    const length = Number(getRequestHeader(event, "content-length") ?? 0);
    if (length > 0 && !getRequestHeader(event, "content-type")?.includes("application/json")) {
      fail(415, "Content-Type must be application/json");
    }
  }

  if (!PUBLIC.has(path) && !await hasSession(event)) fail(401, "로그인이 필요합니다.");
});
