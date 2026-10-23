/**
 * 생성 미디어 관리. MCP 서버의 내부 API(/internal/media, backend 네트워크)를 거쳐 S3 저장소를 다룹니다.
 * S3 접속 정보는 MCP 서버만 갖고, 대시보드는 그 결과를 보여 주고 관리자의 삭제 요청을 전달합니다.
 */

export interface MediaObject {
  name: string;
  size: number;
  mimeType: string;
  lastModified: string;
  tool?: string;
  model?: string;
  prompt?: string;
  /** APISIX 컨슈머 이름 (k_<id>) */
  consumer?: string;
  interactionId?: string;
}

export interface MediaPage {
  items: MediaObject[];
  nextCursor?: string;
}

export interface MediaStats {
  count: number;
  bytes: number;
  store: string;
}

/** 생성 시 만든 이름 형식만 받습니다 (경로 탈출 방지). MCP 서버도 같은 규칙으로 다시 검사합니다. */
export function mediaName(value: unknown): string {
  const name = typeof value === "string" ? value : "";
  if (!/^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$/.test(name)) fail(400, "잘못된 미디어 이름입니다.");
  return name;
}

async function internal(path: string, init?: RequestInit): Promise<Response> {
  const res = await fetch(`${env.mcp}/internal/media${path}`, { ...init, signal: AbortSignal.timeout(30_000) });
  if (res.status === 503) fail(503, "MCP 서버에 미디어 저장소(S3)가 설정되지 않았습니다.");
  return res;
}

async function json<T>(res: Response, what: string): Promise<T> {
  if (!res.ok) throw new Error(`${what} → HTTP ${res.status}: ${(await res.text()).slice(0, 300)}`);
  return await res.json() as T;
}

export async function listMedia(cursor: string | undefined, limit: number): Promise<MediaPage> {
  const query = new URLSearchParams({ limit: String(limit) });
  if (cursor) query.set("cursor", cursor);
  return json<MediaPage>(await internal(`?${query}`), "미디어 목록");
}

export async function mediaStats(): Promise<MediaStats> {
  return json<MediaStats>(await internal("/stats"), "미디어 통계");
}

/** @returns 지웠으면 true, 이미 없으면 false */
export async function deleteMedia(name: string): Promise<boolean> {
  const res = await internal(`/${encodeURIComponent(name)}`, { method: "DELETE" });
  if (res.status === 404) return false;
  if (!res.ok) throw new Error(`미디어 삭제 → HTTP ${res.status}: ${(await res.text()).slice(0, 300)}`);
  return true;
}

/** 미리보기/다운로드용 원본. MCP 서버의 /mcp/media/{name} 을 내부 주소로 직접 부릅니다 (게이트웨이 인증 불필요). */
export async function openMedia(name: string): Promise<Response> {
  return await fetch(`${env.mcp}/mcp/media/${encodeURIComponent(name)}`, { signal: AbortSignal.timeout(120_000) });
}
