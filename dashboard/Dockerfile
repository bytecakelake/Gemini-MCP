# syntax=docker/dockerfile:1
# Gemini MCP dashboard — Nuxt 4 + Vuetify 4, Nitro deno-server 프리셋. 런타임은 Deno distroless + non-root.
# 빌드 컨텍스트: dashboard/

# ---------- build ----------
FROM denoland/deno:2.8.2 AS build
WORKDIR /app
COPY package.json deno.json deno.lock ./
RUN deno install --frozen
COPY . .
RUN deno task build && mkdir -p /data

# ---------- runtime ----------
FROM denoland/deno:distroless-2.8.2
WORKDIR /app
ENV DENO_NO_UPDATE_CHECK=1 \
    HOST=0.0.0.0 \
    PORT=3000 \
    DATA_DIR=/data
COPY --from=build /app/.output /app/.output
# 관리자 계정(비밀번호 해시)은 /data 볼륨에 저장됩니다.
COPY --from=build --chown=65532:65532 /data /data
VOLUME /data

USER 65532:65532
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD ["deno", "eval", "const r = await fetch('http://127.0.0.1:3000/api/admin/auth/state'); Deno.exit(r.ok ? 0 : 1)"]
ENTRYPOINT ["/tini", "--", "deno", "run", "--allow-net", "--allow-read=/app,/data", "--allow-write=/data", "--allow-env", "--unstable-byonm", "--unstable-node-globals", ".output/server/index.mjs"]
