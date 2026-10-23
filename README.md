# Gemini MCP Server (C# / .NET 10 / Native AOT) + Apache APISIX + Dashboard

Claude 가 직접 할 수 없거나 약한 작업을 Google Gemini API 에 위임하는 **HTTP(Streamable HTTP) MCP 서버**와,
그 앞에서 **API 키 인증·라우팅**을 맡는 오픈소스 게이트웨이 **Apache APISIX**, 요청 로그를 모으는 **Grafana Loki**,
이를 관리하는 **Material Design 대시보드**로 구성됩니다. 사용법을 알려주는 **스킬(SKILL.md)이 바이너리에 내장**되어 있습니다.

```
클라이언트 ─HTTPS─▶ Caddy ─▶ APISIX ─┬─ /mcp            ─▶ gemini-mcp  (key-auth: 대시보드에서 발급한 API 키)
                                     ├─ /skill, /health ─▶ gemini-mcp  (공개)
                                     └─ 그 외            ─▶ dashboard   (관리자 로그인)
APISIX ─(loki-logger)─▶ Loki ◀─ dashboard (로그 조회·지표 계산, 관리 로그 기록)
dashboard ─(Admin API)─▶ APISIX ─▶ etcd (라우트·API 키)
```

| 서비스 | 기술 | 역할 |
|---|---|---|
| `gemini-mcp` | C# / .NET 10 Native AOT | MCP 도구 9종. 인증하지 않으며 내부 네트워크에만 노출 |
| `apisix` | [Apache APISIX](https://apisix.apache.org/) 3.19 (Apache-2.0) | API 게이트웨이: `key-auth` 로 API 키 검증, `consumer-restriction` 으로 비활성/만료 키 차단, `loki-logger` 로 요청 로그 전송, 라우팅 |
| `etcd` | etcd 3.6 | APISIX 설정 저장소 (라우트, 컨슈머 = API 키) |
| `loki` | [Grafana Loki](https://grafana.com/oss/loki/) 3.7 (AGPL-3.0) | 요청·관리·시스템 로그 저장. 대시보드 지표도 LogQL 로 여기서 계산 |
| `dashboard` | Nuxt 4 + Vuetify 4(Material Design) + TypeScript, Nitro `deno-server` | 모니터링, 로그, API 키 관리, 설정. 서버 쪽에서 관리자 인증과 APISIX·Loki 연동 |
| `caddy` | Caddy 2 | TLS 종단, zstd/gzip 압축, HTTP/3 |
| `s3` | [SeaweedFS](https://github.com/seaweedfs/seaweedfs) 4.48 (Apache-2.0) | 생성 미디어용 내장 S3 저장소 (기본 `COMPOSE_PROFILES=s3`, 외부 S3 를 쓰면 끔) |

## 제공 도구

| 도구 | 용도 |
|---|---|
| `gemini_web_search` | Google 검색 그라운딩 — 최신 정보 + 출처 |
| `gemini_read_urls` | URL Context — 웹 페이지(최대 20개) 읽기·요약·비교 |
| `gemini_analyze_media` | 영상·오디오·이미지·PDF·YouTube 분석, 전사(받아쓰기) |
| `gemini_generate_image` | 이미지 생성/편집 (Nano Banana) → ResourceLink |
| `gemini_text_to_speech` | 음성 합성 → WAV ResourceLink |
| `gemini_run_code` | Gemini Python 샌드박스 코드 실행 |
| `gemini_ask` | 세컨드 오피니언, 대용량 컨텍스트 분석, JSON Schema 구조화 출력 |
| `gemini_generate_video` | 영상 생성/편집/연장 (Gemini Omni) → MP4 ResourceLink |
| `gemini_list_models` | 사용 가능한 모델을 용도(text/pro/image/tts/video)별로 표시, 현재 선택 표시 |
| `gemini_select_model` | 용도별 기본 모델 선택 (서버 전역, 재시작 후에도 유지) |
| `gemini_usage_guide` | 내장 스킬(사용 설명서) 반환 |

### 모델 선택

모델은 환경 변수로 정하지 않고 에이전트가 직접 고릅니다. `gemini_list_models` 가 API 키로 쓸 수 있는 모델을 용도별로 보여주고(★ = 현재 선택),
`gemini_select_model { category, model }` 로 용도별 기본 모델을 바꿉니다(`model: "default"` 로 초기화). 선택은 서버 전역이며
출력 폴더의 `.gemini-mcp-models.json` 에 저장되어 재시작 후에도 유지됩니다. 한 번만 다른 모델을 쓰려면 각 도구의 `model` 인수를 넘기면 됩니다.

- **최신 모델 반영**: 목록은 `models.list` 에서 받아 1시간(3600초) 캐시합니다. `refresh: true` 로 즉시 새로 받을 수 있고,
  `gemini_select_model` 은 캐시에 없는 이름이면 한 번 새로 받아 다시 확인합니다.
- **분류**: 이름뿐 아니라 표시 이름·설명과 모델 계열로 판단합니다. 예를 들어 `nano-banana-*` 는 image 로,
  음악(`lyria`)·에이전트(`deep-research`, `antigravity`, `computer-use`)·로보틱스·전사·임베딩·Live 전용 모델은 `other` 로 분류됩니다(`include_other: true` 로 표시).
- **정렬**: `-latest` 별칭 → gemini 계열 → 버전 높은 순 → 정식 버전 순. `[latest]`, `[preview]` 표시가 붙습니다.
- **기본값 점검**: 선택(또는 기본) 모델이 목록에 없으면(종료 등) ⚠ 와 함께 그 용도의 최신 후보를 보여줍니다.
  `gemini_select_model { category, model: "newest" }` 로 최신 모델을 바로 고를 수 있습니다.

| category | 쓰는 도구 | 내장 기본값 |
|---|---|---|
| `text` | ask, web_search, read_urls, analyze_media, run_code | `gemini-flash-latest` |
| `pro` | 위 도구에서 `model: "pro"` | `gemini-pro-latest` |
| `image` | generate_image | `gemini-3.1-flash-image` |
| `tts` | text_to_speech | `gemini-3.8-flash-tts` |
| `video` | generate_video | `gemini-omni-1.1-flash` |

## 내장 스킬

같은 SKILL.md 를 여러 경로로 제공합니다.

- MCP `initialize` 의 `instructions` (요약본, 연결 즉시 Claude 에게 전달)
- MCP 프롬프트 `gemini_skill` (Claude Code 에서 `/mcp__gemini__gemini_skill`)
- MCP 리소스 `gemini://skill/SKILL.md`
- 도구 `gemini_usage_guide`
- HTTP `GET /skill`
- Claude Code 스킬로 설치: `GeminiMcp.exe --install-skill` → `~/.claude/skills/gemini-mcp/SKILL.md`

## 인증

인증 정보는 환경 변수로 받지 않습니다.

- **MCP 클라이언트 (APISIX)**: 대시보드에서 발급한 API 키를 `Authorization: Bearer gmcp_…` 헤더로 보냅니다.
  - API 키 하나는 APISIX 컨슈머 하나(`k_<id>`)이고, 키 원문은 그 컨슈머의 `key-auth` credential 로 etcd 에 암호화되어 저장됩니다.
    대시보드는 키 원문을 발급(또는 재발급) 직후 한 번만 보여 줍니다.
  - `serverless-pre-function` 이 `Bearer` 키를 `key-auth` 가 읽는 헤더로 옮기고, `proxy-rewrite` 가 `Authorization` 을 지워 키를 MCP 서버로 넘기지 않습니다.
  - 키가 없거나 틀리면 `401` (`key-auth`), 비활성·만료 키는 `401` (`consumer-restriction` 허용 목록에서 제외, 만료는 1분마다 반영).
- **대시보드 관리자**: 최초 실행 시 대시보드 서버 로그에 일회용 **설정 코드**(`Dashboard setup code: XXXX-XXXX`)가 출력됩니다.
  대시보드에 접속해 이 코드로 관리자 비밀번호(10자 이상, PBKDF2-SHA256, `dashboard-data` 볼륨)를 만들면 이후에는 비밀번호로 로그인합니다.
  세션은 `HttpOnly; SameSite=Strict` 쿠키(12시간, 대시보드 재시작 시 다시 로그인)이고, 상태를 바꾸는 요청은 JSON 본문 + 같은 Origin 만 받습니다.
  로그인·설정 코드 입력은 IP 당 5회 실패 시 5분간 잠깁니다. 비밀번호를 잊었다면 `docker.ps1 reset-admin` (또는 `docker.sh reset-admin`).
- **APISIX Admin API**: `control` 네트워크(대시보드·etcd·Loki 만 연결)의 고정 주소에서만 수신하므로 관리 키 없이 네트워크로만 접근을 제한합니다.
  MCP 서버나 외부(Caddy)에서는 닿지 않습니다.
- **Origin 검사**(DNS 리바인딩 방지): MCP 서버가 브라우저 요청의 Origin 을 localhost 또는 `MCP_ALLOWED_ORIGINS` 로 제한합니다.

## 대시보드

| 화면 | 내용 |
|---|---|
| 모니터링 | APISIX·MCP 서버·Loki 상태와 응답 시간 이력, 요청 수/오류율/지연 시간(p50·p95·p99)/응답 데이터, 시간대별 요청·오류 및 p95 그래프(1시간·24시간·7일), 도구별·키별 호출 수, 지연 시간 분포. 15초마다 갱신 |
| 로그 | MCP 요청(키, JSON-RPC 메서드, 도구, 상태, 지연, 크기, IP, User-Agent), 관리 작업(로그인, 키 발급 등), 시스템(서비스 장애/복구). 종류·수준·키·도구·검색어 필터, 3초 간격 실시간 갱신, 상세 펼치기 |
| API 키 | 발급(이름·만료), 수정, 활성/비활성 전환, 재발급(이전 키 즉시 무효), 삭제, 보존 기간 내 요청 수·마지막 사용 시각 |
| 생성 미디어 | S3 에 보관된 이미지·음성·영상을 최신순으로 표시(미리보기·재생, 도구·모델·프롬프트·요청한 API 키·크기·생성 시각), 검색·도구 필터, 다운로드, 개별·선택 삭제(관리 로그에 기록). 전체 개수·용량 |
| 설정 | 클라이언트 연결 예시(Claude Code CLI, `.mcp.json`), 구성 정보, 관리자 비밀번호 변경 |

요청 지표는 Loki 의 요청 로그를 LogQL 로 집계하므로(로그 한 건 = 요청 한 건) 소량 트래픽에서도 정확합니다.
로그와 지표는 `LOG_RETENTION_DAYS`(기본 7일) 동안 보존됩니다. 라이트/다크 테마는 시스템 설정을 따르고 상단 버튼으로 바꿀 수 있습니다.

## Docker self-hosting

Native AOT 로 빌드한 MCP 서버(chiseled 이미지), Deno distroless 이미지의 대시보드, APISIX·etcd·Loki·Caddy 를
non-root·권한 제거 상태로 실행합니다(APISIX 를 뺀 나머지는 읽기 전용 파일시스템). 호스트에 포트를 게시하는 것은 **Caddy** 뿐이며,
같은 주소에서 MCP(`/mcp`)와 대시보드(`/`)를 함께 제공합니다. 관련 파일은 [docker/](docker) 폴더에 있고 설정 파일 `.env` 도 이 폴더(`docker/.env`)에 생성됩니다.

```powershell
.\docker\docker.ps1 up        # Windows: .env 생성(최초 1회) → 빌드 → 실행 → healthy 대기 → 연결 정보·설정 코드 출력
```

```bash
docker/docker.sh up           # Linux / macOS / Git Bash
```

1. `up` 이 끝나면 `https://127.0.0.1:5100/` 대시보드에 접속해, 출력된 **설정 코드**로 관리자 비밀번호를 만듭니다.
2. **API 키** 화면에서 키를 발급하고 클라이언트에 등록합니다 (아래 [Claude 에 연결](#claude-에-연결)).

대시보드 서버는 시작할 때 APISIX 에 라우트(`/mcp`, `/skill`·`/health`, 대시보드)를 등록합니다.
docker compose 를 직접 쓰는 경우: `docker compose -f docker/docker-compose.yml up -d --build` (설정 코드는 `docker logs gemini-mcp-dashboard`).

| 명령 | 동작 |
|---|---|
| `setup` | `.env` 생성 (`-ShareDir`/`--share-dir`, `-Bind`/`--bind`, `-Port`/`--port`, `-Domain`/`--domain`, `-TlsCert`·`-TlsKey`/`--tls-cert`·`--tls-key`, `-Force`/`--force`) |
| `up` | 빌드 + 시작, healthcheck 통과까지 대기 |
| `down` / `restart` / `logs` / `status` | 중지·삭제 / 재시작 / 로그 / 상태와 연결 정보 |
| `rebuild` | 캐시 없이 베이스 이미지부터 다시 빌드 |
| `cert` | Caddy 로컬 CA 루트 인증서를 `docker/caddy-root.crt` 로 내보내기 (`MCP_TLS=internal`) |
| `reset-admin` | 대시보드 관리자 계정 삭제 → 새 설정 코드 출력 (API 키·로그는 유지) |

### 네트워크

| 네트워크 | 연결 | 용도 |
|---|---|---|
| `edge` | caddy, apisix | 외부 요청 |
| `backend` | apisix, gemini-mcp, dashboard | MCP·대시보드로 전달, 대시보드의 MCP 상태 확인·도구 목록 |
| `control` (`10.89.250.0/24`) | apisix(`10.89.250.10`), dashboard, etcd, loki | APISIX Admin API, 설정 저장소, 로그 |

`control` 서브넷이 다른 Docker 네트워크와 겹치면 [docker-compose.yml](docker/docker-compose.yml) 과 [apisix/config.yaml](docker/apisix/config.yaml) 의 주소를 함께 바꾸세요.

### TLS · 압축 (Caddy)

- **주소**: `https://127.0.0.1:5100/` (대시보드), `https://127.0.0.1:5100/mcp` (MCP). `MCP_BIND`/`MCP_PORT` 로 변경.
  같은 포트로 평문 `http://` 요청이 오면 Caddy 의 `http_redirect` 리스너가 `https://` 로 308 리다이렉트합니다(경로·쿼리·메서드·본문 유지).
  이후 TLS ALPN 으로 HTTP/2 가 협상되고, `Alt-Svc` 로 광고한 HTTP/3 로 브라우저가 자동 전환합니다. 평문으로 내용을 주고받지는 않습니다.
- **인증서** (`MCP_TLS`)
  - 아래의 `MCP_TLS_CERT`/`MCP_TLS_KEY` 를 지정하면 이 값은 무시됩니다.
  - `internal`(기본): Caddy 가 자체 로컬 CA 로 `MCP_DOMAIN`(기본 `localhost, 127.0.0.1`)의 인증서를 발급·갱신합니다.
    클라이언트가 루트 인증서를 신뢰해야 하므로 `up` 후 한 번 `cert` 를 실행하고, Claude Code(Node.js)에는 `NODE_EXTRA_CA_CERTS` 로 지정하세요.
    브라우저로 대시보드를 열 때도 이 루트 인증서를 신뢰시켜야 경고가 사라집니다 (Windows: `certutil -user -addstore Root docker\caddy-root.crt`).
    CA 는 `caddy-data` 볼륨에 보존되므로 `down -v` 로 볼륨을 지우지 않는 한 다시 할 필요가 없습니다.

    ```powershell
    .\docker\docker.ps1 cert
    [Environment]::SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", "$PWD\docker\caddy-root.crt", "User")  # 이후 Claude Code 재시작
    ```
  - 이메일 주소: 공개 DNS 도메인에 Let's Encrypt/ZeroSSL 공인 인증서를 자동 발급합니다 (TLS-ALPN 검증이므로 `MCP_BIND=0.0.0.0`, `MCP_PORT=443`, `MCP_DOMAIN=<도메인>` 필요).
- **관리자가 준비한 인증서** (`MCP_TLS_CERT`, `MCP_TLS_KEY`): PEM 인증서(중간 인증서 체인 포함)와 개인 키의 호스트 경로를 둘 다 지정하면
  `MCP_TLS` 대신 그 파일을 씁니다. 각 파일은 caddy 컨테이너의 `/certs/cert.pem`·`/certs/key.pem` 에 읽기 전용으로 마운트되며,
  파일 단위 마운트라 certbot 의 `live/` 심볼릭 링크도 그대로 쓸 수 있습니다. 상대 경로는 `docker/` 폴더 기준입니다.
  - 컨테이너는 추가 권한 없는 root 로 실행되므로, 키 파일이 root 가 아닌 다른 사용자 소유의 `600` 이면 읽지 못합니다 (root 소유 또는 읽기 권한 필요).
  - `MCP_DOMAIN` 에는 인증서에 들어 있는 이름을 적으세요(클라이언트가 접속하는 이름). IP 로 접속하면 `MCP_DEFAULT_SNI` 도 맞춥니다.
  - Caddy 는 인증서 파일 변경을 감지하지 않습니다. 갱신한 뒤 `restart` 하세요 (예: certbot `--deploy-hook "docker restart gemini-mcp-caddy"`).
  - `up`/`restart` 는 시작 전에 두 파일이 있는지 확인합니다. 한쪽만 지정하면 caddy 가 시작하지 않습니다.

  ```bash
  docker/docker.sh setup --bind 0.0.0.0 --port 443 --domain mcp.example.com \
    --tls-cert /etc/letsencrypt/live/mcp.example.com/fullchain.pem \
    --tls-key /etc/letsencrypt/live/mcp.example.com/privkey.pem
  ```
- **다른 PC 에서 접속**: `setup -Bind 0.0.0.0` 후 접속에 쓰는 호스트명/IP 를 `MCP_DOMAIN` 에 추가하세요. IP 로 접속하면 SNI 가 없으므로 그 IP 를 `MCP_DEFAULT_SNI` 로 지정합니다.
  외부에 공개해도 MCP 는 API 키, 대시보드는 관리자 로그인이 있어야 쓸 수 있습니다.
- **압축**: `Accept-Encoding` 에 따라 zstd → gzip 순으로 적용됩니다. MCP 도구 응답(SSE)도 압축됩니다. 압축은 Caddy 한 곳에서만 합니다.
- **HTTP/1.1 · HTTP/2 · HTTP/3**: Caddy 가 컨테이너 안에서도 `MCP_PORT` 로 수신하고 TCP·UDP 를 같은 번호로 게시하므로,
  `Alt-Svc: h3=":<MCP_PORT>"` 가 실제 호스트 포트를 광고합니다. 방화벽에서 외부 공개 시 UDP 도 열어야 HTTP/3 가 쓰입니다.
- 설정 파일: [docker/Caddyfile](docker/Caddyfile).

### 기타 설정

- **Gemini API 키**: `.env` 의 `GEMINI_API_KEY` 를 비워 두면 셸(Windows 는 사용자 환경 변수 포함)의 값을 씁니다. 키를 파일에 평문으로 저장하지 않으려면 비워 두세요.
- **파일 공유**: 컨테이너는 호스트 파일을 볼 수 없으므로 `HOST_SHARE_DIR`(기본: 사용자 홈)을 `/share` 로 마운트하고 경로를 자동 변환합니다.
  Claude 가 `C:\Users\me\video.mp4` 를 넘기면 서버는 `/share/video.mp4` 를 읽습니다. 공유 폴더 밖의 파일은 읽을 수 없습니다.
- **생성 미디어**: 이미지·음성·영상은 모두 S3 호환 저장소에 보관하고, 응답에는 바이트 대신 `gemini://media/<이름>` ResourceLink 를 돌려줍니다.
  클라이언트는 MCP `resources/read` 또는 `GET /mcp/media/<이름>`(MCP 와 같은 API 키)으로 받아 갑니다.
  객체마다 메타데이터(`x-amz-meta-tool`, `-model`, `-prompt`(최대 약 1.2KB, 퍼센트 인코딩), `-consumer`(요청한 API 키), `-interaction-id`)를 함께 저장합니다.
  자동 삭제는 없고, 관리자가 대시보드 **생성 미디어** 화면에서 보고 지웁니다. 이름이 역순 시각으로 시작해 S3 목록이 최신순으로 나옵니다.

  | 저장소 | 설정 |
  |---|---|
  | 내장 S3 (기본, SeaweedFS 컨테이너) | `.env` 기본값 그대로(`COMPOSE_PROFILES=s3`, `S3_ENDPOINT=http://s3:8333`, `S3_BUCKET=gemini-media`). `S3_SECRET_KEY` 는 `docker.ps1`/`docker.sh` 가 무작위로 만듭니다. 버킷과 계정은 첫 실행 때 자동 생성되고 데이터는 `s3-data` 볼륨에 남습니다 |
  | 외부 S3 (AWS S3, Cloudflare R2, Backblaze B2, MinIO 등) | `COMPOSE_PROFILES` 를 비우고(내장 S3 끄기) `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_ENDPOINT`(AWS 면 비우고 `S3_REGION`) 설정 |

  버킷이 없으면 MCP 서버가 시작할 때 만듭니다. 외부 S3 키에는 해당 버킷(접두사 `S3_PREFIX`, 기본 `gemini-mcp-media/`)의 읽기·쓰기·목록·삭제 권한이 필요합니다.
  S3 접속 정보는 MCP 서버만 갖고, 대시보드는 MCP 서버의 내부 API(`/internal/media`, `backend` 네트워크, APISIX 가 라우팅하지 않음)로 목록 조회·삭제를 요청합니다.
  APISIX 는 요청한 키의 컨슈머 이름을 `X-Mcp-Consumer` 헤더로 넘기며(클라이언트가 보낸 값은 덮어씀), 대시보드가 이를 키 이름으로 바꿔 보여 줍니다.
  이전 버전의 `.env`(S3 설정 없음)는 `docker.ps1`/`docker.sh` 가 실행될 때 내장 S3 설정을 자동으로 추가합니다.
  예전 버전이 공유 폴더에 남긴 `<OUTPUT_SUBDIR>/.media` 는 더 이상 쓰지 않으니 지워도 됩니다.
- **데이터 볼륨**: `etcd-data`(라우트·API 키), `loki-data`(로그), `dashboard-data`(관리자 비밀번호 해시), `caddy-data`(인증서), `s3-data`(내장 S3).
- 설정 항목 전체는 [docker/.env.example](docker/.env.example) 을 참고하세요. 설정 우선순위는 셸 환경 변수 > `.env` 입니다 (docker compose 와 동일).

## MCP 서버 단독 실행 (개발용)

필요: .NET 10 SDK, Visual Studio C++ 빌드 도구(AOT 게시 시).

```powershell
.\scripts\publish-aot.ps1                       # publish\win-x64\GeminiMcp.exe (단일 네이티브 실행 파일)
$env:GEMINI_API_KEY = "<your key>"
# 이미지·음성·영상 생성에는 S3 저장소가 필요합니다. 로컬에서는 SeaweedFS 를 띄워 씁니다.
docker run -d --name gmcp-s3 -p 127.0.0.1:8333:8333 -v gmcp-s3:/data -e AWS_ACCESS_KEY_ID=dev -e AWS_SECRET_ACCESS_KEY=devsecret123 chrislusf/seaweedfs:4.48
$env:GEMINI_MCP_S3_ENDPOINT = "http://127.0.0.1:8333"; $env:GEMINI_MCP_S3_BUCKET = "gemini-media"
$env:GEMINI_MCP_S3_ACCESS_KEY = "dev"; $env:GEMINI_MCP_S3_SECRET_KEY = "devsecret123"
.\publish\win-x64\GeminiMcp.exe                 # http://127.0.0.1:5100/mcp (인증 없음, 루프백 전용)
```

| 환경 변수 | 기본값 | 설명 |
|---|---|---|
| `GEMINI_API_KEY` | (필수) | `GOOGLE_API_KEY` 도 인식 |
| `GEMINI_MCP_OUTPUT_DIR` | `~/gemini-mcp-output` | 모델 선택(`.gemini-mcp-models.json`) 저장 폴더 |
| `GEMINI_MCP_S3_BUCKET` | (없음) | 생성 미디어를 보관할 S3 버킷. 없으면 이미지·음성·영상 도구가 오류를 반환 |
| `GEMINI_MCP_S3_ENDPOINT` | `https://s3.<region>.amazonaws.com` | S3 호환 서버 주소 (SeaweedFS, R2, MinIO 등) |
| `GEMINI_MCP_S3_REGION` | `us-east-1` | 서명 리전 (`AWS_REGION` 도 인식) |
| `GEMINI_MCP_S3_ACCESS_KEY` / `_SECRET_KEY` | (S3 사용 시 필수) | `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` 도 인식 |
| `GEMINI_MCP_S3_PREFIX` | `gemini-mcp-media/` | 버킷 안 키 접두사 |
| `GEMINI_MCP_S3_PATH_STYLE` | 엔드포인트 지정 시 `true` | path-style 주소 사용 여부 |
| `GEMINI_MCP_PUBLIC_URL` | (요청 Host) | 미디어 다운로드 URL 의 기준 주소 |
| `GEMINI_INLINE_LIMIT_BYTES` | `15728640` | 이보다 큰 파일은 Files API 로 업로드 |
| `MCP_ALLOWED_ORIGINS` | (localhost 만) | 허용할 브라우저 Origin, 쉼표 구분 |
| `ASPNETCORE_URLS` / `--urls` | `http://127.0.0.1:5100` | 수신 주소 |
| `GEMINI_MCP_PATH_MAPS` | (없음) | `호스트경로=서버경로` 매핑 (`;` 구분). Docker 에서 사용 |

MCP 서버 자체는 인증하지 않습니다. 루프백 또는 내부 네트워크에만 노출하고, 외부 접속은 APISIX 를 거치게 하세요.

대시보드 서버 환경 변수(`APISIX_ADMIN_URL`, `LOKI_URL`, `MCP_URL`, `DASHBOARD_UPSTREAM`, `DATA_DIR`, `LOG_RETENTION_DAYS`)는
[dashboard/server/utils/env.ts](dashboard/server/utils/env.ts) 를 참고하세요. 화면만 고칠 때는 `cd dashboard && deno task dev` 로 띄울 수 있지만,
관리 API 는 APISIX·Loki 가 필요하므로 전체 동작은 Docker 구성에서 확인하세요.

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

**대시보드**: `cd dashboard && deno task typecheck` (vue-tsc, 화면과 서버).

**MCP 서버**: xUnit v3 (Microsoft.Testing.Platform) 테스트가 **AOT 로 게시된 `publish\win-x64\GeminiMcp.exe` 를 직접 띄워**
공식 MCP C# 클라이언트로 검증합니다. 먼저 `scripts\publish-aot.ps1` 로 게시하세요.

```powershell
dotnet test --project tests/GeminiMcp.Tests                                  # 전체 (실제 Gemini API 호출 포함)
dotnet test --project tests/GeminiMcp.Tests --filter-not-trait "Category=Live" # API 호출 없이 프로토콜/보안만
```

| 테스트 클래스 | 내용 |
|---|---|
| `HttpEndpointTests` | `/health`, `/skill`, 외부 Origin 403, localhost Origin 허용, (APISIX 경유 시) 키 없음·잘못된 키 401 |
| `McpProtocolTests` | initialize(서버 정보·instructions·capabilities), 구버전 프로토콜(2025-03-26/06-18/11-25) 협상, 도구 목록·스키마, 스킬 프롬프트·리소스, 도구 오류(isError) |
| `GeminiToolTests` (Live) | 9개 도구 실호출: 검색 출처, JSON Schema, Python 실행, 이미지 생성→재인식, TTS→받아쓰기, YouTube |
| `FilesApiUploadTests` (Live) | 인라인 한도를 넘는 파일이 Files API 업로드 경로로 처리되는지 |
| `MediaStoreTests` | 미디어 리소스 템플릿, 경로 탈출 차단, 내부 API Origin 거부·404, (Live) 음성 생성→최신순 목록·메타데이터(한글 프롬프트, API 키)·통계→관리자 삭제 후 조회·재사용 실패. S3 는 `GEMINI_MCP_TEST_S3_*` 가 있을 때만 |
| `NoMediaStoreTests` | S3 가 없으면 미디어 도구가 Gemini 호출 전에 오류, 다운로드·내부 API 503 |

- `GEMINI_API_KEY` 가 없으면 Live 테스트는 건너뜁니다 (Windows 에서는 사용자 환경 변수도 읽음).
- 다른 실행 파일을 검증하려면 `GEMINI_MCP_EXE` 환경 변수로 경로를 지정합니다.
- **실행 중인 배포(APISIX 경유) 검증**: `GEMINI_MCP_URL` 을 지정하면 프로세스를 띄우지 않고 그 서버를 테스트합니다.
  이미지·음성 테스트가 호스트 경로로 파일을 주고받으므로 경로 매핑까지 검증됩니다.
  (`FilesApiUploadTests` 는 서버 설정을 바꿔야 하므로 항상 로컬 AOT 바이너리로 실행합니다.)

  ```powershell
  $env:GEMINI_MCP_URL = "https://127.0.0.1:5100"   # Caddy 루트 인증서를 Windows 사용자 저장소에 신뢰시켜야 합니다 (cert 명령 안내 참고)
  $env:GEMINI_MCP_API_KEY = "<대시보드에서 발급한 키>"
  $env:GEMINI_MCP_TEST_OUTPUT_DIR = "$HOME\gemini-mcp-output"   # HOST_SHARE_DIR\OUTPUT_SUBDIR
  dotnet test --project tests/GeminiMcp.Tests
  ```
- **S3 저장소**: 이미지·음성·영상 테스트에는 S3 가 필요합니다. 일회용 SeaweedFS 를 띄우고 주소와 키를 넘기면
  테스트 서버마다 새 버킷을 만듭니다 (없으면 해당 테스트는 건너뜁니다).

  ```powershell
  docker run -d --rm --name gmcp-s3-test -p 127.0.0.1:18333:8333 -e AWS_ACCESS_KEY_ID=testkey -e AWS_SECRET_ACCESS_KEY=testsecret123 chrislusf/seaweedfs:4.48
  $env:GEMINI_MCP_TEST_S3_ENDPOINT = "http://127.0.0.1:18333"
  $env:GEMINI_MCP_TEST_S3_ACCESS_KEY = "testkey"
  $env:GEMINI_MCP_TEST_S3_SECRET_KEY = "testsecret123"
  dotnet test --project tests/GeminiMcp.Tests
  ```
- `global.json` 이 .NET 10 `dotnet test` 를 Microsoft.Testing.Platform 모드로 설정합니다.

## 구조

```
src/GeminiMcp/                MCP 서버 (.NET 10 Native AOT)
  Program.cs                  호스트, MCP 등록, Origin 검사, /skill·/health
  GeminiOptions.cs            설정(환경 변수)
  PathMapper.cs               호스트↔컨테이너 경로 변환
  Gemini/                     Gemini REST(v1beta) 클라이언트, 미디어 변환, 응답 파싱
  Gemini/MediaStore.cs        생성 미디어 보관(TTL)·리소스·정리, 로컬 디스크 저장소
  Gemini/MediaStore.cs        생성 미디어 보관(S3, 메타데이터)·리소스·목록/통계/삭제
  Gemini/S3MediaBackend.cs    S3 호환 저장소 (Signature V4 직접 구현, AOT)
  Tools/GeminiTools.cs        MCP 도구 9종
  Skill/                      내장 스킬 (SKILL.md + 리소스/프롬프트/설치)
dashboard/                    대시보드 (Nuxt 4 + Vuetify 4 + TypeScript, Deno)
  app/pages/                  index(모니터링), logs, keys, media(생성 미디어), settings, login
  app/components/             TimeSeriesChart(SVG), StatTile
  app/plugins/vuetify.ts      Material Design 테마(라이트/다크)
  server/api/admin/           관리 API (인증, 키, 로그, 지표, 상태, 도구 목록, 생성 미디어)
  server/utils/apisix.ts      APISIX Admin API: 라우트 등록, API 키(컨슈머·credential), 허용 목록 동기화
  server/utils/loki.ts        Loki: 로그 조회, LogQL 지표, 관리/시스템 로그 기록
  server/utils/media.ts       생성 미디어: MCP 서버 내부 API(/internal/media) 호출
  server/utils/auth.ts        관리자 설정 코드·비밀번호(PBKDF2)·세션
  server/plugins/bootstrap.ts 시작 시 라우트 등록, 만료 키 동기화, 상태 확인
  Dockerfile                  Nitro deno-server 빌드 → Deno distroless 이미지
scripts/publish-aot.ps1       Native AOT 게시
docker/
  docker.ps1 | docker.sh      Docker self-hosting (setup/up/down/restart/rebuild/logs/status/cert/reset-admin)
  Dockerfile                  MCP 서버: AOT 빌드 → chiseled 런타임 이미지 (빌드 컨텍스트: 저장소 루트)
  Dockerfile.dockerignore     빌드 컨텍스트 제외 목록
  docker-compose.yml          MCP 서버 + APISIX + etcd + Loki + 대시보드 + Caddy
  apisix/config.yaml          APISIX 설정 (Admin API 는 control 네트워크 전용, 사용 플러그인)
  loki/loki.yaml              Loki 단일 프로세스 설정 (파일시스템, 보존 기간)
  Caddyfile                   TLS 종단 + zstd/gzip 압축 리버스 프록시 → APISIX
  .env.example                Docker 설정 예시
tests/GeminiMcp.Tests/        xUnit v3 E2E 테스트 (AOT 바이너리 또는 APISIX 경유 배포 대상)
```

MCP 프로토콜은 공식 [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.3.0 (stateless Streamable HTTP)을 사용하며,
Gemini 요청/응답은 AOT 안전한 `JsonNode` 로 처리합니다. 트리밍/AOT 경고는 오류로 승격되어 있어 경고 0개로 빌드됩니다.
