---
name: gemini-mcp
description: Gemini MCP 서버(gemini_* 도구) 사용법. Claude 가 직접 할 수 없거나 약한 작업 — 실시간 Google 검색, 웹 페이지 읽기, 영상/오디오 분석·전사, 이미지 생성·편집, 음성 합성(TTS), Python 샌드박스 실행, 대용량 문서 분석, 다른 모델의 세컨드 오피니언 — 이 필요할 때 사용합니다.
---

# Gemini MCP 스킬

이 MCP 서버는 Google Gemini API 를 Claude 에 연결합니다. **Claude 가 스스로 잘 할 수 있는 일은 직접 하고**,
아래 표처럼 Claude 에게 없는 능력이 필요할 때만 Gemini 도구를 호출하세요.

## 언제 어떤 도구를 쓰나

| 상황 | 도구 | 핵심 인수 |
|---|---|---|
| 최신 정보·뉴스·가격·버전 확인 (학습 이후 정보) | `gemini_web_search` | `query`, `instructions` |
| 특정 웹 페이지(최대 20개) 읽기·요약·비교 | `gemini_read_urls` | `urls[]`, `prompt`, `also_search` |
| 영상·오디오 이해 (요약, 장면 설명, 받아쓰기, 화자 구분, 번역) / YouTube | `gemini_analyze_media` | `files[]`, `prompt` |
| 이미지 생성 / 기존 이미지 편집·합성 | `gemini_generate_image` | `prompt`, `input_images[]`, `aspect_ratio`, `output_path` |
| 텍스트 → 음성 파일(WAV) | `gemini_text_to_speech` | `text`, `voice`, `style`, `output_path` |
| 계산·데이터 분석을 Python 샌드박스에서 실행 | `gemini_run_code` | `task`, `files[]` |
| 세컨드 오피니언, 교차 검증, 초대형 컨텍스트(1M 토큰) 분석, JSON 구조화 출력 | `gemini_ask` | `prompt`, `files[]`, `model`, `json_schema` |
| 사용 가능한 모델 확인 | `gemini_list_models` | `filter` |
| 이 설명서 다시 보기 | `gemini_usage_guide` | — |

## 사용 규칙

1. **파일 경로는 MCP 서버가 실행 중인 PC 기준의 절대 경로**를 사용합니다. (예: `C:\Users\me\Videos\demo.mp4`)
   http(s) URL 과 YouTube URL 도 그대로 넘길 수 있습니다.
   15MB 를 넘는 파일은 서버가 자동으로 Gemini Files API 에 업로드합니다 (영상은 처리 대기로 수십 초 걸릴 수 있음).
2. **모델 선택**: `model` 을 생략하면 빠른 기본 모델(flash). 어려운 추론·긴 분석은 `model: "pro"`.
   특정 버전이 필요하면 `gemini_list_models` 로 이름을 확인한 뒤 그대로 전달합니다.
3. **생성 결과물**(이미지, 음성)은 파일로 저장되고 경로가 반환됩니다. `output_path` 를 생략하면 서버의 기본 출력 폴더
   (`~/gemini-mcp-output`, `GEMINI_MCP_OUTPUT_DIR` 로 변경 가능)에 저장됩니다.
   생성된 이미지를 직접 확인하려면 `return_image: true` 를 주거나, 반환된 경로의 파일을 읽으세요.
4. **결과 검증**: Gemini 의 답변도 틀릴 수 있습니다. 검색 결과는 `## Sources` 의 출처를 사용자에게 함께 제시하고,
   중요한 결론은 Claude 가 한 번 더 검토하세요. 응답 끝의 `[gemini model=... tokens ...]` 줄은 메타 정보입니다.
5. **프롬프트 작성 팁**: Gemini 에게는 맥락이 없습니다. 필요한 배경·제약·원하는 출력 형식을 프롬프트에 모두 적으세요.
   이미지 생성은 피사체·스타일·구도·조명·색감·넣을 문구를 구체적으로 적을수록 품질이 좋아집니다.
6. 민감 정보(비밀번호, 키, 개인정보)가 담긴 파일·텍스트는 외부 API(Gemini)로 전송되므로 보내기 전에 사용자에게 확인하세요.

## 예시

**최신 정보 조사**
```json
{ "name": "gemini_web_search", "arguments": { "query": ".NET 10 최신 서비스 릴리스 버전과 주요 변경 사항", "instructions": "한국어로, 버전 번호와 날짜를 표로" } }
```

**YouTube 영상 요약**
```json
{ "name": "gemini_analyze_media", "arguments": { "files": ["https://www.youtube.com/watch?v=XXXXXXXXXXX"], "prompt": "핵심 내용을 타임스탬프(mm:ss)와 함께 5개 항목으로 요약" } }
```

**회의 녹음 전사**
```json
{ "name": "gemini_analyze_media", "arguments": { "files": ["C:\\Recordings\\meeting.m4a"], "prompt": "화자를 구분해 전사하고, 마지막에 결정 사항과 액션 아이템을 정리" } }
```

**이미지 생성 / 편집**
```json
{ "name": "gemini_generate_image", "arguments": { "prompt": "미니멀한 플랫 스타일의 고양이 앱 아이콘, 파스텔 블루 배경", "aspect_ratio": "1:1", "output_path": "C:\\Work\\icon.png" } }
{ "name": "gemini_generate_image", "arguments": { "prompt": "배경을 해질녘 해변으로 바꿔줘", "input_images": ["C:\\Work\\photo.jpg"] } }
```

**음성 합성**
```json
{ "name": "gemini_text_to_speech", "arguments": { "text": "안녕하세요, 오늘의 브리핑을 시작합니다.", "voice": "Kore", "style": "차분한 뉴스 앵커처럼" } }
```

**세컨드 오피니언 / 구조화 출력**
```json
{ "name": "gemini_ask", "arguments": { "prompt": "다음 코드의 동시성 버그를 찾아줘: ...", "model": "pro" } }
{ "name": "gemini_ask", "arguments": { "prompt": "이 PDF 의 계약 당사자와 금액을 추출", "files": ["C:\\Docs\\contract.pdf"], "json_schema": "{\"type\":\"object\",\"properties\":{\"parties\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"amount\":{\"type\":\"string\"}}}" } }
```

## 오류 대응

- `GEMINI_API_KEY 가 설정되지 않았습니다` → 서버 실행 환경에 `GEMINI_API_KEY` 환경 변수를 설정 후 재시작.
- `Gemini API 오류 (404 ...)` → 모델 이름 오류. `gemini_list_models` 로 확인.
- `Gemini API 오류 (429 ...)` → 할당량 초과. 잠시 후 재시도하거나 `flash` 모델 사용.
- `파일을 찾을 수 없습니다` → 경로가 서버 PC 기준 절대 경로인지 확인.
