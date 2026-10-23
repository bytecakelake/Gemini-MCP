import { readCookie, SESSION_COOKIE, sessionCookie, validatePassword } from "./auth.ts";
import { type Context, VERSION } from "./context.ts";
import { toPublic } from "./keys.ts";
import type { LogEntry } from "./logs.ts";
import { type RangeName, RANGES } from "./metrics.ts";
import { fetchTools } from "./upstream.ts";
import { clientIp, errorJson, json } from "./util.ts";

const PREFIX = "/api/admin";
const DAY = 24 * 60 * 60 * 1000;

type Handler = (args: {
  ctx: Context;
  req: Request;
  url: URL;
  ip: string;
  params: Record<string, string>;
  body: Record<string, unknown>;
}) => Promise<Response> | Response;

interface Route {
  method: string;
  pattern: URLPattern;
  handler: Handler;
  /** false 면 로그인 없이 호출 가능 */
  auth: boolean;
}

const routes: Route[] = [];
function route(method: string, path: string, handler: Handler, auth = true) {
  routes.push({ method, pattern: new URLPattern({ pathname: PREFIX + path }), handler, auth });
}

function isSecure(req: Request): boolean {
  return req.headers.get("x-forwarded-proto") === "https" || new URL(req.url).protocol === "https:";
}

