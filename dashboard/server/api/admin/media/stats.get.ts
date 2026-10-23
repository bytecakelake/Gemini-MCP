export default defineEventHandler(async () => {
  try {
    return await mediaStats();
  } catch (err) {
    fail(502, `미디어 통계를 가져오지 못했습니다: ${errorText(err)}`);
  }
});
