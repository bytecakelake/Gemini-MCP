export default defineEventHandler(async (event) => {
  const body = await jsonBody(event);
  const name = str(body.name).trim();
  if (!name || name.length > 80) fail(400, "이름은 1~80자여야 합니다.");
  const { key, secret } = await createKey(name, parseExpiry(body) ?? null);
  await audit(clientIp(event), `API 키 발급: ${key.name} (${key.prefix}…)`);
  setResponseStatus(event, 201);
  return { key: { ...key, requestCount: 0, lastUsedAt: null }, secret };
});
