import { type ApiKey, keyStatus } from "./keys.ts";
import type { Context } from "./context.ts";
import type { LogLevel } from "./logs.ts";
import { badGateway, forward } from "./proxy.ts";
import { clientIp, isLoopbackOrigin } from "./util.ts";

interface RpcInfo {
  ids: unknown[];
  methods: string[];
  tools: string[];
}

/** JSON-RPC 요청(단일 또는 배치)에서 메서드와 tools/call 의 도구 이름을 뽑습니다. */
export function inspectRpc(body: Uint8Array): RpcInfo {
  const info: RpcInfo = { ids: [], methods: [], tools: [] };
  if (body.length === 0) return info;
  try {
    const parsed = JSON.parse(new TextDecoder().decode(body));
    for (const msg of Array.isArray(parsed) ? parsed : [parsed]) {
      if (!msg || typeof msg !== "object") continue;
      if ("id" in msg) info.ids.push(msg.id);
      if (typeof msg.method === "string") info.methods.push(msg.method);
      if (msg.method === "tools/call" && typeof msg.params?.name === "string") info.tools.push(msg.params.name);
    }
  } catch {
    // 본문 해석 실패는 업스트림이 판단하도록 그대로 전달합니다.
  }
  return info;
}

function rpcError(status: number, code: number, message: string, id: unknown = null, headers: HeadersInit = {}) {
  return new Response(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }), {
    status,
    headers: { "content-type": "application/json", ...headers },
  });
}

/**
 * 응답 본문을 그대로 흘려보내면서 크기를 세고, 도구 오류(isError) / JSON-RPC 오류를 감지합니다.
 * 스트림이 끝나거나 클라이언트가 끊으면 onDone 을 한 번 호출합니다.
 */
function observe(
  body: ReadableStream<Uint8Array>,
  onDone: (result: { bytes: number; toolError: boolean; aborted: boolean }) => void,
): ReadableStream<Uint8Array> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const pattern = /"isError"\s*:\s*true|"error"\s*:\s*\{\s*"code"/;
  let bytes = 0;
  let tail = "";
  let toolError = false;
  let finished = false;
  const finish = (aborted: boolean) => {
    if (finished) return;
    finished = true;
    onDone({ bytes, toolError, aborted });
  };

  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const { done, value } = await reader.read();
        if (done) {
          controller.close();
          finish(false);
          return;
        }
        bytes += value.byteLength;
        if (!toolError) {
          // 청크 경계에 걸친 패턴도 찾도록 이전 청크의 끝부분을 이어 붙입니다.
          const text = tail + decoder.decode(value, { stream: true });
          toolError = pattern.test(text);
          tail = text.slice(-32);
        }
        controller.enqueue(value);
      } catch (err) {
        controller.error(err);
        finish(true);
      }
    },
    cancel(reason) {
      finish(true);
      return reader.cancel(reason);
    },
  });
}

