import { assert, assertEquals, assertMatch } from "@std/assert";
import { createHandler } from "./src/app.ts";
import { loadConfig } from "./src/config.ts";
import { createContext } from "./src/context.ts";
import { percentile } from "./src/metrics.ts";

/** 가짜 MCP 서버 + 가짜 대시보드 + 메모리 KV 로 게이트웨이를 띄웁니다. */
async function setup() {
  const upstreamHeaders: Headers[] = [];
  const mcp = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen: () => {} }, async (req) => {
    upstreamHeaders.push(new Headers(req.headers));
    const { pathname } = new URL(req.url);
    if (pathname === "/health") return new Response("ok");
    if (pathname === "/skill") return new Response("---\nname: gemini-mcp\n");
    const msg = await req.json();
    const result = msg.method === "tools/list"
      ? { tools: [{ name: "gemini_ask" }, { name: "gemini_web_search" }] }
      : msg.params?.name === "gemini_fail"
      ? { content: [{ type: "text", text: "boom" }], isError: true }
      : { content: [{ type: "text", text: "pong" }] };
    const body = `event: message\ndata: ${JSON.stringify({ jsonrpc: "2.0", id: msg.id, result })}\n\n`;
    return new Response(body, { headers: { "content-type": "text/event-stream" } });
  });
  const dashboard = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen: () => {} }, (req) => {
    return new Response(`dashboard ${new URL(req.url).pathname} cookie=${req.headers.get("cookie") ?? ""}`);
  });

  let setupCode = "";
  const config = loadConfig((name) =>
    ({
      GATEWAY_DATA_DIR: ":memory:",
      MCP_UPSTREAM: `http://127.0.0.1:${mcp.addr.port}`,
      DASHBOARD_UPSTREAM: `http://127.0.0.1:${dashboard.addr.port}`,
    })[name]
  );
  const ctx = await createContext(config, { onSetupCode: (c) => setupCode = c });
  const handler = createHandler(ctx);
  const gateway = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen: () => {} }, handler);
  const base = `http://127.0.0.1:${gateway.addr.port}`;

  let cookie = "";
  const admin = async (method: string, path: string, body?: unknown) => {
    const res = await fetch(base + "/api/admin" + path, {
      method,
      headers: { cookie, ...(body ? { "content-type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    const setCookie = res.headers.get("set-cookie");
    if (setCookie) cookie = setCookie.split(";")[0];
    return { status: res.status, data: await res.json() };
  };

  const mcpCall = (key: string | null, payload: unknown, headers: Record<string, string> = {}) =>
    fetch(base + "/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        accept: "application/json, text/event-stream",
        ...(key ? { authorization: `Bearer ${key}` } : {}),
        ...headers,
      },
      body: JSON.stringify(payload),
    });

  return {
    ctx,
    base,
    admin,
    mcpCall,
    upstreamHeaders,
    setupCode: () => setupCode,
    async close() {
      await gateway.shutdown();
      await mcp.shutdown();
      await dashboard.shutdown();
      await ctx.close();
    },
  };
}

async function loginAsAdmin(t: Awaited<ReturnType<typeof setup>>) {
  const state = await t.admin("GET", "/auth/state");
  assertEquals(state.data.setupRequired, true);
  const res = await t.admin("POST", "/auth/setup", { code: t.setupCode(), password: "correct horse battery" });
  assertEquals(res.status, 200);
}

const call = (name: string, id = 1) => ({ jsonrpc: "2.0", id, method: "tools/call", params: { name, arguments: {} } });

/** 응답 스트림이 끝난 뒤 로그/지표가 기록되므로 잠시 기다립니다. */
const settle = () => new Promise((r) => setTimeout(r, 50));

Deno.test("admin setup requires the one-time code and creates a session", async () => {
  const t = await setup();
  try {
    assertEquals((await t.admin("GET", "/keys")).status, 401);
    assertEquals((await t.admin("GET", "/auth/state")).data, { setupRequired: true, authenticated: false });
    assertEquals(
      (await t.admin("POST", "/auth/setup", { code: "WRONG-CODE", password: "long enough pw" })).status,
      403,
    );
    assertEquals((await t.admin("POST", "/auth/setup", { code: t.setupCode(), password: "short" })).status, 400);

    await loginAsAdmin(t);
    assertEquals((await t.admin("GET", "/auth/state")).data, { setupRequired: false, authenticated: true });
    assertEquals(
      (await t.admin("POST", "/auth/setup", { code: t.setupCode(), password: "another password" })).status,
      409,
    );

    await t.admin("POST", "/auth/logout");
    assertEquals((await t.admin("GET", "/keys")).status, 401);
    assertEquals((await t.admin("POST", "/auth/login", { password: "nope nope nope" })).status, 401);
    assertEquals((await t.admin("POST", "/auth/login", { password: "correct horse battery" })).status, 200);
    assertEquals((await t.admin("GET", "/keys")).status, 200);
  } finally {
    await t.close();
  }
});

Deno.test("mutating admin requests reject cross-origin and non-JSON bodies", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const crossOrigin = await fetch(t.base + "/api/admin/keys", {
      method: "POST",
      headers: { origin: "https://evil.example", "content-type": "application/json" },
      body: JSON.stringify({ name: "x" }),
    });
    assertEquals(crossOrigin.status, 403);
    await crossOrigin.body?.cancel();
  } finally {
    await t.close();
  }
});

