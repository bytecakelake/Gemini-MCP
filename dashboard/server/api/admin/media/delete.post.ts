/** 여러 미디어를 한 번에 삭제합니다. 본문: { names: string[] } (최대 200개). */
export default defineEventHandler(async (event) => {
  const body = await jsonBody(event);
  if (!Array.isArray(body.names) || body.names.length === 0) fail(400, "삭제할 미디어를 고르세요.");
  if (body.names.length > 200) fail(400, "한 번에 200개까지 삭제할 수 있습니다.");
  const names = [...new Set(body.names.map(mediaName))];

  const deleted: string[] = [];
  const failed: { name: string; error: string }[] = [];
  for (const name of names) {
    try {
      if (await deleteMedia(name)) deleted.push(name);
    } catch (err) {
      failed.push({ name, error: errorText(err) });
    }
  }
  if (deleted.length) {
    await audit(clientIp(event), `생성 미디어 ${deleted.length}개 삭제: ${deleted.slice(0, 10).join(", ")}${deleted.length > 10 ? " …" : ""}`, "warn");
  }
  return { deleted: deleted.length, failed };
});
