/**
 * Apache APISIX Admin API 연동. 게이트웨이의 라우트와 API 키(컨슈머 + key-auth)를 여기서 관리합니다.
 *
 * API 키 하나 = 컨슈머 하나:
 *   username  k_<id>            (로그·지표에 남는 식별자)
 *   desc      키 이름
 *   labels    prefix(표시용 키 앞부분), expires(만료 시각 ms), disabled("true")
 *   credential  /consumers/k_<id>/credentials/key — key-auth.key = gmcp_<random> (APISIX 가 etcd 에 암호화해 저장)
 * 비활성/만료 키는 /mcp 라우트의 consumer-restriction 허용 목록에서 빠집니다 (1분마다, 그리고 변경 즉시 동기화).
 */

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: number;
  expiresAt: number | null;
  disabled: boolean;
  status: "active" | "disabled" | "expired";
}

interface Consumer {
  username: string;
  desc?: string;
  labels?: Record<string, string>;
  create_time?: number;
}

const KEY_PREFIX = "gmcp_";
const MCP_ROUTE = "mcp";
/** consumer-restriction 의 whitelist 는 비어 있을 수 없어, 활성 키가 없을 때 쓰는 자리표시자입니다. */
const NO_ACTIVE_KEYS = "__no_active_keys__";

/**
 * key-auth 앞에서 실행되는 Lua 함수 (serverless-pre-function, rewrite 단계):
 *  - MCP 클라이언트가 보내는 "Authorization: Bearer <키>" 를 key-auth 가 읽는 apikey 헤더로 옮깁니다.
 *  - JSON-RPC 본문에서 메서드와 tools/call 의 도구 이름을 꺼내 X-MCP-RPC / X-MCP-Tool 헤더로 남겨
 *    loki-logger 가 로그에 기록하게 합니다 (클라이언트가 보낸 같은 이름의 헤더는 먼저 지웁니다).
 */
const PRE_FUNCTION = `return function(conf, ctx)
  local core = require("apisix.core")
  core.request.set_header(ctx, "X-MCP-RPC", nil)
  core.request.set_header(ctx, "X-MCP-Tool", nil)
  local auth = core.request.header(ctx, "Authorization")
  if auth then
    local key = auth:match("^[Bb]earer%s+(%S+)$")
    if key then core.request.set_header(ctx, "apikey", key) end
  end
  if core.request.get_method() ~= "POST" then return end
  local body = core.request.get_body(1048576, ctx)
  if not body then return end
  local msg = core.json.decode(body)
  if type(msg) ~= "table" then return end
  if msg[1] ~= nil then msg = msg[1] end
  if type(msg) ~= "table" then return end
  if type(msg.method) == "string" then core.request.set_header(ctx, "X-MCP-RPC", msg.method) end
  if msg.method == "tools/call" and type(msg.params) == "table" and type(msg.params.name) == "string" then
    core.request.set_header(ctx, "X-MCP-Tool", msg.params.name)
  end
end`;

/** loki-logger 로그 한 줄의 형식. 키 원문·Authorization 은 남기지 않습니다. */
const LOG_FORMAT = {
  msec: "$msec",
  method: "$request_method",
  uri: "$uri",
  status: "$status",
  latency: "$request_time",
  consumer: "$consumer_name",
  rpc: "$http_x_mcp_rpc",
  tool: "$http_x_mcp_tool",
  ip: "$http_x_forwarded_for",
  ua: "$http_user_agent",
  bytes_in: "$request_length",
  bytes_out: "$bytes_sent",
};

async function admin<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${env.apisixAdmin}/apisix/admin${path}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(10_000),
  });
  const text = await res.text();
  if (!res.ok && !(method === "DELETE" && res.status === 404)) {
    throw new Error(`APISIX Admin API ${method} ${path} → ${res.status}: ${text.slice(0, 300)}`);
  }
  return (text ? JSON.parse(text) : {}) as T;
}

function hostPort(url: string) {
  const u = new URL(url);
  return `${u.hostname}:${u.port || (u.protocol === "https:" ? 443 : 80)}`;
}

