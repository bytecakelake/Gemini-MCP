export default defineEventHandler(async (event) => {
  const range = String(getQuery(event).range ?? "24h") as RangeName;
  if (!(range in RANGES)) fail(400, `range 는 ${Object.keys(RANGES).join(", ")} 중 하나여야 합니다.`);
  const [summary, keys] = await Promise.all([
    metrics(range).catch((err) => fail(502, `지표를 계산하지 못했습니다: ${errorText(err)}`)),
    listKeys().catch(() => []),
  ]);
  const names = new Map(keys.map((k) => [k.id, k.name]));
  return { ...summary, keys: summary.keys.map((k) => ({ ...k, name: names.get(k.id) ?? `${k.id} (삭제된 키)` })) };
});