Deno.test("MCP requests need an active API key and are forwarded without it", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const created = await t.admin("POST", "/keys", { name: "laptop" });
    assertEquals(created.status, 201);
    const secret: string = created.data.secret;
    assertMatch(secret, /^gmcp_[A-Za-z0-9_-]{43}$/);
    assertEquals(created.data.key.hash, undefined);

    const noKey = await t.mcpCall(null, call("gemini_ask"));
    assertEquals(noKey.status, 401);
    assertMatch(noKey.headers.get("www-authenticate") ?? "", /^Bearer/);
    await noKey.body?.cancel();

    const bad = await t.mcpCall("gmcp_notarealkey", call("gemini_ask"));
    assertEquals(bad.status, 401);
    await bad.body?.cancel();

    const ok = await t.mcpCall(secret, call("gemini_ask"));
    assertEquals(ok.status, 200);
    assertMatch(await ok.text(), /pong/);
    const forwarded = t.upstreamHeaders.at(-1)!;
    assertEquals(forwarded.get("authorization"), null);

    // 비활성화 → 거부, 다시 활성화 → 허용
    await t.admin("PATCH", `/keys/${created.data.key.id}`, { disabled: true });
    const disabled = await t.mcpCall(secret, call("gemini_ask"));
    assertEquals(disabled.status, 401);
    await disabled.body?.cancel();
    await t.admin("PATCH", `/keys/${created.data.key.id}`, { disabled: false });

    // 재발급 → 이전 키 무효
    const rotated = await t.admin("POST", `/keys/${created.data.key.id}/rotate`);
    const old = await t.mcpCall(secret, call("gemini_ask"));
    assertEquals(old.status, 401);
    await old.body?.cancel();
    const fresh = await t.mcpCall(rotated.data.secret, call("gemini_ask"));
    assertEquals(fresh.status, 200);
    await fresh.body?.cancel();

    // 삭제
    assertEquals((await t.admin("DELETE", `/keys/${created.data.key.id}`)).data, { ok: true });
    const deleted = await t.mcpCall(rotated.data.secret, call("gemini_ask"));
    assertEquals(deleted.status, 401);
    await deleted.body?.cancel();
  } finally {
    await t.close();
  }
});

Deno.test("expired keys are rejected", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const created = await t.admin("POST", "/keys", { name: "temp" });
    const key = t.ctx.keys.get(created.data.key.id)!;
    await t.ctx.keys.update(key.id, { expiresAt: Date.now() - 1000 });
    const res = await t.mcpCall(created.data.secret, call("gemini_ask"));
    assertEquals(res.status, 401);
    assertMatch((await res.json()).error.message, /만료/);
  } finally {
    await t.close();
  }
});

