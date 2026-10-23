import { randomToken, sha256Hex } from "./util.ts";

/** 저장되는 API 키 레코드. 평문 키는 발급/회전 응답에서 한 번만 보여 주고 SHA-256 해시만 보관합니다. */
export interface ApiKey {
  id: string;
  name: string;
  /** 목록에서 키를 구분하기 위한 앞부분 (예: gmcp_AbCdEf12). */
  prefix: string;
  hash: string;
  createdAt: number;
  expiresAt: number | null;
  disabled: boolean;
  /** null 이면 모든 도구 허용. */
  allowedTools: string[] | null;
  lastUsedAt: number | null;
  requestCount: number;
}

export type PublicApiKey = Omit<ApiKey, "hash"> & { status: "active" | "disabled" | "expired" };

export interface KeyInput {
  name?: string;
  expiresAt?: number | null;
  disabled?: boolean;
  allowedTools?: string[] | null;
}

const SECRET_PREFIX = "gmcp_";
const USAGE_FLUSH_MS = 10_000;

export function keyStatus(key: ApiKey, now = Date.now()): PublicApiKey["status"] {
  if (key.disabled) return "disabled";
  if (key.expiresAt !== null && key.expiresAt <= now) return "expired";
  return "active";
}

export function toPublic(key: ApiKey): PublicApiKey {
  const { hash: _hash, ...rest } = key;
  return { ...rest, status: keyStatus(key) };
}

function newSecret(): string {
  return SECRET_PREFIX + randomToken(32);
}

function normalizeTools(tools: string[] | null | undefined): string[] | null {
  if (!tools) return null;
  const list = [...new Set(tools.map((t) => t.trim()).filter(Boolean))];
  return list.length > 0 ? list : null;
}

/**
 * API 키 저장소. 인증은 매 요청마다 일어나므로 전체 키를 메모리에 캐시하고(해시 → 키),
 * 사용 통계(lastUsedAt, requestCount)는 메모리에서 누적한 뒤 주기적으로 KV 에 기록합니다.
 */
export class KeyStore {
  #byId = new Map<string, ApiKey>();
  #byHash = new Map<string, ApiKey>();
  #dirty = new Set<string>();
  #timer?: ReturnType<typeof setInterval>;

  constructor(private kv: Deno.Kv) {}

  async load(): Promise<void> {
    for await (const entry of this.kv.list<ApiKey>({ prefix: ["apikey"] })) this.#cache(entry.value);
    this.#timer = setInterval(() => this.flush(), USAGE_FLUSH_MS);
  }

  async close(): Promise<void> {
    clearInterval(this.#timer);
    await this.flush();
  }

  list(): PublicApiKey[] {
    return [...this.#byId.values()].sort((a, b) => b.createdAt - a.createdAt).map(toPublic);
  }

  get(id: string): ApiKey | undefined {
    return this.#byId.get(id);
  }

  /** Bearer 로 받은 평문 키를 검증합니다. 비활성/만료 키도 돌려주므로 호출자가 keyStatus 로 판단합니다. */
  async find(secret: string): Promise<ApiKey | undefined> {
    if (!secret.startsWith(SECRET_PREFIX)) return undefined;
    return this.#byHash.get(await sha256Hex(secret));
  }

  async create(input: KeyInput & { name: string }): Promise<{ key: ApiKey; secret: string }> {
    const secret = newSecret();
    const key: ApiKey = {
      id: crypto.randomUUID(),
      name: input.name.trim(),
      prefix: secret.slice(0, SECRET_PREFIX.length + 8),
      hash: await sha256Hex(secret),
      createdAt: Date.now(),
      expiresAt: input.expiresAt ?? null,
      disabled: false,
      allowedTools: normalizeTools(input.allowedTools),
      lastUsedAt: null,
      requestCount: 0,
    };
    await this.kv.set(["apikey", key.id], key);
    this.#cache(key);
    return { key, secret };
  }

  async update(id: string, input: KeyInput): Promise<ApiKey | undefined> {
    const current = this.#byId.get(id);
    if (!current) return undefined;
    const next: ApiKey = {
      ...current,
      name: input.name?.trim() || current.name,
      expiresAt: input.expiresAt === undefined ? current.expiresAt : input.expiresAt,
      disabled: input.disabled ?? current.disabled,
      allowedTools: input.allowedTools === undefined ? current.allowedTools : normalizeTools(input.allowedTools),
    };
    await this.kv.set(["apikey", id], next);
    this.#uncache(current);
    this.#cache(next);
    return next;
  }

  /** 새 평문 키를 발급하고 이전 키는 즉시 무효화합니다. 이름·권한·통계는 유지됩니다. */
  async rotate(id: string): Promise<{ key: ApiKey; secret: string } | undefined> {
    const current = this.#byId.get(id);
    if (!current) return undefined;
    const secret = newSecret();
    const next: ApiKey = {
      ...current,
      prefix: secret.slice(0, SECRET_PREFIX.length + 8),
      hash: await sha256Hex(secret),
    };
    await this.kv.set(["apikey", id], next);
    this.#uncache(current);
    this.#cache(next);
    return { key: next, secret };
  }

  async delete(id: string): Promise<ApiKey | undefined> {
    const current = this.#byId.get(id);
    if (!current) return undefined;
    await this.kv.delete(["apikey", id]);
    this.#uncache(current);
    this.#dirty.delete(id);
    return current;
  }

  recordUse(key: ApiKey): void {
    key.lastUsedAt = Date.now();
    key.requestCount++;
    this.#dirty.add(key.id);
  }

  async flush(): Promise<void> {
    const ids = [...this.#dirty];
    this.#dirty.clear();
    for (const id of ids) {
      const key = this.#byId.get(id);
      if (key) await this.kv.set(["apikey", id], key);
    }
  }

  #cache(key: ApiKey) {
    this.#byId.set(key.id, key);
    this.#byHash.set(key.hash, key);
  }

  #uncache(key: ApiKey) {
    this.#byId.delete(key.id);
    this.#byHash.delete(key.hash);
  }
}