function str(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function audit(ctx: Context, ip: string, message: string, level: "info" | "warn" = "info") {
  return ctx.logs.append({ kind: "audit", level, ip, message });
}

// ---------- 인증 ----------

route("GET", "/auth/state", async ({ ctx, req }) => {
  const setUp = await ctx.auth.isSetUp();
  if (!setUp) await ctx.auth.ensureSetupCode();
  return json({
    setupRequired: !setUp,
    authenticated: setUp && await ctx.auth.hasSession(readCookie(req, SESSION_COOKIE)),
  });
}, false);

route("POST", "/auth/setup", async ({ ctx, req, ip, body }) => {
  if (ctx.auth.isLockedOut(ip)) return errorJson(429, "시도 횟수를 초과했습니다. 5분 뒤 다시 시도하세요.");
  const invalid = validatePassword(body.password);
  if (invalid) return errorJson(400, invalid);
  const result = await ctx.auth.setup(str(body.code), str(body.password), ip);
  if (result === "already") return errorJson(409, "이미 관리자 계정이 있습니다.");
  if (result === "invalid-code") {
    await audit(ctx, ip, "관리자 설정 실패: 잘못된 설정 코드", "warn");
    return errorJson(403, "설정 코드가 올바르지 않습니다. 게이트웨이 로그의 코드를 확인하세요.");
  }
  await audit(ctx, ip, "관리자 계정 생성");
  const session = await ctx.auth.createSession(ip);
  return json({ ok: true }, 200, { "set-cookie": sessionCookie(session, isSecure(req)) });
}, false);

route("POST", "/auth/login", async ({ ctx, req, ip, body }) => {
  if (ctx.auth.isLockedOut(ip)) return errorJson(429, "로그인 시도 횟수를 초과했습니다. 5분 뒤 다시 시도하세요.");
  if (!await ctx.auth.verifyPassword(str(body.password), ip)) {
    await audit(ctx, ip, "로그인 실패", "warn");
    return errorJson(401, "비밀번호가 올바르지 않습니다.");
  }
  await audit(ctx, ip, "로그인");
  const session = await ctx.auth.createSession(ip);
  return json({ ok: true }, 200, { "set-cookie": sessionCookie(session, isSecure(req)) });
}, false);

route("POST", "/auth/logout", async ({ ctx, req, ip }) => {
  await ctx.auth.deleteSession(readCookie(req, SESSION_COOKIE));
  await audit(ctx, ip, "로그아웃");
  return json({ ok: true }, 200, { "set-cookie": sessionCookie("", isSecure(req), 0) });
});

route("POST", "/auth/password", async ({ ctx, req, ip, body }) => {
  if (!await ctx.auth.verifyPassword(str(body.current), ip)) {
    return errorJson(403, "현재 비밀번호가 올바르지 않습니다.");
  }
  const invalid = validatePassword(body.next);
  if (invalid) return errorJson(400, invalid);
  await ctx.auth.changePassword(str(body.next));
  await audit(ctx, ip, "관리자 비밀번호 변경 (모든 세션 로그아웃)");
  const session = await ctx.auth.createSession(ip);
  return json({ ok: true }, 200, { "set-cookie": sessionCookie(session, isSecure(req)) });
});

// ---------- API 키 ----------

function parseExpiry(body: Record<string, unknown>): number | null | undefined {
  if (!("expiresInDays" in body)) return undefined;
  const days = Number(body.expiresInDays);
  return body.expiresInDays === null || !days ? null : Date.now() + days * DAY;
}

function parseTools(value: unknown): string[] | null | undefined {
  if (value === undefined) return undefined;
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : null;
}

route("GET", "/keys", ({ ctx }) => json({ items: ctx.keys.list() }));

route("POST", "/keys", async ({ ctx, ip, body }) => {
  const name = str(body.name).trim();
  if (!name || name.length > 80) return errorJson(400, "이름은 1~80자여야 합니다.");
  const { key, secret } = await ctx.keys.create({
    name,
    expiresAt: parseExpiry(body) ?? null,
    allowedTools: parseTools(body.allowedTools) ?? null,
  });
  await audit(ctx, ip, `API 키 발급: ${key.name} (${key.prefix}…)`);
  return json({ key: toPublic(key), secret }, 201);
});

route("PATCH", "/keys/:id", async ({ ctx, ip, params, body }) => {
  const name = body.name === undefined ? undefined : str(body.name).trim();
  if (name !== undefined && (!name || name.length > 80)) return errorJson(400, "이름은 1~80자여야 합니다.");
  const key = await ctx.keys.update(params.id, {
    name,
    disabled: typeof body.disabled === "boolean" ? body.disabled : undefined,
    expiresAt: parseExpiry(body),
    allowedTools: parseTools(body.allowedTools),
  });
  if (!key) return errorJson(404, "키를 찾을 수 없습니다.");
  const change = typeof body.disabled === "boolean" ? (body.disabled ? "비활성화" : "활성화") : "수정";
  await audit(ctx, ip, `API 키 ${change}: ${key.name} (${key.prefix}…)`);
  return json({ key: toPublic(key) });
});

route("POST", "/keys/:id/rotate", async ({ ctx, ip, params }) => {
  const result = await ctx.keys.rotate(params.id);
  if (!result) return errorJson(404, "키를 찾을 수 없습니다.");
  await audit(ctx, ip, `API 키 재발급: ${result.key.name} (${result.key.prefix}…)`);
  return json({ key: toPublic(result.key), secret: result.secret });
});

route("DELETE", "/keys/:id", async ({ ctx, ip, params }) => {
  const key = await ctx.keys.delete(params.id);
  if (!key) return errorJson(404, "키를 찾을 수 없습니다.");
  await audit(ctx, ip, `API 키 삭제: ${key.name} (${key.prefix}…)`, "warn");
  return json({ ok: true });
});

// ---------- 로그 ----------

route("GET", "/logs", async ({ ctx, url }) => {
  const p = url.searchParams;
  return json(
    await ctx.logs.query({
      kind: p.get("kind") || undefined,
      level: p.get("level") || undefined,
      keyId: p.get("keyId") || undefined,
      tool: p.get("tool") || undefined,
      q: p.get("q") || undefined,
      before: p.get("before") || undefined,
      limit: Number(p.get("limit")) || undefined,
    }),
  );
});

route("DELETE", "/logs", async ({ ctx, ip }) => {
  const removed = await ctx.logs.clear();
  await audit(ctx, ip, `로그 ${removed}건 삭제`, "warn");
  return json({ removed });
});

/** 새 로그를 Server-Sent Events 로 실시간 전달합니다. */
route("GET", "/logs/stream", ({ ctx, req }) => {
  const encoder = new TextEncoder();
  let unsubscribe = () => {};
  let keepAlive: ReturnType<typeof setInterval> | undefined;
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      const send = (entry: LogEntry) => controller.enqueue(encoder.encode(`data: ${JSON.stringify(entry)}\n\n`));
      unsubscribe = ctx.logs.subscribe(send);
      controller.enqueue(encoder.encode(": connected\n\n"));
      keepAlive = setInterval(() => controller.enqueue(encoder.encode(": ping\n\n")), 20_000);
      req.signal.addEventListener("abort", () => {
        unsubscribe();
        clearInterval(keepAlive);
      });
    },
    cancel() {
      unsubscribe();
      clearInterval(keepAlive);
    },
  });
  return new Response(stream, {
    headers: { "content-type": "text/event-stream", "cache-control": "no-store", "x-accel-buffering": "no" },
  });
});

