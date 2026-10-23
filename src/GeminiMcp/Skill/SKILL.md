---
name: gemini-mcp
description: Gemini MCP 서버(gemini_* 도구) 사용법. Claude 가 직접 할 수 없거나 약한 작업 — 실시간 Google 검색, 웹 페이지 읽기, 영상/오디오 분석·전사, 이미지 생성·편집, 영상 생성·편집, 음성 합성(TTS), Python 샌드박스 실행, 대용량 문서 분석, 다른 모델의 세컨드 오피니언 — 이 필요할 때 사용합니다.
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
| 이미지 생성 / 기존 이미지 편집·합성 | `gemini_generate_image` | `prompt`, `input_images[]`, `aspect_ratio`, `return_image` |
| 텍스트 → 음성 파일(WAV) | `gemini_text_to_speech` | `text`, `voice`, `style` |
| 계산·데이터 분석을 Python 샌드박스에서 실행 | `gemini_run_code` | `task`, `files[]` |
| 세컨드 오피니언, 교차 검증, 초대형 컨텍스트(1M 토큰) 분석, JSON 구조화 출력 | `gemini_ask` | `prompt`, `files[]`, `model`, `json_schema` |
| 텍스트·이미지 → 영상(MP4) 생성, 기존 영상 편집·연장 | `gemini_generate_video` | `prompt`, `input_images[]`, `input_video`, `previous_interaction_id`, `aspect_ratio`, `resolution` |
| 사용 가능한 모델을 용도별·최신순으로 확인 (★ = 현재 선택, ⚠ = 종료된 모델) | `gemini_list_models` | `category`, `filter`, `refresh` |
| 용도별 기본 모델 변경 | `gemini_select_model` | `category`, `model` |
| 이 설명서 다시 보기 | `gemini_usage_guide` | — |

## 사용 규칙

1. **파일 경로는 MCP 서버가 실행 중인 PC 기준의 절대 경로**를 사용합니다. (예: `C:\Users\me\Videos\demo.mp4`)
   http(s) URL 과 YouTube URL 도 그대로 넘길 수 있습니다.
   15MB 를 넘는 파일은 서버가 자동으로 Gemini Files API 에 업로드합니다 (영상은 처리 대기로 수십 초 걸릴 수 있음).
2. **모델 선택**: 모델은 환경 변수가 아니라 도구로 고릅니다. 용도(category)는 `text`, `pro`, `image`, `tts`, `video` 입니다.
   - `model` 을 생략하면 그 도구 용도에 선택된 모델을 씁니다 (처음에는 내장 기본값, 예: text = flash).
     어려운 추론·긴 분석은 `model: "pro"`.
   - 계속 쓸 모델을 바꾸려면 `gemini_list_models { category }` 로 후보를 보고 `gemini_select_model { category, model }` 로 선택합니다.
     선택은 서버 전역이며 모든 클라이언트에 적용되고 재시작 후에도 유지됩니다. `model: "default"` 로 되돌리고,
     `model: "newest"` 는 그 용도의 최신 모델을 고릅니다.
   - 목록은 최신순이며 1시간 캐시됩니다(`refresh: true` 로 갱신). 현재 선택 옆에 ⚠ 가 보이면 그 모델이 종료된 것이므로
     표시된 후보로 교체하세요. `[preview]` 모델은 예고 없이 종료될 수 있습니다.
   - 이번 호출만 다른 모델을 쓰려면 각 도구의 `model` 인수에 이름을 넘깁니다. 사용자가 요청하지 않았다면 전역 선택은 바꾸지 마세요.
3. **영상 생성**은 수 분이 걸릴 수 있습니다. 결과의 `interaction_id` 를 `previous_interaction_id` 로 넘기면
   방금 만든 영상을 이어서 편집·연장합니다(재업로드 불필요). 네거티브 프롬프트 필드는 없으므로 피할 내용은 프롬프트에 문장으로 적습니다.
