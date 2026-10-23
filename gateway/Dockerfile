# syntax=docker/dockerfile:1
# Gemini MCP gateway — Deno. 런타임은 distroless(셸 없음) + non-root(65532).
# 빌드 컨텍스트: gateway/

# ---------- build: 의존성 캐시, 데이터 폴더 준비 ----------
FROM denoland/deno:2.8.2 AS build
WORKDIR /app
ENV DENO_DIR=/deno-dir
COPY deno.json main.ts ./
COPY src/ src/
RUN deno cache main.ts && mkdir -p /data

# ---------- runtime ----------
FROM denoland/deno:distroless-2.8.2
WORKDIR /app
ENV DENO_DIR=/deno-dir \
    DENO_NO_UPDATE_CHECK=1 \
    GATEWAY_HOST=0.0.0.0 \
    GATEWAY_PORT=8080 \
    GATEWAY_DATA_DIR=/data
COPY --from=build --chown=65532:65532 /deno-dir /deno-dir
COPY --from=build --chown=65532:65532 /data /data
COPY --from=build /app /app

# Deno KV(API 키 해시, 관리자 계정, 로그, 지표)는 /data 볼륨에 저장됩니다.
VOLUME /data
USER 65532:65532
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD ["deno", "eval", "const r = await fetch('http://127.0.0.1:8080/healthz'); Deno.exit(r.ok ? 0 : 1)"]
ENTRYPOINT ["/tini", "--", "deno", "run", "--allow-net", "--allow-env", "--allow-read=/data", "--allow-write=/data", "main.ts"]