// ---------- 모니터링 ----------

route("GET", "/metrics", ({ ctx, url }) => {
  const range = (url.searchParams.get("range") ?? "24h") as RangeName;
  if (!(range in RANGES)) return errorJson(400, `range 는 ${Object.keys(RANGES).join(", ")} 중 하나여야 합니다.`);
  const summary = ctx.metrics.summary(range);
  const names = new Map(ctx.keys.list().map((k) => [k.id, k.name]));
  return json({ ...summary, keys: summary.keys.map((k) => ({ ...k, name: names.get(k.id) ?? "(삭제된 키)" })) });
});

route("GET", "/status", ({ ctx }) => {
  const keys = ctx.keys.list();
  const memory = Deno.memoryUsage();
  return json({
    gateway: {
      version: VERSION,
      deno: Deno.version.deno,
      startedAt: ctx.startedAt,
      uptimeSeconds: Math.round((Date.now() - ctx.startedAt) / 1000),
      memory: { rss: memory.rss, heapUsed: memory.heapUsed, heapTotal: memory.heapTotal },
      inflight: ctx.metrics.inflight,
      logRetentionDays: ctx.config.logRetentionDays,
    },
    upstreams: ctx.health.states,
    keys: {
      total: keys.length,
      active: keys.filter((k) => k.status === "active").length,
      disabled: keys.filter((k) => k.status === "disabled").length,
      expired: keys.filter((k) => k.status === "expired").length,
    },
  });
});

route("GET", "/tools", async ({ ctx }) => {
  try {
    return json({ items: await fetchTools(ctx.config.mcpUpstream) });
  } catch (err) {
    return errorJson(502, `MCP 서버에서 도구 목록을 가져오지 못했습니다: ${err instanceof Error ? err.message : err}`);
  }
});

// ---------- 디스패치 ----------

/**
 * /api/admin/* 요청 처리. 세션 쿠키(SameSite=Strict, HttpOnly)로 인증하고, 상태를 바꾸는 요청은
 * JSON 본문 + 같은 출처(Origin)만 받아 CSRF 를 막습니다.
 */
export async function handleAdminApi(ctx: Context, req: Request, info?: Deno.ServeHandlerInfo): Promise<Response> {
  const url = new URL(req.url);
  const ip = clientIp(req, info);
  const candidates = routes.filter((r) => r.pattern.test(url));
  const match = candidates.find((r) => r.method === req.method);
  if (!match) {
    return candidates.length ? errorJson(405, "Method not allowed") : errorJson(404, "Not found");
  }

  const mutating = req.method !== "GET" && req.method !== "HEAD";
  if (mutating) {
    const origin = req.headers.get("origin");
    const host = req.headers.get("x-forwarded-host") ?? url.host;
    if (origin && URL.parse(origin)?.host !== host) return errorJson(403, "Cross-origin request rejected");
  }

  if (match.auth && !await ctx.auth.hasSession(readCookie(req, SESSION_COOKIE))) {
    return errorJson(401, "로그인이 필요합니다.");
  }

  let body: Record<string, unknown> = {};
  if (mutating) {
    const text = await req.text();
    if (text) {
      // 폼 전송(CSRF)으로는 application/json 을 보낼 수 없습니다.
      if (!req.headers.get("content-type")?.includes("application/json")) {
        return errorJson(415, "Content-Type must be application/json");
      }
      try {
        const parsed = JSON.parse(text);
        body = parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
      } catch {
        return errorJson(400, "잘못된 JSON 입니다.");
      }
    }
  }

  const params = match.pattern.exec(url)?.pathname.groups ?? {};
  return await match.handler({ ctx, req, url, ip, params: params as Record<string, string>, body });
}