4. **생성 결과물**(이미지, 음성, 영상)은 서버 저장소(로컬 디스크 또는 S3)에 보관 기간(기본 48시간, Gemini Files API 와 같음) 동안 보관되고
   `gemini://media/<이름>` **ResourceLink** 로 반환됩니다. 서버는 결과물을 영구 저장하지 않습니다.
   바이트는 응답에 넣지 않으므로 토큰을 거의 쓰지 않습니다. 필요할 때만 리소스로 읽거나(resources/read),
   다운로드 URL(`/mcp/media/<이름>`, MCP 와 같은 API 키)로 받으세요.
   - ⚠ **곧 삭제됩니다.** 보관 기간이 지나면(정확한 시각은 결과에 표시) 파일이 자동 삭제되고 복구할 수 없으며, 그 URI 를 다른 도구에 넘겨도 실패합니다.
     사용자에게 결과물을 전달하거나 나중에 다시 쓸 예정이면 그 전에 내려받아 클라이언트 쪽에 저장하세요.
     사용자에게 링크만 전달할 때는 곧 만료된다는 점도 함께 알리세요.
   - 다른 도구의 입력(`files`, `input_images`, `input_video`)에 `gemini://media/...` URI 를 그대로 넘길 수 있습니다.
   - 생성된 이미지를 직접 봐야 할 때만 `return_image: true` 를 주세요(이미지 한 장당 수천 토큰).
5. **결과 검증**: Gemini 의 답변도 틀릴 수 있습니다. 검색 결과는 `## Sources` 의 출처를 사용자에게 함께 제시하고,
   중요한 결론은 Claude 가 한 번 더 검토하세요. 응답 끝의 `[gemini model=... tokens ...]` 줄은 메타 정보입니다.
6. **프롬프트 작성 팁**: Gemini 에게는 맥락이 없습니다. 필요한 배경·제약·원하는 출력 형식을 프롬프트에 모두 적으세요.
   이미지 생성은 피사체·스타일·구도·조명·색감·넣을 문구를 구체적으로 적을수록 품질이 좋아집니다.
7. 민감 정보(비밀번호, 키, 개인정보)가 담긴 파일·텍스트는 외부 API(Gemini)로 전송되므로 보내기 전에 사용자에게 확인하세요.

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
{ "name": "gemini_generate_image", "arguments": { "prompt": "미니멀한 플랫 스타일의 고양이 앱 아이콘, 파스텔 블루 배경", "aspect_ratio": "1:1" } }
{ "name": "gemini_generate_image", "arguments": { "prompt": "배경을 해질녘 해변으로 바꿔줘", "input_images": ["C:\\Work\\photo.jpg"] } }
```

**영상 생성 / 이어서 편집**
```json
{ "name": "gemini_generate_video", "arguments": { "prompt": "비 오는 밤 네온 거리를 걷는 고양이, 낮은 앵글로 천천히 따라가는 카메라, 빗소리", "aspect_ratio": "16:9" } }
{ "name": "gemini_generate_video", "arguments": { "prompt": "이 사진 속 인물이 손을 흔들며 미소 짓는 장면", "input_images": ["C:\\Work\\portrait.jpg"], "aspect_ratio": "9:16" } }
{ "name": "gemini_generate_video", "arguments": { "prompt": "마지막에 우산을 펼치는 장면으로 연장", "previous_interaction_id": "v1_..." } }
```

**모델 확인 / 선택**
```json
{ "name": "gemini_list_models", "arguments": { "category": "image" } }
{ "name": "gemini_select_model", "arguments": { "category": "text", "model": "gemini-3.1-pro-preview" } }
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
- `Gemini API 오류 (404 ...)` → 모델 이름 오류 또는 종료된 모델. `gemini_list_models` 로 확인하고 필요하면 `gemini_select_model` 로 교체.
- `Gemini API 오류 (429 ...)` → 할당량 초과. 잠시 후 재시도하거나 `flash` 모델 사용.
- `파일을 찾을 수 없습니다` → 경로가 서버 PC 기준 절대 경로인지 확인.
