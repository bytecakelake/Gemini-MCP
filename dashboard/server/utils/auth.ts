import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { H3Event } from "h3";

/**
 * 대시보드 관리자 인증. 관리자가 없으면 일회용 설정 코드를 서버 로그에 출력하고, 그 코드로 비밀번호를 만듭니다.
 * 비밀번호는 PBKDF2-SHA256 해시로 DATA_DIR/admin.json 에 저장하고, 세션은 메모리에 둡니다(재시작 시 다시 로그인).
 */
interface AdminRecord {
  hash: string;
  salt: string;
  iterations: number;
  createdAt: number;
  updatedAt: number;
}

export const SESSION_COOKIE = "gmcp_session";
const SESSION_TTL_MS = 12 * 60 * 60 * 1000;
const PBKDF2_ITERATIONS = 310_000;
const MAX_FAILURES = 5;
const LOCKOUT_MS = 5 * 60 * 1000;

const adminFile = () => join(env.dataDir, "admin.json");
const sessions = new Map<string, { createdAt: number; ip: string }>();
const failures = new Map<string, { count: number; until: number }>();
let setupCode: string | null = null;

function base64url(bytes: Uint8Array): string {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function randomToken(byteLength = 32): string {
  return base64url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function deriveHash(password: string, salt: string, iterations: number): Promise<string> {
  const material = await crypto.subtle.importKey("raw", new TextEncoder().encode(password), "PBKDF2", false, [
    "deriveBits",
  ]);
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: new TextEncoder().encode(salt), iterations },
    material,
    256,
  );
  return base64url(new Uint8Array(bits));
}

async function readAdmin(): Promise<AdminRecord | null> {
  try {
    return JSON.parse(await readFile(adminFile(), "utf8")) as AdminRecord;
  } catch {
    return null;
  }
}

async function savePassword(password: string, createdAt: number) {
  const salt = randomToken(16);
  const record: AdminRecord = {
    hash: await deriveHash(password, salt, PBKDF2_ITERATIONS),
    salt,
    iterations: PBKDF2_ITERATIONS,
    createdAt,
    updatedAt: Date.now(),
  };
  await mkdir(env.dataDir, { recursive: true });
  await writeFile(adminFile(), JSON.stringify(record), { mode: 0o600 });
}

export function validatePassword(password: unknown): string | null {
  if (typeof password !== "string" || password.length < 10) return "비밀번호는 10자 이상이어야 합니다.";
  if (password.length > 256) return "비밀번호가 너무 깁니다.";
  return null;
}

export async function isSetUp(): Promise<boolean> {
  return (await readAdmin()) !== null;
}

/** 관리자가 없으면 설정 코드를 만들어 로그로 알립니다 (docker logs gemini-mcp-dashboard). */
export async function ensureSetupCode(): Promise<void> {
  if (setupCode || await isSetUp()) return;
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  const chars = Array.from(crypto.getRandomValues(new Uint8Array(8)), (b) => alphabet[b % alphabet.length]).join("");
  setupCode = `${chars.slice(0, 4)}-${chars.slice(4)}`;
  console.log(
    [
      "",
      "==================================================================",
      "  대시보드 관리자 계정이 없습니다. 대시보드에 접속해 아래 설정 코드로",
      "  관리자 비밀번호를 만드세요. (코드는 대시보드를 재시작하면 바뀝니다)",
      `  Dashboard setup code: ${setupCode}`,
      "==================================================================",
      "",
    ].join("\n"),
  );
}

export function isLockedOut(ip: string): boolean {
  const entry = failures.get(ip);
  return !!entry && entry.count >= MAX_FAILURES && entry.until > Date.now();
}

function recordFailure(ip: string) {
  const entry = failures.get(ip);
  const count = entry && entry.until > Date.now() ? entry.count + 1 : 1;
  failures.set(ip, { count, until: Date.now() + LOCKOUT_MS });
}

export async function setupAdmin(code: string, password: string, ip: string): Promise<"ok" | "invalid-code" | "already"> {
  if (await isSetUp()) return "already";
  if (!setupCode || !timingSafeEqual(code.trim().toUpperCase(), setupCode)) {
    recordFailure(ip);
    return "invalid-code";
  }
  await savePassword(password, Date.now());
  setupCode = null;
  return "ok";
}

export async function verifyPassword(password: string, ip: string): Promise<boolean> {
  const admin = await readAdmin();
  const ok = !!admin && timingSafeEqual(await deriveHash(password, admin.salt, admin.iterations), admin.hash);
  if (ok) failures.delete(ip);
  else recordFailure(ip);
  return ok;
}

export async function changePassword(password: string): Promise<void> {
  const admin = await readAdmin();
  await savePassword(password, admin?.createdAt ?? Date.now());
  sessions.clear();
}

/** 관리자 계정이 지워졌는지(reset-admin) 함께 확인합니다. */
export async function hasSession(event: H3Event): Promise<boolean> {
  const id = getCookie(event, SESSION_COOKIE);
  const session = id ? sessions.get(id) : undefined;
  if (!session || session.createdAt + SESSION_TTL_MS < Date.now()) return false;
  if (!await isSetUp()) {
    sessions.clear();
    return false;
  }
  return true;
}

function isSecure(event: H3Event): boolean {
  return getRequestHeader(event, "x-forwarded-proto") === "https" || getRequestProtocol(event) === "https";
}

export function startSession(event: H3Event, ip: string) {
  const id = randomToken(32);
  sessions.set(id, { createdAt: Date.now(), ip });
  setCookie(event, SESSION_COOKIE, id, {
    httpOnly: true,
    sameSite: "strict",
    secure: isSecure(event),
    path: "/",
    maxAge: SESSION_TTL_MS / 1000,
  });
}

export function endSession(event: H3Event) {
  const id = getCookie(event, SESSION_COOKIE);
  if (id) sessions.delete(id);
  deleteCookie(event, SESSION_COOKIE, { path: "/" });
}


/** Caddy → APISIX 를 거치며 붙은 X-Forwarded-For 의 첫 값이 실제 클라이언트입니다. */
export function clientIp(event: H3Event): string {
  return getRequestHeader(event, "x-forwarded-for")?.split(",")[0]?.trim() || getRequestIP(event) || "";
}
