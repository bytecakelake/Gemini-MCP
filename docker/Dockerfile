# syntax=docker/dockerfile:1
# Gemini MCP Server — Native AOT 빌드 → chiseled(셸 없음, non-root) 런타임 이미지

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
ARG TARGETARCH
WORKDIR /src

# 의존성 복원 레이어를 소스와 분리해 캐시합니다.
COPY src/GeminiMcp/GeminiMcp.csproj src/GeminiMcp/
RUN RID=linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet restore src/GeminiMcp/GeminiMcp.csproj -r $RID

COPY src/ src/
RUN RID=linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet publish src/GeminiMcp/GeminiMcp.csproj -c Release -r $RID --no-restore -o /app \
 && rm -f /app/*.dbg /app/*.pdb /app/*.json \
 && mkdir -p /data/output

# ---------- runtime ----------
# runtime-deps: AOT 바이너리에 필요한 최소 OS 라이브러리 + CA 인증서만 포함 (ICU 불필요: InvariantGlobalization)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/GeminiMcp /app/GeminiMcp
COPY --from=build --chown=1654:1654 /data /data

ENV ASPNETCORE_URLS=http://+:5100 \
    ASPNETCORE_HTTP_PORTS= \
    HEALTHCHECK_URL=http://127.0.0.1:5100/health \
    GEMINI_MCP_OUTPUT_DIR=/data/output

EXPOSE 5100
USER 1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 CMD ["/app/GeminiMcp", "--healthcheck"]
ENTRYPOINT ["/app/GeminiMcp"]
