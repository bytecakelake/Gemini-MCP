# Gemini MCP Server (C# / .NET 10 / Native AOT) + Gateway + Dashboard

Claude 가 직접 할 수 없거나 약한 작업을 Google Gemini API 에 위임하는 **HTTP(Streamable HTTP) MCP 서버**와,
그 앞에서 **API 키 인증·로그·모니터링**을 맡는 **gateway**, 이를 관리하는 **Material Design 대시보드**로 구성됩니다.
사용법을 알려주는 **스킬(SKILL.md)이 바이너리에 내장**되어 있습니다.

```
클라이언트 ──HTTPS──▶ Caddy ──▶ gateway (Deno) ─┬─ /mcp, /skill, /health ─▶ gemini-mcp (.NET AOT)
                                              ├─ /api/admin            ─▶ 관리 API (대시보드 세션)
                                              └─ 그 외                  ─▶ dashboard (Nuxt 4 + Vuetify 4, Deno)
```

| 서비스 | 기술 | 역할 |
|---|---|---|
| `gemini-mcp` | C# / .NET 10 Native AOT | MCP 도구 9종. 인증하지 않으며 내부 네트워크에만 노출 |
| `gateway` | Deno + TypeScript | API 키 검증, 도구별 권한, 요청 로그, 지표 집계, 업스트림 상태 확인, 라우팅. 데이터는 Deno KV |
| `dashboard` | Nuxt 4 + Vuetify 4(Material Design) + TypeScript, Nitro `deno-server` | 모니터링, 로그, API 키 관리, 설정 |
| `caddy` | Caddy 2 | TLS 종단, zstd/gzip 압축, HTTP/3 |

## 제공 도구

| 도구 | 용도 |
|---|---|
| `gemini_web_search` | Google 검색 그라운딩 — 최신 정보 + 출처 |
| `gemini_read_urls` | URL Context — 웹 페이지(최대 20개) 읽기·요약·비교 |
| `gemini_analyze_media` | 영상·오디오·이미지·PDF·YouTube 분석, 전사(받아쓰기) |
| `gemini_generate_image` | 이미지 생성/편집 (Nano Banana) → 파일 저장 |
| `gemini_text_to_speech` | 음성 합성 → WAV 저장 |
| `gemini_run_code` | Gemini Python 샌드박스 코드 실행 |
| `gemini_ask` | 세컨드 오피니언, 대용량 컨텍스트 분석, JSON Schema 구조화 출력 |
| `gemini_list_models` | 사용 가능한 모델 목록 |
| `gemini_usage_guide` | 내장 스킬(사용 설명서) 반환 |

## 내장 스킬

같은 SKILL.md 를 여러 경로로 제공합니다.

- MCP `initialize` 의 `instructions` (요약본, 연결 즉시 Claude 에게 전달)
- MCP 프롬프트 `gemini_skill` (Claude Code 에서 `/mcp__gemini__gemini_skill`)
- MCP 리소스 `gemini://skill/SKILL.md`
- 도구 `gemini_usage_guide`
- HTTP `GET /skill`
- Claude Code 스킬로 설치: `GeminiMcp.exe --install-skill` → `~/.claude/skills/gemini-mcp/SKILL.md`

## 인증

인증은 모두 **gateway** 가 담당하며, 인증 정보를 환경 변수로 받지 않습니다.

- **MCP 클라이언트**: 대시보드에서 발급한 API 키를 `Authorization: Bearer gmcp_…` 헤더로 보냅니다.
  키 원문은 발급(또는 재발급) 직후 한 번만 보여 주고 서버에는 SHA-256 해시만 저장합니다.
  키마다 이름, 만료일, 활성/비활성, **허용 도구 목록**(그 외 도구의 `tools/call` 은 403)을 설정할 수 있습니다.
  키가 없거나 틀리거나 만료·비활성이면 `401` + `WWW-Authenticate: Bearer` 를 돌려줍니다. 키는 MCP 서버로 전달하지 않습니다.
