export default defineEventHandler(async (event) => {
  const body = await jsonBody(event);
  const name = body.name === undefined ? undefined : str(body.name).trim();
  if (name !== undefined && (!name || name.length > 80)) fail(400, "이름은 1~80자여야 합니다.");
  const key = await updateKey(getRouterParam(event, "id") ?? "", {
    name,
    disabled: typeof body.disabled === "boolean" ? body.disabled : undefined,
    expiresAt: parseExpiry(body),
  });
  if (!key) fail(404, "키를 찾을 수 없습니다.");
  const change = typeof body.disabled === "boolean" ? (body.disabled ? "비활성화" : "활성화") : "수정";
  await audit(clientIp(event), `API 키 ${change}: ${key.name} (${key.prefix}…)`);
  return { key };
});