Deno.test("allowedTools restricts tools/call but not other methods", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const created = await t.admin("POST", "/keys", { name: "search-only", allowedTools: ["gemini_web_search"] });
    const secret = created.data.secret;

    const denied = await t.mcpCall(secret, call("gemini_ask", 7));
    assertEquals(denied.status, 403);
    const body = await denied.json();
    assertEquals(body.id, 7);
    assertEquals(body.error.code, -32003);

    const allowed = await t.mcpCall(secret, call("gemini_web_search"));
    assertEquals(allowed.status, 200);
    await allowed.body?.cancel();

    const list = await t.mcpCall(secret, { jsonrpc: "2.0", id: 2, method: "tools/list" });
    assertEquals(list.status, 200);
    await list.body?.cancel();
  } finally {
    await t.close();
  }
});

Deno.test("foreign browser origins are rejected before authentication", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const { data } = await t.admin("POST", "/keys", { name: "k" });
    const foreign = await t.mcpCall(data.secret, call("gemini_ask"), { origin: "https://evil.example" });
    assertEquals(foreign.status, 403);
    await foreign.body?.cancel();
    const local = await t.mcpCall(data.secret, call("gemini_ask"), { origin: "http://localhost:3000" });
    assertEquals(local.status, 200);
    await local.body?.cancel();
  } finally {
    await t.close();
  }
});

Deno.test("requests are logged and aggregated into metrics", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    const { data } = await t.admin("POST", "/keys", { name: "metrics-key" });
    for (const name of ["gemini_ask", "gemini_ask", "gemini_fail"]) {
      const res = await t.mcpCall(data.secret, call(name));
      await res.text();
    }
    const unauthorized = await t.mcpCall(null, call("gemini_ask"));
    await unauthorized.body?.cancel();
    await settle();

    const logs = await t.admin("GET", "/logs?kind=request");
    assertEquals(logs.data.items.length, 4);
    const failed = logs.data.items.find((l: { tool?: string }) => l.tool === "gemini_fail");
    assertEquals(failed.toolError, true);
    assertEquals(failed.level, "warn");
    assertEquals(failed.keyName, "metrics-key");

    const filtered = await t.admin("GET", "/logs?tool=gemini_ask");
    assertEquals(filtered.data.items.length, 2);

    const audit = await t.admin("GET", "/logs?kind=audit");
    assert(audit.data.items.some((l: { message: string }) => l.message.includes("API 키 발급")));

    const metrics = await t.admin("GET", "/metrics?range=1h");
    assertEquals(metrics.data.totals.requests, 4);
    assertEquals(metrics.data.totals.errors, 2);
    assertEquals(metrics.data.totals.toolErrors, 1);
    assertEquals(metrics.data.series.length, 60);
    assertEquals(metrics.data.tools[0], { name: "gemini_ask", count: 2, errors: 0 });
    assertEquals(metrics.data.keys[0].name, "metrics-key");

    const keys = await t.admin("GET", "/keys");
    assertEquals(keys.data.items[0].requestCount, 3);
  } finally {
    await t.close();
  }
});

Deno.test("status reports upstream health and tools come from the MCP server", async () => {
  const t = await setup();
  try {
    await loginAsAdmin(t);
    await t.ctx.health.checkAll();
    const status = await t.admin("GET", "/status");
    assertEquals(status.data.upstreams.map((u: { healthy: boolean }) => u.healthy), [true, true]);
    const tools = await t.admin("GET", "/tools");
    assertEquals(tools.data.items.map((x: { name: string }) => x.name), ["gemini_ask", "gemini_web_search"]);
  } finally {
    await t.close();
  }
});

Deno.test("dashboard, skill and health routes are proxied without cookies", async () => {
  const t = await setup();
  try {
    const page = await fetch(t.base + "/keys", { headers: { cookie: "gmcp_session=secret" } });
    assertEquals(await page.text(), "dashboard /keys cookie=");
    assertEquals(page.headers.get("x-frame-options"), "DENY");
    assertEquals(await (await fetch(t.base + "/health")).text(), "ok");
    const skill = await fetch(t.base + "/skill");
    assertMatch(await skill.text(), /name: gemini-mcp/);
  } finally {
    await t.close();
  }
});

Deno.test("percentile interpolates within histogram buckets", () => {
  // 50ms 이하 10건, 100ms 이하 10건
  const hist = [10, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
  assertEquals(percentile(hist, 0.5, 90), 50);
  assertEquals(percentile(hist, 0.95, 90), 90);
  assertEquals(percentile([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0.5, 0), 0);
});