- **대시보드 관리자**: 최초 실행 시 gateway 로그에 일회용 **설정 코드**(`Dashboard setup code: XXXX-XXXX`)가 출력됩니다.
  대시보드에 접속해 이 코드로 관리자 비밀번호(10자 이상, PBKDF2-SHA256)를 만들면, 이후에는 비밀번호로 로그인합니다.
  세션은 `HttpOnly; SameSite=Strict` 쿠키(12시간)이고, 상태를 바꾸는 요청은 JSON 본문 + 같은 Origin 만 받습니다.
  로그인·설정 코드 입력은 IP 당 5회 실패 시 5분간 잠깁니다. 비밀번호를 잊었다면 `docker.ps1 reset-admin` (또는 `docker.sh reset-admin`).
- **Origin 검사**(DNS 리바인딩 방지): 브라우저 요청은 localhost 또는 `MCP_ALLOWED_ORIGINS` 의 Origin 만 허용합니다.

## 대시보드

| 화면 | 내용 |
|---|---|
| 모니터링 | MCP 서버·대시보드 상태와 응답 시간 이력, 게이트웨이 가동 시간·메모리, 요청 수/오류율/지연 시간(p50·p95·p99)/응답 데이터, 시간대별 요청·오류 및 p95 그래프(1시간·24시간·7일), 도구별·키별 호출 수, 지연 시간 분포. 15초마다 갱신 |
| 로그 | MCP 요청(키, JSON-RPC 메서드, 도구, 상태, 지연, 크기, 도구 오류 여부, IP, User-Agent), 관리 작업(로그인, 키 발급 등), 시스템(업스트림 장애/복구). 종류·수준·키·도구·검색어 필터, SSE 실시간 수신, 상세 펼치기, 전체 삭제 |
| API 키 | 발급(이름·만료·허용 도구), 수정, 활성/비활성 전환, 재발급(이전 키 즉시 무효), 삭제, 사용량·마지막 사용 시각 |
| 설정 | 클라이언트 연결 예시(Claude Code CLI, `.mcp.json`), 게이트웨이 정보, 관리자 비밀번호 변경 |

로그와 지표는 `LOG_RETENTION_DAYS`(기본 7일) 동안 보존됩니다. 라이트/다크 테마는 시스템 설정을 따르고 상단 버튼으로 바꿀 수 있습니다.

## Docker self-hosting (권장)

Native AOT 로 빌드한 MCP 서버(chiseled 이미지), Deno distroless 이미지의 gateway·dashboard, Caddy 를 모두
non-root·읽기 전용 파일시스템·권한 제거 상태로 실행합니다. 호스트에 포트를 게시하는 것은 **Caddy** 뿐이며,
같은 주소에서 MCP(`/mcp`)와 대시보드(`/`)를 함께 제공합니다. 관련 파일은 [docker/](docker) 폴더에 있고 설정 파일 `.env` 도 이 폴더(`docker/.env`)에 생성됩니다.

```powershell
.\docker\docker.ps1 up        # Windows: .env 생성(최초 1회) → 빌드 → 실행 → healthy 대기 → 연결 정보·설정 코드 출력
```

```bash
docker/docker.sh up           # Linux / macOS / Git Bash
```

