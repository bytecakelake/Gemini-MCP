const HOP_BY_HOP = [
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
  "host",
  "content-length",
];

export interface ForwardOptions {
  /** 업스트림에 넘기지 않을 요청 헤더 (소문자) */
  dropHeaders?: string[];
  /** 이미 읽은 요청 본문 */
  body?: Uint8Array<ArrayBuffer>;
  clientIp?: string;
}

/**
 * 요청을 업스트림으로 그대로 전달하고 응답을 스트리밍으로 돌려줍니다 (SSE 포함).
 * 업스트림 압축은 끄고(accept-encoding: identity) 압축은 앞단 Caddy 에 맡깁니다:
 * fetch 가 응답을 자동으로 풀어 Content-Encoding 헤더와 본문이 어긋나는 것을 막기 위해서입니다.
 */
export async function forward(req: Request, upstream: string, options: ForwardOptions = {}): Promise<Response> {
  const url = new URL(req.url);
  const headers = new Headers();
  const drop = new Set([...HOP_BY_HOP, ...(options.dropHeaders ?? [])]);
  for (const [name, value] of req.headers) if (!drop.has(name)) headers.set(name, value);
  headers.set("accept-encoding", "identity");
  if (options.clientIp && !headers.has("x-forwarded-for")) headers.set("x-forwarded-for", options.clientIp);
  if (!headers.has("x-forwarded-host")) headers.set("x-forwarded-host", url.host);
  if (!headers.has("x-forwarded-proto")) headers.set("x-forwarded-proto", url.protocol.replace(":", ""));

  const hasBody = req.method !== "GET" && req.method !== "HEAD";
  const res = await fetch(upstream + url.pathname + url.search, {
    method: req.method,
    headers,
    body: hasBody ? (options.body ?? req.body) : undefined,
    redirect: "manual",
    signal: req.signal,
  });

  const responseHeaders = new Headers(res.headers);
  for (const name of HOP_BY_HOP) responseHeaders.delete(name);
  return new Response(res.body, { status: res.status, statusText: res.statusText, headers: responseHeaders });
}

export function badGateway(target: string, err: unknown): Response {
  const message = err instanceof Error ? err.message : String(err);
  return new Response(`Bad gateway: ${target} is unavailable (${message})`, {
    status: 502,
    headers: { "content-type": "text/plain; charset=utf-8" },
  });
}
