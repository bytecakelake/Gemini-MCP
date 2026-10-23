export default defineEventHandler(async (event) => {
  const query = getQuery(event);
  const s = (v: unknown) => (typeof v === "string" && v ? v : undefined);
  const names = new Map((await listKeys().catch(() => [])).map((k) => [k.id, k.name]));
  try {
    return await queryLogs(
      {
        kind: s(query.kind),
        level: s(query.level),
        keyId: s(query.keyId),
        tool: s(query.tool),
        q: s(query.q),
        before: s(query.before),
        limit: Math.min(Math.max(Number(query.limit) || 100, 1), 500),
      },
      names,
    );
  } catch (err) {
    fail(502, `로그를 조회하지 못했습니다: ${errorText(err)}`);
  }
});
