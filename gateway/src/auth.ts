import { base64url, randomToken, timingSafeEqual } from "./util.ts";

/**
 * 대시보드 관리자 인증. 환경 변수 대신 최초 실행 시 게이트웨이 로그에 출력되는 일회용 설정 코드로
 * 관리자 비밀번호를 만들고, 이후에는 비밀번호 로그인 → HttpOnly 세션 쿠키를 사용합니다.
 */
interface AdminRecord {
  hash: string;
  salt: string;
  iterations: number;
  createdAt: number;
  updatedAt: number;
}

interface Session {
  createdAt: number;
  ip: string;
}

export const SESSION_COOKIE = "gmcp_session";
export const SESSION_TTL_MS = 12 * 60 * 60 * 1000;
const PBKDF2_ITERATIONS = 310_000;
const MIN_PASSWORD_LENGTH = 10;
const MAX_FAILURES = 5;
const LOCKOUT_MS = 5 * 60 * 1000;

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

export function validatePassword(password: unknown): string | null {
  if (typeof password !== "string" || password.length < MIN_PASSWORD_LENGTH) {
    return `비밀번호는 ${MIN_PASSWORD_LENGTH}자 이상이어야 합니다.`;
  }
  if (password.length > 256) return "비밀번호가 너무 깁니다.";
  return null;
}

export class AdminAuth {
  #setupCode: string | null = null;
  #failures = new Map<string, { count: number; until: number }>();

  constructor(private kv: Deno.Kv, private onSetupCode: (code: string) => void = printSetupCode) {}

  async isSetUp(): Promise<boolean> {
    return (await this.kv.get<AdminRecord>(["admin"])).value !== null;
  }

  /** 관리자가 없으면 설정 코드를 만들어 로그로 알립니다 (이미 있으면 그대로). */
  async ensureSetupCode(): Promise<void> {
    if (this.#setupCode || await this.isSetUp()) return;
    this.#setupCode = readableCode();
    this.onSetupCode(this.#setupCode);
  }

  async setup(code: string, password: string, ip: string): Promise<"ok" | "invalid-code" | "already"> {
    if (await this.isSetUp()) return "already";
    if (!this.#setupCode || !timingSafeEqual(code.trim().toUpperCase(), this.#setupCode)) {
      this.#recordFailure(ip);
      return "invalid-code";
    }
    await this.#savePassword(password, Date.now());
    this.#setupCode = null;
    return "ok";
  }

  isLockedOut(ip: string): boolean {
    const entry = this.#failures.get(ip);
    return !!entry && entry.count >= MAX_FAILURES && entry.until > Date.now();
  }

  async verifyPassword(password: string, ip: string): Promise<boolean> {
    const admin = (await this.kv.get<AdminRecord>(["admin"])).value;
    const ok = !!admin && timingSafeEqual(await deriveHash(password, admin.salt, admin.iterations), admin.hash);
    if (ok) this.#failures.delete(ip);
    else this.#recordFailure(ip);
    return ok;
  }

  #recordFailure(ip: string) {
    const entry = this.#failures.get(ip);
    const count = entry && entry.until > Date.now() ? entry.count + 1 : 1;
    this.#failures.set(ip, { count, until: Date.now() + LOCKOUT_MS });
  }

  async changePassword(password: string): Promise<void> {
    const admin = (await this.kv.get<AdminRecord>(["admin"])).value;
    await this.#savePassword(password, admin?.createdAt ?? Date.now());
    await this.revokeAllSessions();
  }

  async createSession(ip: string): Promise<string> {
    const id = randomToken(32);
    await this.kv.set(["session", id], { createdAt: Date.now(), ip } satisfies Session, { expireIn: SESSION_TTL_MS });
    return id;
  }

  async hasSession(id: string | undefined): Promise<boolean> {
    if (!id) return false;
    const session = (await this.kv.get<Session>(["session", id])).value;
    // expireIn 은 만료 직후 바로 지워진다는 보장이 없으므로 직접 확인합니다.
    return !!session && session.createdAt + SESSION_TTL_MS > Date.now();
  }

  async deleteSession(id: string | undefined): Promise<void> {
    if (id) await this.kv.delete(["session", id]);
  }

  async revokeAllSessions(): Promise<void> {
    for await (const entry of this.kv.list({ prefix: ["session"] })) await this.kv.delete(entry.key);
  }

  /** 비밀번호를 잊었을 때: 관리자 계정과 세션을 지웁니다. 다음 접속 시 새 설정 코드가 로그에 출력됩니다. */
  async reset(): Promise<void> {
    await this.kv.delete(["admin"]);
    await this.revokeAllSessions();
    this.#setupCode = null;
  }

  async #savePassword(password: string, createdAt: number) {
    const salt = randomToken(16);
    const record: AdminRecord = {
      hash: await deriveHash(password, salt, PBKDF2_ITERATIONS),
      salt,
      iterations: PBKDF2_ITERATIONS,
      createdAt,
      updatedAt: Date.now(),
    };
    await this.kv.set(["admin"], record);
  }
}

/** 헷갈리는 문자(0/O, 1/I)를 뺀 32자 알파벳으로 XXXX-XXXX 형식 코드를 만듭니다 (40비트). */
function readableCode(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  const chars = Array.from(crypto.getRandomValues(new Uint8Array(8)), (b) => alphabet[b % alphabet.length]).join("");
  return `${chars.slice(0, 4)}-${chars.slice(4)}`;
}

function printSetupCode(code: string) {
  console.log(
    [
      "",
      "==================================================================",
      "  대시보드 관리자 계정이 없습니다. 대시보드에 접속해 아래 설정 코드로",
      "  관리자 비밀번호를 만드세요. (코드는 게이트웨이를 재시작하면 바뀝니다)",
      `  Dashboard setup code: ${code}`,
      "==================================================================",
      "",
    ].join("\n"),
  );
}

export function readCookie(req: Request, name: string): string | undefined {
  for (const part of req.headers.get("cookie")?.split(";") ?? []) {
    const [k, ...v] = part.trim().split("=");
    if (k === name) return v.join("=");
  }
  return undefined;
}

export function sessionCookie(id: string, secure: boolean, maxAgeSeconds = SESSION_TTL_MS / 1000): string {
  return [
    `${SESSION_COOKIE}=${id}`,
    "Path=/",
    "HttpOnly",
    "SameSite=Strict",
    `Max-Age=${maxAgeSeconds}`,
    ...(secure ? ["Secure"] : []),
  ].join("; ");
}
