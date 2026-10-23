export default defineEventHandler(async (event) => {
  const ip = clientIp(event);
  if (isLockedOut(ip)) fail(429, "시도 횟수를 초과했습니다. 5분 뒤 다시 시도하세요.");
  const body = await jsonBody(event);
  const invalid = validatePassword(body.password);
  if (invalid) fail(400, invalid);
  const result = await setupAdmin(str(body.code), str(body.password), ip);
  if (result === "already") fail(409, "이미 관리자 계정이 있습니다.");
  if (result === "invalid-code") {
    await audit(ip, "관리자 설정 실패: 잘못된 설정 코드", "warn");
    fail(403, "설정 코드가 올바르지 않습니다. 대시보드 로그(docker logs gemini-mcp-dashboard)의 코드를 확인하세요.");
  }
  await audit(ip, "관리자 계정 생성");
  startSession(event, ip);
  return { ok: true };
});
