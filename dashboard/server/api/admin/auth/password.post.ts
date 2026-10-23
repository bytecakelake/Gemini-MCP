export default defineEventHandler(async (event) => {
  const ip = clientIp(event);
  const body = await jsonBody(event);
  if (!await verifyPassword(str(body.current), ip)) fail(403, "현재 비밀번호가 올바르지 않습니다.");
  const invalid = validatePassword(body.next);
  if (invalid) fail(400, invalid);
  await changePassword(str(body.next));
  await audit(ip, "관리자 비밀번호 변경 (모든 세션 로그아웃)");
  startSession(event, ip);
  return { ok: true };
});
