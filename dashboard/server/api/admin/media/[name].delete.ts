export default defineEventHandler(async (event) => {
  const name = mediaName(getRouterParam(event, "name"));
  const deleted = await deleteMedia(name).catch((err) => fail(502, `미디어를 삭제하지 못했습니다: ${errorText(err)}`));
  if (!deleted) fail(404, "미디어를 찾을 수 없습니다.");
  await audit(clientIp(event), `생성 미디어 삭제: ${name}`, "warn");
  return { ok: true };
});
