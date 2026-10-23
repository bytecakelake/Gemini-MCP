export default defineEventHandler(async () => {
  const [keys, usage] = await Promise.all([listKeys(), keyUsage().catch(() => new Map())]);
  return { items: keys.map((k) => ({ requestCount: 0, lastUsedAt: null, ...k, ...usage.get(k.id) })) };
});
