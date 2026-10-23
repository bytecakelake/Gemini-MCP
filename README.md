# Gemini MCP Server (C# / .NET 10 / Native AOT)

Claude 가 직접 할 수 없거나 약한 작업을 Google Gemini API 에 위임하는 **HTTP(Streamable HTTP) MCP 서버**입니다.
사용법을 알려주는 **스킬(SKILL.md)이 바이너리에 내장**되어 있습니다.

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

## 빌드 (Native AOT)

필요: .NET 10 SDK, Visual Studio C++ 빌드 도구(Desktop development with C++).

```powershell
.\scripts\publish-aot.ps1
```

결과: `publish\win-x64\GeminiMcp.exe` (단일 네이티브 실행 파일, 약 19MB, 런타임 설치 불필요).

## 실행

```powershell
$env:GEMINI_API_KEY = "<your key>"
.\publish\win-x64\GeminiMcp.exe            # http://127.0.0.1:5100/mcp
```

| 환경 변수 | 기본값 | 설명 |
|---|---|---|
| `GEMINI_API_KEY` | (필수) | `GOOGLE_API_KEY` 도 인식 |
| `GEMINI_MODEL` | `gemini-flash-latest` | 기본 텍스트 모델 |
| `GEMINI_PRO_MODEL` | `gemini-pro-latest` | `model: "pro"` 별칭 |
| `GEMINI_IMAGE_MODEL` | `gemini-3.1-flash-image` | 이미지 생성 모델 |
| `GEMINI_TTS_MODEL` | `gemini-2.5-flash-preview-tts` | 음성 합성 모델 |
| `GEMINI_MCP_OUTPUT_DIR` | `~/gemini-mcp-output` | 생성 파일 저장 폴더 |
| `GEMINI_INLINE_LIMIT_BYTES` | `15728640` | 이보다 큰 파일은 Files API 로 업로드 |
| `MCP_AUTH_TOKEN` | (없음) | 설정 시 `Authorization: Bearer <token>` 필요 |
| `MCP_ALLOWED_ORIGINS` | (localhost 만) | 허용할 브라우저 Origin, 쉼표 구분 |
| `ASPNETCORE_URLS` / `--urls` | `http://127.0.0.1:5100` | 수신 주소 |
| `GEMINI_MCP_PATH_MAPS` | (없음) | `호스트경로=서버경로` 매핑 (`;` 구분). Docker 에서 사용 |

보안: 기본적으로 루프백(127.0.0.1)에만 바인딩하며, 외부 Origin 요청은 403 으로 거부합니다(DNS 리바인딩 방지).
다른 PC 에서 접근하게 하려면 `--urls http://0.0.0.0:5100` 과 함께 반드시 `MCP_AUTH_TOKEN` 을 설정하세요.

## Docker self-hosting

Native AOT 로 빌드한 바이너리를 셸이 없는 chiseled 이미지(약 52MB)에 담아, non-root·읽기 전용 파일시스템·권한 제거 상태로 실행합니다.
관련 파일은 모두 [docker/](docker) 폴더에 있으며, 설정 파일 `.env` 도 이 폴더(`docker/.env`)에 생성됩니다.

```powershell
.\docker\docker.ps1 up        # Windows: .env 생성(최초 1회) → 빌드 → 실행 → healthy 대기 → 연결 정보 출력
```

```bash
docker/docker.sh up           # Linux / macOS / Git Bash
```

docker compose 를 직접 쓰는 경우: `docker compose -f docker/docker-compose.yml up -d --build`

| 명령 | 동작 |
|---|---|
| `setup` | `.env` 생성 (`-ShareDir`/`--share-dir`, `-Bind`/`--bind`, `-Port`/`--port`, `-WithToken`/`--with-token`, `-Force`/`--force`) |
| `up` | 빌드 + 시작, healthcheck 통과까지 대기 |
| `down` / `restart` / `logs` / `status` | 중지·삭제 / 재시작 / 로그 / 상태와 연결 정보 |
| `rebuild` | 캐시 없이 베이스 이미지부터 다시 빌드 |

- **API 키**: `.env` 의 `GEMINI_API_KEY` 를 비워 두면 셸(Windows 는 사용자 환경 변수 포함)의 값을 씁니다. 키를 파일에 평문으로 저장하지 않으려면 비워 두세요.
- **파일 공유**: 컨테이너는 호스트 파일을 볼 수 없으므로 `HOST_SHARE_DIR`(기본: 사용자 홈)을 `/share` 로 마운트하고 경로를 자동 변환합니다.
  Claude 가 `C:\Users\me\video.mp4` 를 넘기면 서버는 `/share/video.mp4` 를 읽고, 생성 파일은
  `C:\Users\me\gemini-mcp-output\...` 처럼 **호스트 경로로** 돌려줍니다. 공유 폴더 밖의 파일은 읽을 수 없습니다.
- **노출 범위**: 기본은 `127.0.0.1:5100` 에만 게시합니다. 다른 PC 에서 쓰려면 `setup -Bind 0.0.0.0` 을 실행하세요.
  이 경우 토큰이 자동 생성되고, 토큰 없이 외부에 노출하려 하면 스크립트가 실행을 거부합니다.
