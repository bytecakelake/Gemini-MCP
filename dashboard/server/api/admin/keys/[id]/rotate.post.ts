export default defineEventHandler(async (event) => {
  const result = await rotateKey(getRouterParam(event, "id") ?? "");
  if (!result) fail(404, "키를 찾을 수 없습니다.");
  await audit(clientIp(event), `API 키 재발급: ${result.key.name} (${result.key.prefix}…)`);
  return { key: { ...result.key, requestCount: 0, lastUsedAt: null }, secret: result.secret };
});
