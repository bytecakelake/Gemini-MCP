/**
 * 미디어 원본 (미리보기/다운로드, ?download=1 이면 첨부 파일로).
 * 생성 미디어는 이미지·음성·영상뿐이지만, 대시보드와 같은 출처에서 열리므로 스크립트가 실행되지 않게 CSP sandbox 를 겁니다.
 */
export default defineEventHandler(async (event) => {
  const name = mediaName(getRouterParam(event, "name"));
  const res = await openMedia(name).catch((err) => fail(502, `미디어를 가져오지 못했습니다: ${errorText(err)}`));
  if (res.status === 404) fail(404, "미디어를 찾을 수 없습니다.");
  if (!res.ok || !res.body) fail(502, `미디어를 가져오지 못했습니다: HTTP ${res.status}`);

  const type = res.headers.get("content-type") ?? "application/octet-stream";
  const inline = /^(image|audio|video)\//.test(type) && getQuery(event).download === undefined;
  setResponseHeaders(event, {
    "content-type": inline ? type : "application/octet-stream",
    "content-disposition": `${inline ? "inline" : "attachment"}; filename="${name}"`,
    "content-security-policy": "sandbox; default-src 'none'; img-src 'self'; media-src 'self'",
    "cache-control": "private, max-age=3600",
  });
  const length = res.headers.get("content-length");
  if (length) setResponseHeader(event, "content-length", Number(length));
  return res.body;
});