function mcpRoute(whitelist: string[]) {
  return {
    name: "Gemini MCP",
    desc: "MCP Streamable HTTP. API 키(key-auth) 필요. 대시보드가 관리합니다.",
    uris: ["/mcp", "/mcp/*"],
    priority: 10,
    upstream: {
      type: "roundrobin",
      nodes: { [hostPort(env.mcp)]: 1 },
      // 도구 실행(영상 분석, 이미지 생성 등)은 수 분이 걸릴 수 있습니다.
      timeout: { connect: 5, send: 600, read: 600 },
    },
    plugins: {
      "serverless-pre-function": { phase: "rewrite", functions: [PRE_FUNCTION] },
      "key-auth": { header: "apikey", hide_credentials: true },
      "consumer-restriction": {
        type: "consumer_name",
        whitelist: whitelist.length ? whitelist : [NO_ACTIVE_KEYS],
        rejected_code: 401,
        rejected_msg: "API key is disabled or expired",
      },
      // API 키는 MCP 서버로 넘기지 않습니다 (apikey 헤더는 hide_credentials 가 지움).
      // 대신 컨슈머 이름(k_<id>)을 넘겨 생성 미디어 메타데이터에 남깁니다. set 이라 클라이언트가 보낸 값은 덮어씁니다.
      "proxy-rewrite": { headers: { set: { "X-Mcp-Consumer": "$consumer_name" }, remove: ["Authorization"] } },
      "loki-logger": {
        endpoint_addrs: [env.loki],
        log_labels: { job: "apisix", kind: "request" },
        log_format: LOG_FORMAT,
        batch_max_size: 100,
        inactive_timeout: 1,
      },
    },
  };
}

/** 라우트를 (다시) 등록합니다. 대시보드 서버가 시작할 때 APISIX 가 준비될 때까지 재시도합니다. */
export async function ensureRoutes(): Promise<void> {
  await admin("PUT", `/routes/${MCP_ROUTE}`, mcpRoute(activeConsumers(await listConsumers())));
  await admin("PUT", "/routes/mcp-public", {
    name: "Gemini MCP public",
    desc: "내장 스킬 문서와 상태 확인 (인증 없음)",
    uris: ["/skill", "/health"],
    methods: ["GET", "HEAD"],
    priority: 10,
    upstream: { type: "roundrobin", nodes: { [hostPort(env.mcp)]: 1 } },
  });
  await admin("PUT", "/routes/dashboard", {
    name: "Dashboard",
    desc: "관리 대시보드 (Nuxt). 관리자 로그인은 대시보드가 처리합니다.",
    uri: "/*",
    priority: 0,
    upstream: { type: "roundrobin", nodes: { [env.dashboardUpstream]: 1 } },
  });
}

async function listConsumers(): Promise<Consumer[]> {
  const res = await admin<{ list?: { value: Consumer }[] }>("GET", "/consumers");
  return (res.list ?? []).map((item) => item.value).filter((c) => c.username.startsWith("k_"));
}

function toKey(c: Consumer, now = Date.now()): ApiKey {
  const expiresAt = c.labels?.expires ? Number(c.labels.expires) : null;
  const disabled = c.labels?.disabled === "true";
  return {
    id: c.username,
    name: c.desc ?? c.username,
    prefix: c.labels?.prefix ?? "",
    createdAt: (c.create_time ?? 0) * 1000,
    expiresAt,
    disabled,
    status: disabled ? "disabled" : expiresAt !== null && expiresAt <= now ? "expired" : "active",
  };
}

function activeConsumers(consumers: Consumer[]): string[] {
  return consumers.map((c) => toKey(c)).filter((k) => k.status === "active").map((k) => k.id);
}

/** 허용 목록을 현재 활성 키로 맞춥니다 (만료 처리 포함). 바뀐 경우에만 라우트를 갱신합니다. */
export async function syncWhitelist(): Promise<void> {
  const active = activeConsumers(await listConsumers()).sort();
  const route = await admin<{ value?: { plugins?: Record<string, { whitelist?: string[] }> } }>(
    "GET",
    `/routes/${MCP_ROUTE}`,
  );
  const current = [...(route.value?.plugins?.["consumer-restriction"]?.whitelist ?? [])].sort();
  const wanted = active.length ? active : [NO_ACTIVE_KEYS];
  if (JSON.stringify(current) !== JSON.stringify(wanted)) {
    await admin("PATCH", `/routes/${MCP_ROUTE}/plugins/consumer-restriction/whitelist`, wanted);
  }
}