1. `up` 이 끝나면 `https://127.0.0.1:5100/` 대시보드에 접속해, 출력된 **설정 코드**로 관리자 비밀번호를 만듭니다.
2. **API 키** 화면에서 키를 발급하고 클라이언트에 등록합니다 (아래 [Claude 에 연결](#claude-에-연결)).

docker compose 를 직접 쓰는 경우: `docker compose -f docker/docker-compose.yml up -d --build` (설정 코드는 `docker logs gemini-mcp-gateway`).

| 명령 | 동작 |
|---|---|
| `setup` | `.env` 생성 (`-ShareDir`/`--share-dir`, `-Bind`/`--bind`, `-Port`/`--port`, `-Domain`/`--domain`, `-Force`/`--force`) |
| `up` | 빌드 + 시작, 모든 healthcheck 통과까지 대기 |
| `down` / `restart` / `logs` / `status` | 중지·삭제 / 재시작 / 로그 / 상태와 연결 정보 |
| `rebuild` | 캐시 없이 베이스 이미지부터 다시 빌드 |
| `cert` | Caddy 로컬 CA 루트 인증서를 `docker/caddy-root.crt` 로 내보내기 (`MCP_TLS=internal`) |
| `reset-admin` | 대시보드 관리자 계정·세션 삭제 → 새 설정 코드 출력 (API 키·로그는 유지) |

### TLS · 압축 (Caddy)

- **주소**: `https://127.0.0.1:5100/` (대시보드), `https://127.0.0.1:5100/mcp` (MCP). `MCP_BIND`/`MCP_PORT` 로 변경. 평문 HTTP 는 받지 않습니다.
- **인증서** (`MCP_TLS`)
  - `internal`(기본): Caddy 가 자체 로컬 CA 로 `MCP_DOMAIN`(기본 `localhost, 127.0.0.1`)의 인증서를 발급·갱신합니다.
    클라이언트가 루트 인증서를 신뢰해야 하므로 `up` 후 한 번 `cert` 를 실행하고, Claude Code(Node.js)에는 `NODE_EXTRA_CA_CERTS` 로 지정하세요.
    브라우저로 대시보드를 열 때도 이 루트 인증서를 신뢰시켜야 경고가 사라집니다 (Windows: `certutil -user -addstore Root docker\caddy-root.crt`).
    CA 는 `caddy-data` 볼륨에 보존되므로 `down -v` 로 볼륨을 지우지 않는 한 다시 할 필요가 없습니다.

    ```powershell
    .\docker\docker.ps1 cert
    [Environment]::SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", "$PWD\docker\caddy-root.crt", "User")  # 이후 Claude Code 재시작
    ```
  - 이메일 주소: 공개 DNS 도메인에 Let's Encrypt/ZeroSSL 공인 인증서를 자동 발급합니다 (TLS-ALPN 검증이므로 `MCP_BIND=0.0.0.0`, `MCP_PORT=443`, `MCP_DOMAIN=<도메인>` 필요).
- **다른 PC 에서 접속**: `setup -Bind 0.0.0.0` 후 접속에 쓰는 호스트명/IP 를 `MCP_DOMAIN` 에 추가하세요. IP 로 접속하면 SNI 가 없으므로 그 IP 를 `MCP_DEFAULT_SNI` 로 지정합니다.
  외부에 공개해도 MCP 는 API 키, 대시보드는 관리자 로그인이 있어야 쓸 수 있습니다.
- **압축**: `Accept-Encoding` 에 따라 zstd → gzip 순으로 적용됩니다. MCP 도구 응답(SSE)도 압축됩니다.
  gateway·MCP 서버는 압축하지 않고(업스트림 요청은 `accept-encoding: identity`) Caddy 한 곳에서만 압축합니다.
- **HTTP/1.1 · HTTP/2 · HTTP/3**: Caddy 가 컨테이너 안에서도 `MCP_PORT` 로 수신하고 TCP·UDP 를 같은 번호로 게시하므로,
  `Alt-Svc: h3=":<MCP_PORT>"` 가 실제 호스트 포트를 광고합니다. 방화벽에서 외부 공개 시 UDP 도 열어야 HTTP/3 가 쓰입니다.
- 설정 파일: [docker/Caddyfile](docker/Caddyfile).

### 기타 설정

- **Gemini API 키**: `.env` 의 `GEMINI_API_KEY` 를 비워 두면 셸(Windows 는 사용자 환경 변수 포함)의 값을 씁니다. 키를 파일에 평문으로 저장하지 않으려면 비워 두세요.
- **파일 공유**: 컨테이너는 호스트 파일을 볼 수 없으므로 `HOST_SHARE_DIR`(기본: 사용자 홈)을 `/share` 로 마운트하고 경로를 자동 변환합니다.
  Claude 가 `C:\Users\me\video.mp4` 를 넘기면 서버는 `/share/video.mp4` 를 읽고, 생성 파일은
  `C:\Users\me\gemini-mcp-output\...` 처럼 **호스트 경로로** 돌려줍니다. 공유 폴더 밖의 파일은 읽을 수 없습니다.
- **gateway 데이터**: API 키 해시, 관리자 계정, 세션, 로그, 지표는 `gateway-data` 볼륨의 Deno KV(SQLite)에 저장됩니다.
- 설정 항목 전체는 [docker/.env.example](docker/.env.example) 을 참고하세요. 설정 우선순위는 셸 환경 변수 > `.env` 입니다 (docker compose 와 동일).

## 로컬 개발 (Docker 없이)

필요: .NET 10 SDK(+ AOT 게시 시 Visual Studio C++ 빌드 도구), Deno 2.8+.

```powershell
.\scripts\publish-aot.ps1                       # publish\win-x64\GeminiMcp.exe (단일 네이티브 실행 파일)
$env:GEMINI_API_KEY = "<your key>"
.\publish\win-x64\GeminiMcp.exe                 # MCP 서버  http://127.0.0.1:5100 (인증 없음, 루프백 전용)
```

```bash
cd dashboard && deno install && deno task build && deno task start    # 대시보드 http://127.0.0.1:3000
cd gateway && deno task start                                          # gateway   http://127.0.0.1:8080 (설정 코드가 출력됨)
```

이제 `http://127.0.0.1:8080/` 이 대시보드, `http://127.0.0.1:8080/mcp` 가 키 인증 MCP 엔드포인트입니다.
대시보드를 고치면서 보려면 `deno task dev`(Nuxt 개발 서버 :3000, `/api/admin` 은 :8080 gateway 로 프록시)를 쓰세요.

### MCP 서버 환경 변수

| 환경 변수 | 기본값 | 설명 |
|---|---|---|
| `GEMINI_API_KEY` | (필수) | `GOOGLE_API_KEY` 도 인식 |
| `GEMINI_MODEL` | `gemini-flash-latest` | 기본 텍스트 모델 |
| `GEMINI_PRO_MODEL` | `gemini-pro-latest` | `model: "pro"` 별칭 |
| `GEMINI_IMAGE_MODEL` | `gemini-3.1-flash-image` | 이미지 생성 모델 |
| `GEMINI_TTS_MODEL` | `gemini-2.5-flash-preview-tts` | 음성 합성 모델 |
| `GEMINI_MCP_OUTPUT_DIR` | `~/gemini-mcp-output` | 생성 파일 저장 폴더 |
| `GEMINI_INLINE_LIMIT_BYTES` | `15728640` | 이보다 큰 파일은 Files API 로 업로드 |
| `MCP_ALLOWED_ORIGINS` | (localhost 만) | 허용할 브라우저 Origin, 쉼표 구분 |
| `ASPNETCORE_URLS` / `--urls` | `http://127.0.0.1:5100` | 수신 주소 |
| `GEMINI_MCP_PATH_MAPS` | (없음) | `호스트경로=서버경로` 매핑 (`;` 구분). Docker 에서 사용 |

MCP 서버 자체는 인증하지 않습니다. 루프백 또는 내부 네트워크에만 노출하고, 외부 접속은 gateway 를 거치게 하세요.

### gateway 환경 변수

| 환경 변수 | 기본값 | 설명 |
|---|---|---|
| `GATEWAY_HOST` / `GATEWAY_PORT` | `127.0.0.1` / `8080` | 수신 주소 (Docker 이미지: `0.0.0.0`) |
| `MCP_UPSTREAM` | `http://127.0.0.1:5100` | MCP 서버 |
| `DASHBOARD_UPSTREAM` | `http://127.0.0.1:3000` | 대시보드 |
| `GATEWAY_DATA_DIR` | `./data` | Deno KV 저장 폴더 (Docker: `/data` 볼륨) |
| `MCP_ALLOWED_ORIGINS` | (localhost 만) | 허용할 브라우저 Origin |
| `LOG_RETENTION_DAYS` | `7` | 로그·지표 보존 기간 |
| `MAX_BODY_BYTES` | `10485760` | MCP 요청 본문 최대 크기 |
| `HEALTH_INTERVAL_SECONDS` | `15` | 업스트림 상태 확인 주기 |

## Claude 에 연결

대시보드 **API 키** 화면에서 키를 발급한 뒤 등록합니다 (발급 창에 아래 명령이 키와 함께 표시됩니다).

**Claude Code**

```bash
claude mcp add --transport http gemini https://127.0.0.1:5100/mcp --header "Authorization: Bearer <API 키>"
```

**프로젝트 `.mcp.json`** — 키를 파일에 쓰지 않도록 Claude Code 의 환경 변수 확장을 씁니다 (`GEMINI_MCP_API_KEY` 에 키를 넣어 두세요).

```json
{
  "mcpServers": {
    "gemini": {
      "type": "http",
      "url": "https://127.0.0.1:5100/mcp",
      "headers": { "Authorization": "Bearer ${GEMINI_MCP_API_KEY}" }
    }
  }
}
```

## 검증

**gateway** (Deno 테스트, 가짜 MCP 서버·메모리 KV): 관리자 설정 코드·로그인·잠금, CSRF 차단, 키 발급/비활성/재발급/삭제/만료,
도구 권한, Origin 검사, 로그·지표 집계, 업스트림 상태, 라우팅.

```bash
cd gateway && deno task test
```

**대시보드**: `cd dashboard && deno task typecheck` (vue-tsc).

**MCP 서버**: xUnit v3 (Microsoft.Testing.Platform) 테스트가 **AOT 로 게시된 `publish\win-x64\GeminiMcp.exe` 를 직접 띄워**
공식 MCP C# 클라이언트로 검증합니다. 먼저 `scripts\publish-aot.ps1` 로 게시하세요.

```powershell
dotnet test --project tests/GeminiMcp.Tests                                  # 전체 (실제 Gemini API 호출 포함)
dotnet test --project tests/GeminiMcp.Tests --filter-not-trait "Category=Live" # API 호출 없이 프로토콜/보안만
```

| 테스트 클래스 | 내용 |
|---|---|
| `HttpEndpointTests` | `/health`, `/skill`, 외부 Origin 403, localhost Origin 허용, (gateway 경유 시) 키 없음·잘못된 키 401 |
| `McpProtocolTests` | initialize(서버 정보·instructions·capabilities), 구버전 프로토콜(2025-03-26/06-18/11-25) 협상, 도구 목록·스키마, 스킬 프롬프트·리소스, 도구 오류(isError) |
| `GeminiToolTests` (Live) | 9개 도구 실호출: 검색 출처, JSON Schema, Python 실행, 이미지 생성→재인식, TTS→받아쓰기, YouTube |
| `FilesApiUploadTests` (Live) | 인라인 한도를 넘는 파일이 Files API 업로드 경로로 처리되는지 |

- `GEMINI_API_KEY` 가 없으면 Live 테스트는 건너뜁니다 (Windows 에서는 사용자 환경 변수도 읽음).
- 다른 실행 파일을 검증하려면 `GEMINI_MCP_EXE` 환경 변수로 경로를 지정합니다.
- **실행 중인 배포(gateway 경유) 검증**: `GEMINI_MCP_URL` 을 지정하면 프로세스를 띄우지 않고 그 서버를 테스트합니다.
  이미지·음성 테스트가 호스트 경로로 파일을 주고받으므로 경로 매핑까지 검증됩니다.
  (`FilesApiUploadTests` 는 서버 설정을 바꿔야 하므로 항상 로컬 AOT 바이너리로 실행합니다.)

  ```powershell
  $env:GEMINI_MCP_URL = "https://127.0.0.1:5100"   # Caddy 루트 인증서를 Windows 사용자 저장소에 신뢰시켜야 합니다 (cert 명령 안내 참고)
  $env:GEMINI_MCP_API_KEY = "<대시보드에서 발급한 키>"
  $env:GEMINI_MCP_TEST_OUTPUT_DIR = "$HOME\gemini-mcp-output"   # HOST_SHARE_DIR\OUTPUT_SUBDIR
  dotnet test --project tests/GeminiMcp.Tests
  ```
- `global.json` 이 .NET 10 `dotnet test` 를 Microsoft.Testing.Platform 모드로 설정합니다.

## 구조

```
src/GeminiMcp/                MCP 서버 (.NET 10 Native AOT)
  Program.cs                  호스트, MCP 등록, Origin 검사, /skill·/health
  GeminiOptions.cs            설정(환경 변수)
  PathMapper.cs               호스트↔컨테이너 경로 변환
  Gemini/                     Gemini REST(v1beta) 클라이언트, 미디어 변환, 응답 파싱, 출력 파일
  Tools/GeminiTools.cs        MCP 도구 9종
  Skill/                      내장 스킬 (SKILL.md + 리소스/프롬프트/설치)
gateway/                      gateway (Deno + TypeScript)
  main.ts                     진입점 (--reset-admin)
  src/app.ts                  라우팅 (/mcp, /skill, /health, /api/admin, 대시보드)
  src/mcp.ts                  MCP 프록시: Origin·API 키·도구 권한 검사, 응답 스트림 관찰, 로그·지표 기록
  src/api.ts                  관리 API (인증, 키, 로그·SSE, 지표, 상태, 도구 목록)
  src/auth.ts                 관리자 설정 코드·비밀번호(PBKDF2)·세션
  src/keys.ts | logs.ts | metrics.ts | upstream.ts   키 저장소, 로그, 분 단위 지표, 상태 확인
  gateway_test.ts             테스트
  Dockerfile                  Deno distroless 이미지
dashboard/                    대시보드 (Nuxt 4 + Vuetify 4 + TypeScript, Deno)
  app/pages/                  index(모니터링), logs, keys, settings, login
  app/components/             TimeSeriesChart(SVG), StatTile
  app/plugins/vuetify.ts      Material Design 테마(라이트/다크)
  Dockerfile                  Nitro deno-server 빌드 → Deno distroless 이미지
scripts/publish-aot.ps1       Native AOT 게시
docker/
  docker.ps1 | docker.sh      Docker self-hosting (setup/up/down/restart/rebuild/logs/status/cert/reset-admin)
  Dockerfile                  MCP 서버: AOT 빌드 → chiseled 런타임 이미지 (빌드 컨텍스트: 저장소 루트)
  Dockerfile.dockerignore     빌드 컨텍스트 제외 목록
  docker-compose.yml          MCP 서버 + gateway + 대시보드 + Caddy
  Caddyfile                   TLS 종단 + zstd/gzip 압축 리버스 프록시 → gateway
  .env.example                Docker 설정 예시
tests/GeminiMcp.Tests/        xUnit v3 E2E 테스트 (AOT 바이너리 또는 gateway 경유 배포 대상)
```

MCP 프로토콜은 공식 [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.3.0 (stateless Streamable HTTP)을 사용하며,
Gemini 요청/응답은 AOT 안전한 `JsonNode` 로 처리합니다. 트리밍/AOT 경고는 오류로 승격되어 있어 경고 0개로 빌드됩니다.
