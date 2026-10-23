import { handleAdminApi } from "./api.ts";
import type { Context } from "./context.ts";
import { handleMcp } from "./mcp.ts";
import { badGateway, forward } from "./proxy.ts";
import { clientIp } from "./util.ts";

const SECURITY_HEADERS: Record<string, string> = {
  "x-content-type-options": "nosniff",
  "x-frame-options": "DENY",
  "referrer-policy": "no-referrer",
};

function withSecurityHeaders(res: Response): Response {
  for (const [name, value] of Object.entries(SECURITY_HEADERS)) {
    if (!res.headers.has(name)) res.headers.set(name, value);
  }
  return res;
}

/**
 * 라우팅:
 *   /mcp           → MCP 서버 (API 키 인증, 로그·지표 기록)
 *   /skill, /health → MCP 서버의 내장 스킬 문서·상태 (공개, 인증 없음)
 *   /api/admin/*   → 관리 API (대시보드 세션 인증)
 *   /healthz       → 게이트웨이 자체 상태
 *   그 외          → Nuxt 대시보드
 */
export function createHandler(ctx: Context): Deno.ServeHandler {
  return async (req, info) => {
    const { pathname } = new URL(req.url);

    if (pathname === "/mcp" || pathname.startsWith("/mcp/")) return await handleMcp(ctx, req, info);

    if (pathname === "/healthz") return new Response("ok");

    if (pathname === "/skill" || pathname === "/health") {
      try {
        return await forward(req, ctx.config.mcpUpstream, { dropHeaders: ["authorization", "cookie"] });
      } catch (err) {
        return badGateway("MCP server", err);
      }
    }

    if (pathname === "/api/admin" || pathname.startsWith("/api/admin/")) {
      try {
        return withSecurityHeaders(await handleAdminApi(ctx, req, info));
      } catch (err) {
        console.error("admin api error", err);
        return withSecurityHeaders(Response.json({ error: "Internal error" }, { status: 500 }));
      }
    }

    try {
      // 관리자 세션 쿠키는 대시보드 서버(Nitro)에 넘길 필요가 없습니다.
      const res = await forward(req, ctx.config.dashboardUpstream, {
        clientIp: clientIp(req, info),
        dropHeaders: ["authorization", "cookie"],
      });
      return withSecurityHeaders(res);
    } catch (err) {
      return badGateway("dashboard", err);
    }
  };
}