export async function listKeys(): Promise<ApiKey[]> {
  return (await listConsumers()).map((c) => toKey(c)).sort((a, b) => b.createdAt - a.createdAt);
}

async function getConsumer(id: string): Promise<Consumer | null> {
  if (!/^k_[a-f0-9]+$/.test(id)) return null;
  try {
    return (await admin<{ value: Consumer }>("GET", `/consumers/${id}`)).value;
  } catch {
    return null;
  }
}

function newSecret() {
  const secret = KEY_PREFIX + randomToken(32);
  return { secret, prefix: secret.slice(0, KEY_PREFIX.length + 8) };
}

function labels(prefix: string, expiresAt: number | null, disabled: boolean): Record<string, string> {
  return {
    managed: "dashboard",
    prefix,
    ...(expiresAt !== null ? { expires: String(expiresAt) } : {}),
    ...(disabled ? { disabled: "true" } : {}),
  };
}

export async function createKey(name: string, expiresAt: number | null): Promise<{ key: ApiKey; secret: string }> {
  const id = `k_${Array.from(crypto.getRandomValues(new Uint8Array(6)), (b) => b.toString(16).padStart(2, "0")).join("")}`;
  const { secret, prefix } = newSecret();
  await putCredential(id, secret);
  const res = await admin<{ value: Consumer }>("PUT", "/consumers", {
    username: id,
    desc: name,
    labels: labels(prefix, expiresAt, false),
  });
  await syncWhitelist();
  return { key: toKey(res.value), secret };
}

/** 키 원문은 컨슈머와 분리된 credential 리소스에 둡니다. 컨슈머(이름·라벨)를 고쳐도 키는 건드리지 않습니다. */
async function putCredential(id: string, secret: string) {
  // credential 은 컨슈머가 있어야 만들 수 있으므로, 새 키는 컨슈머를 먼저 만듭니다 (이름·라벨은 호출자가 채움).
  if (!await getConsumer(id)) await admin("PUT", "/consumers", { username: id, labels: { managed: "dashboard" } });
  await admin("PUT", `/consumers/${id}/credentials/key`, { plugins: { "key-auth": { key: secret } } });
}

/** 이름·만료·활성 여부를 바꿉니다. 키(credential)는 그대로 둡니다. */
export async function updateKey(
  id: string,
  change: { name?: string; expiresAt?: number | null; disabled?: boolean },
): Promise<ApiKey | null> {
  const consumer = await getConsumer(id);
  if (!consumer) return null;
  const current = toKey(consumer);
  const res = await admin<{ value: Consumer }>("PUT", "/consumers", {
    username: id,
    desc: change.name ?? current.name,
    labels: labels(
      current.prefix,
      change.expiresAt === undefined ? current.expiresAt : change.expiresAt,
      change.disabled ?? current.disabled,
    ),
  });
  await syncWhitelist();
  return toKey(res.value);
}

/** 새 키를 발급하고 이전 키는 즉시 무효화합니다. */
export async function rotateKey(id: string): Promise<{ key: ApiKey; secret: string } | null> {
  const consumer = await getConsumer(id);
  if (!consumer) return null;
  const current = toKey(consumer);
  const { secret, prefix } = newSecret();
  await putCredential(id, secret);
  const res = await admin<{ value: Consumer }>("PUT", "/consumers", {
    username: id,
    desc: current.name,
    labels: labels(prefix, current.expiresAt, current.disabled),
  });
  return { key: toKey(res.value), secret };
}

export async function deleteKey(id: string): Promise<ApiKey | null> {
  const consumer = await getConsumer(id);
  if (!consumer) return null;
  await admin("DELETE", `/consumers/${id}`);
  await syncWhitelist();
  return toKey(consumer);
}

/** Admin API 가 응답하는지 (APISIX 와 etcd 상태). */
export async function pingAdmin(): Promise<void> {
  await admin("GET", "/routes/mcp");
}
