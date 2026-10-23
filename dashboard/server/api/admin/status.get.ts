export default defineEventHandler(async () => {
  const keys = await listKeys().catch(() => []);
  return {
    dashboard: {
      startedAt,
      uptimeSeconds: Math.round((Date.now() - startedAt) / 1000),
      logRetentionDays: env.retentionDays,
    },
    upstreams: healthStates,
    keys: {
      total: keys.length,
      active: keys.filter((k) => k.status === "active").length,
      disabled: keys.filter((k) => k.status === "disabled").length,
      expired: keys.filter((k) => k.status === "expired").length,
    },
  };
});