- 설정 항목 전체는 [docker/.env.example](docker/.env.example) 을 참고하세요. 설정 우선순위는 셸 환경 변수 > `.env` 입니다 (docker compose 와 동일).

## Claude 에 연결

**Claude Code**

```bash
claude mcp add --transport http gemini http://127.0.0.1:5100/mcp
```

토큰을 쓰는 경우:

```bash
claude mcp add --transport http gemini http://127.0.0.1:5100/mcp --header "Authorization: Bearer <token>"
```

**프로젝트 `.mcp.json`**

```json
{
  "mcpServers": {
    "gemini": { "type": "http", "url": "http://127.0.0.1:5100/mcp" }
  }
}
```

## 검증

xUnit v3 (Microsoft.Testing.Platform) 테스트가 **AOT 로 게시된 `publish\win-x64\GeminiMcp.exe` 를 직접 띄워**
공식 MCP C# 클라이언트로 검증합니다. 먼저 `scripts\publish-aot.ps1` 로 게시하세요.

```powershell
dotnet test --project tests/GeminiMcp.Tests                                  # 전체 (실제 Gemini API 호출 포함)
dotnet test --project tests/GeminiMcp.Tests --filter-not-trait "Category=Live" # API 호출 없이 프로토콜/보안만
```

| 테스트 클래스 | 내용 |
|---|---|
| `HttpEndpointTests` | `/health`, `/skill`, 토큰 401, 외부 Origin 403, localhost Origin 허용 |
| `McpProtocolTests` | initialize(서버 정보·instructions·capabilities), 구버전 프로토콜(2025-03-26/06-18/11-25) 협상, 도구 목록·스키마, 스킬 프롬프트·리소스, 도구 오류(isError) |
| `GeminiToolTests` (Live) | 9개 도구 실호출: 검색 출처, JSON Schema, Python 실행, 이미지 생성→재인식, TTS→받아쓰기, YouTube |
| `FilesApiUploadTests` (Live) | 인라인 한도를 넘는 파일이 Files API 업로드 경로로 처리되는지 |

- `GEMINI_API_KEY` 가 없으면 Live 테스트는 건너뜁니다 (Windows 에서는 사용자 환경 변수도 읽음).
- 다른 실행 파일을 검증하려면 `GEMINI_MCP_EXE` 환경 변수로 경로를 지정합니다.
- **실행 중인 서버(예: Docker 컨테이너) 검증**: `GEMINI_MCP_URL` 을 지정하면 프로세스를 띄우지 않고 그 서버를 테스트합니다.
  이미지·음성 테스트가 호스트 경로로 파일을 주고받으므로 경로 매핑까지 검증됩니다.
  (`FilesApiUploadTests` 는 서버 설정을 바꿔야 하므로 항상 로컬 AOT 바이너리로 실행합니다.)

  ```powershell
  $env:GEMINI_MCP_URL = "http://127.0.0.1:5100"
  $env:GEMINI_MCP_TOKEN = "<MCP_AUTH_TOKEN, 없으면 생략>"
  $env:GEMINI_MCP_TEST_OUTPUT_DIR = "$HOME\gemini-mcp-output"   # HOST_SHARE_DIR\OUTPUT_SUBDIR
  dotnet test --project tests/GeminiMcp.Tests
  ```
- `global.json` 이 .NET 10 `dotnet test` 를 Microsoft.Testing.Platform 모드로 설정합니다.

## 구조

```
src/GeminiMcp/
  Program.cs              호스트, MCP 등록, Origin/토큰 검사, /skill·/health
  GeminiOptions.cs        설정(환경 변수)
  PathMapper.cs           호스트↔컨테이너 경로 변환
  Gemini/GeminiClient.cs  Gemini REST(v1beta) + Files API 업로드
  Gemini/MediaResolver.cs 로컬 파일/URL/YouTube → Gemini part
  Gemini/GeminiResponse.cs 응답 파싱(텍스트, 코드 실행, 출처, 바이너리)
  Gemini/OutputFiles.cs   출력 경로, PCM→WAV
  Tools/GeminiTools.cs    MCP 도구 9종
  Skill/SKILL.md          내장 스킬 (EmbeddedResource)
  Skill/SkillContent.cs   스킬 리소스/프롬프트/설치
scripts/publish-aot.ps1   Native AOT 게시
docker/
  docker.ps1 | docker.sh  Docker self-hosting (setup/up/down/restart/rebuild/logs/status)
  Dockerfile              AOT 빌드 → chiseled 런타임 이미지 (빌드 컨텍스트: 저장소 루트)
  Dockerfile.dockerignore 빌드 컨텍스트 제외 목록
  docker-compose.yml      포트·볼륨·보안 옵션 (docker/.env 사용)
  .env.example            Docker 설정 예시
tests/GeminiMcp.Tests/    xUnit v3 E2E 테스트 (AOT 바이너리 대상)
  Infrastructure/         서버 프로세스 픽스처, MCP 클라이언트 헬퍼
```

MCP 프로토콜은 공식 [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.3.0 (stateless Streamable HTTP)을 사용하며,
Gemini 요청/응답은 AOT 안전한 `JsonNode` 로 처리합니다. 트리밍/AOT 경고는 오류로 승격되어 있어 경고 0개로 빌드됩니다.
