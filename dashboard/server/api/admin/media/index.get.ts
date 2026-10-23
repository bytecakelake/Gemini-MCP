/** 생성 미디어 목록 (최신순, 커서 페이지). 컨슈머 이름(k_<id>)을 API 키 이름으로 바꿔 줍니다. */
export default defineEventHandler(async (event) => {
  const query = getQuery(event);
  const cursor = typeof query.cursor === "string" && query.cursor ? query.cursor : undefined;
  const limit = Math.min(Math.max(Number(query.limit) || 50, 1), 200);
  const names = new Map((await listKeys().catch(() => [])).map((k) => [k.id, k.name]));
  try {
    const page = await listMedia(cursor, limit);
    return {
      items: page.items.map((item) => ({
        ...item,
        lastModified: Date.parse(item.lastModified),
        keyName: item.consumer ? names.get(item.consumer) ?? null : null,
      })),
      nextCursor: page.nextCursor ?? null,
    };
  } catch (err) {
    fail(502, `미디어 목록을 가져오지 못했습니다: ${errorText(err)}`);
  }
});