/** POST/GET/DELETE /mcp: Origin 검사 → API 키 인증 → 도구 권한 검사 → MCP 서버로 전달 + 로그/지표 기록 */
export async function handleMcp(ctx: Context, req: Request, info?: Deno.ServeHandlerInfo): Promise<Response> {
  const started = performance.now();
  const ts = Date.now();
  const ip = clientIp(req, info);
  const userAgent = req.headers.get("user-agent") ?? undefined;
  const base = { kind: "request" as const, ip, method: req.method, userAgent };

  // DNS 리바인딩 방지 (MCP 사양 권고): 브라우저에서 온 요청은 localhost 또는 허용 목록의 Origin 만 받습니다.
  const origin = req.headers.get("origin");
  if (origin && !isLoopbackOrigin(origin) && !ctx.config.allowedOrigins.has(origin.toLowerCase())) {
    await ctx.logs.append({ ...base, level: "warn", status: 403, message: `Origin 거부: ${origin}` });
    return new Response("Origin not allowed", { status: 403 });
  }

  const secret = req.headers.get("authorization")?.match(/^Bearer\s+(\S+)$/i)?.[1];
  const key = secret ? await ctx.keys.find(secret) : undefined;
  const status = key ? keyStatus(key) : null;
  if (!key || status !== "active") {
    const reason = !secret
      ? "API 키 없음"
      : !key
      ? "알 수 없는 API 키"
      : status === "expired"
      ? "만료된 키"
      : "비활성 키";
    await ctx.logs.append({
      ...base,
      level: "warn",
      status: 401,
      keyId: key?.id,
      keyName: key?.name,
      message: `인증 실패: ${reason}`,
    });
    ctx.metrics.record({ ts, status: 401, durationMs: 0, toolError: false, keyId: key?.id, bytesOut: 0 });
    return rpcError(401, -32001, `Unauthorized: ${reason}`, null, {
      "www-authenticate": 'Bearer realm="gemini-mcp", error="invalid_token"',
    });
  }

  let body = new Uint8Array();
  if (req.method === "POST") {
    const declared = Number(req.headers.get("content-length") ?? 0);
    if (declared > ctx.config.maxBodyBytes) return rpcError(413, -32600, "Request body too large");
    body = new Uint8Array(await req.arrayBuffer());
    if (body.byteLength > ctx.config.maxBodyBytes) return rpcError(413, -32600, "Request body too large");
  }

  const rpc = inspectRpc(body);
  const tool = rpc.tools[0];
  const rpcMethod = rpc.methods.join(",") || undefined;
  const keyFields = { keyId: key.id, keyName: key.name, rpcMethod, tool, bytesIn: body.byteLength };
  ctx.keys.recordUse(key);

  const denied = rpc.tools.find((t) => !isToolAllowed(key, t));
  if (denied) {
    await ctx.logs.append({ ...base, ...keyFields, level: "warn", status: 403, message: `도구 권한 없음: ${denied}` });
    ctx.metrics.record({ ts, status: 403, durationMs: 0, toolError: false, tool: denied, keyId: key.id, bytesOut: 0 });
    return rpcError(403, -32003, `This API key is not allowed to call tool '${denied}'`, rpc.ids[0] ?? null);
  }

  const end = ctx.metrics.begin();
  let upstream: Response;
  try {
    upstream = await forward(req, ctx.config.mcpUpstream, {
      body,
      clientIp: ip,
      // API 키와 쿠키는 MCP 서버로 넘기지 않습니다. Origin 은 여기서 이미 검사했습니다.
      dropHeaders: ["authorization", "cookie", "origin"],
    });
  } catch (err) {
    end();
    const durationMs = Math.round(performance.now() - started);
    await ctx.logs.append({
      ...base,
      ...keyFields,
      level: "error",
      status: 502,
      durationMs,
      message: `MCP 서버 연결 실패: ${err instanceof Error ? err.message : err}`,
    });
    ctx.metrics.record({ ts, status: 502, durationMs, toolError: false, tool, keyId: key.id, bytesOut: 0 });
    return badGateway("MCP server", err);
  }

  const finalize = (bytes: number, toolError: boolean, aborted: boolean) => {
    end();
    const durationMs = Math.round(performance.now() - started);
    // stateless 서버는 서버→클라이언트 SSE 스트림(GET)을 지원하지 않아 405 를 돌려줍니다.
    // 클라이언트가 연결할 때마다 시도하는 정상 동작이므로 오류로 세지 않습니다.
    const probe = req.method === "GET" && upstream.status === 405;
    const level: LogLevel = upstream.status >= 500
      ? "error"
      : (upstream.status >= 400 && !probe) || toolError
      ? "warn"
      : "info";
    const what = tool ? `${rpcMethod} ${tool}` : rpcMethod ?? (probe ? "GET (SSE 스트림 미지원)" : req.method);
    const suffix = aborted ? " (클라이언트 연결 끊김)" : toolError ? " (오류 응답)" : "";
    if (!probe) {
      ctx.metrics.record({ ts, status: upstream.status, durationMs, toolError, tool, keyId: key.id, bytesOut: bytes });
    }
    ctx.logs.append({
      ...base,
      ...keyFields,
      level,
      status: upstream.status,
      durationMs,
      toolError,
      bytesOut: bytes,
      message: `${what} → ${upstream.status} ${durationMs}ms${suffix}`,
    }).catch((err) => console.error("log append failed", err));
  };

  if (!upstream.body) {
    finalize(0, false, false);
    return upstream;
  }
  const body$ = observe(upstream.body, (r) => finalize(r.bytes, r.toolError, r.aborted));
  return new Response(body$, { status: upstream.status, statusText: upstream.statusText, headers: upstream.headers });
}

export function isToolAllowed(key: ApiKey, tool: string): boolean {
  return key.allowedTools === null || key.allowedTools.includes(tool);
}
