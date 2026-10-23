export default defineEventHandler(async (event) => {
  const key = await deleteKey(getRouterParam(event, "id") ?? "");
  if (!key) fail(404, "키를 찾을 수 없습니다.");
  await audit(clientIp(event), `API 키 삭제: ${key.name} (${key.prefix}…)`, "warn");
  return { ok: true };
});
