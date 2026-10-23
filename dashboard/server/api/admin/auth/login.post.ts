export default defineEventHandler(async (event) => {
  const ip = clientIp(event);
  if (isLockedOut(ip)) fail(429, "로그인 시도 횟수를 초과했습니다. 5분 뒤 다시 시도하세요.");
  const body = await jsonBody(event);
  if (!await verifyPassword(str(body.password), ip)) {
    await audit(ip, "로그인 실패", "warn");
    fail(401, "비밀번호가 올바르지 않습니다.");
  }
  await audit(ip, "로그인");
  startSession(event, ip);
  return { ok: true };
});
