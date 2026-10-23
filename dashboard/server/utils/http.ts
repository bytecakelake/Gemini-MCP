import type { H3Event } from "h3";

/** 클라이언트가 { error } 로 읽는 오류 응답. */
export function fail(statusCode: number, error: string): never {
  throw createError({ statusCode, message: error, data: { error } });
}

export async function jsonBody(event: H3Event): Promise<Record<string, unknown>> {
  const body = await readBody(event).catch(() => fail(400, "잘못된 JSON 입니다."));
  return body && typeof body === "object" && !Array.isArray(body) ? body as Record<string, unknown> : {};
}

export const str = (value: unknown) => (typeof value === "string" ? value : "");

export async function audit(ip: string, message: string, level: "info" | "warn" = "info") {
  await pushLog("audit", level, message, ip);
}

/** 요청 본문의 expiresInDays → 만료 시각. 키가 없으면 undefined(변경 없음), null/0 이면 만료 없음. */
export function parseExpiry(body: Record<string, unknown>): number | null | undefined {
  if (!("expiresInDays" in body)) return undefined;
  const days = Number(body.expiresInDays);
  return body.expiresInDays === null || !days ? null : Date.now() + days * DAY_MS;
}

export const errorText = (err: unknown) => (err instanceof Error ? err.message : String(err));
