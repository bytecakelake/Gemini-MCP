export default defineEventHandler(async (event) => {
  const setUp = await isSetUp();
  if (!setUp) await ensureSetupCode();
  return { setupRequired: !setUp, authenticated: setUp && await hasSession(event) };
});
