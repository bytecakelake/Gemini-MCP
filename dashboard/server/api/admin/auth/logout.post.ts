export default defineEventHandler(async (event) => {
  endSession(event);
  await audit(clientIp(event), "로그아웃");
  return { ok: true };
});
