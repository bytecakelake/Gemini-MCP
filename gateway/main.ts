import { createHandler } from "./src/app.ts";
import { loadConfig } from "./src/config.ts";
import { createContext, VERSION } from "./src/context.ts";

const config = loadConfig();

if (Deno.args.includes("--reset-admin")) {
  const ctx = await createContext(config, { onSetupCode: () => {} });
  await ctx.auth.reset();
  await ctx.close();
  console.log("관리자 계정과 세션을 삭제했습니다. 대시보드에 접속하면 게이트웨이 로그에 새 설정 코드가 출력됩니다.");
  Deno.exit(0);
}

const ctx = await createContext(config);
ctx.health.start();
await ctx.auth.ensureSetupCode();
await ctx.logs.append({
  kind: "system",
  level: "info",
  message: `게이트웨이 시작 (v${VERSION}, Deno ${Deno.version.deno})`,
});

const server = Deno.serve({
  hostname: config.hostname,
  port: config.port,
  onListen: ({ hostname, port }) => {
    console.log(`Gemini MCP gateway listening on http://${hostname}:${port}`);
    console.log(`  /mcp        → ${config.mcpUpstream}/mcp (API key)`);
    console.log(`  /api/admin  → admin API (dashboard session)`);
    console.log(`  /*          → ${config.dashboardUpstream} (dashboard)`);
  },
}, createHandler(ctx));

const shutdown = async () => {
  await server.shutdown();
  await ctx.close();
  Deno.exit(0);
};
Deno.addSignalListener("SIGINT", shutdown);
if (Deno.build.os !== "windows") Deno.addSignalListener("SIGTERM", shutdown);
