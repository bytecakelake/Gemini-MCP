/** MCP 서버의 tools/list (stateless 서버라 initialize 없이 호출). 로그 화면의 도구 필터 후보로 씁니다. */
export default defineEventHandler(async () => {
  try {
    const res = await fetch(`${env.mcp}/mcp`, {
      method: "POST",
      headers: { "content-type": "application/json", accept: "application/json, text/event-stream" },
      body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list", params: {} }),
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const text = await res.text();
    const payload = res.headers.get("content-type")?.includes("text/event-stream")
      ? text.split("\n").filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).join("")
      : text;
    const message = JSON.parse(payload);
    if (message.error) throw new Error(message.error.message);
    const tools: { name: string; title?: string; description?: string }[] = message.result?.tools ?? [];
    return { items: tools.map(({ name, title, description }) => ({ name, title, description })) };
  } catch (err) {
    fail(502, `MCP 서버에서 도구 목록을 가져오지 못했습니다: ${errorText(err)}`);
  }
});
