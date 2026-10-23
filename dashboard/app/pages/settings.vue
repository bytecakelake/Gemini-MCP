<script setup lang="ts">
import { mdiCheck, mdiContentCopy } from "@mdi/js";
import type { Status } from "~/types/api";

const api = useApi();
const status = ref<Status>();
const form = reactive({ current: "", next: "", confirm: "", saving: false, error: "", done: false });
const copied = ref("");

onMounted(async () => {
  status.value = await api<Status>("/status").catch(() => undefined);
});

const origin = computed(() => (import.meta.client ? location.origin : ""));
const snippets = computed(() => [
  {
    id: "cli",
    title: "Claude Code (CLI)",
    code: `claude mcp add --transport http gemini ${origin.value}/mcp --header "Authorization: Bearer <API 키>"`,
  },
  {
    id: "json",
    title: "프로젝트 .mcp.json",
    code: JSON.stringify(
      {
        mcpServers: {
          gemini: {
            type: "http",
            url: `${origin.value}/mcp`,
            headers: { Authorization: "Bearer ${GEMINI_MCP_API_KEY}" },
          },
        },
      },
      null,
      2,
    ),
  },
]);

async function copy(id: string, text: string) {
  await navigator.clipboard.writeText(text);
  copied.value = id;
  setTimeout(() => (copied.value = ""), 2000);
}

async function changePassword() {
  form.error = "";
  form.done = false;
  if (form.next.length < 10) return (form.error = "새 비밀번호는 10자 이상이어야 합니다.");
  if (form.next !== form.confirm) return (form.error = "새 비밀번호가 일치하지 않습니다.");
  form.saving = true;
  try {
    await api("/auth/password", { method: "POST", body: { current: form.current, next: form.next } });
    Object.assign(form, { current: "", next: "", confirm: "", done: true });
  } catch (err) {
    form.error = errorMessage(err);
  } finally {
    form.saving = false;
  }
}
</script>

<template>
  <div>
    <h1 class="page-title mb-4">설정</h1>
    <v-row>
      <v-col cols="12" lg="7">
        <v-card class="mb-4">
          <v-card-item title="클라이언트 연결" subtitle="API 키 페이지에서 발급한 키를 헤더에 넣으세요." />
          <v-card-text>
            <div v-for="s in snippets" :key="s.id" class="mb-4">
              <div class="d-flex align-center mb-1">
                <span class="text-body-medium font-weight-medium">{{ s.title }}</span>
                <v-spacer />
                <v-btn
                  size="small"
                  variant="text"
                  :prepend-icon="copied === s.id ? mdiCheck : mdiContentCopy"
                  @click="copy(s.id, s.code)"
                >
                  {{ copied === s.id ? "복사됨" : "복사" }}
                </v-btn>
              </div>
              <v-sheet border rounded="lg" class="pa-3 mono" style="white-space: pre-wrap; word-break: break-all">{{ s.code }}</v-sheet>
            </div>
            <div class="text-body-medium text-medium-emphasis">
              .mcp.json 예시는 키를 파일에 쓰지 않도록 Claude Code 의 환경 변수 확장(<span class="mono">${GEMINI_MCP_API_KEY}</span>)을 씁니다.
              내장 스킬 문서: <a :href="`${origin}/skill`" target="_blank" rel="noopener">{{ origin }}/skill</a>
            </div>
          </v-card-text>
        </v-card>

        <v-card v-if="status">
          <v-card-item title="게이트웨이" />
          <v-table density="compact">
            <tbody>
              <tr><td>버전</td><td class="tabular">v{{ status.gateway.version }} (Deno {{ status.gateway.deno }})</td></tr>
              <tr><td>시작 시각</td><td class="tabular">{{ formatDateTime(status.gateway.startedAt) }}</td></tr>
              <tr><td>로그·지표 보존</td><td>{{ status.gateway.logRetentionDays }}일 (LOG_RETENTION_DAYS)</td></tr>
              <tr v-for="u in status.upstreams" :key="u.name"><td>{{ u.name }}</td><td class="mono">{{ u.url }}</td></tr>
            </tbody>
          </v-table>
        </v-card>
      </v-col>

      <v-col cols="12" lg="5">
        <v-card>
          <v-card-item title="관리자 비밀번호 변경" subtitle="변경하면 다른 모든 세션이 로그아웃됩니다." />
          <v-card-text>
            <v-alert v-if="form.error" type="error" variant="tonal" density="compact" class="mb-4">{{ form.error }}</v-alert>
            <v-alert v-if="form.done" type="success" variant="tonal" density="compact" class="mb-4">비밀번호를 변경했습니다.</v-alert>
            <v-form @submit.prevent="changePassword">
              <v-text-field v-model="form.current" label="현재 비밀번호" type="password" autocomplete="current-password" class="mb-2" />
              <v-text-field v-model="form.next" label="새 비밀번호 (10자 이상)" type="password" autocomplete="new-password" class="mb-2" />
              <v-text-field v-model="form.confirm" label="새 비밀번호 확인" type="password" autocomplete="new-password" class="mb-2" />
              <v-btn type="submit" color="primary" :loading="form.saving">변경</v-btn>
            </v-form>
            <v-divider class="my-4" />
            <div class="text-body-medium text-medium-emphasis">
              비밀번호를 잊었다면 서버에서 관리자 계정을 초기화한 뒤 새 설정 코드로 다시 만드세요:
              <div class="mono mt-1">docker/docker.ps1 reset-admin</div>
            </div>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>
  </div>
</template>
