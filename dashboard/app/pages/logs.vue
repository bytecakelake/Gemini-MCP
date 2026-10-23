<script setup lang="ts">
import {
  mdiAlert,
  mdiAlertOctagon,
  mdiInformationOutline,
  mdiMagnify,
  mdiPause,
  mdiPlay,
  mdiRefresh,
} from "@mdi/js";
import type { ApiKey, LogEntry, LogKind, LogLevel } from "~/types/api";

const api = useApi();
const items = ref<LogEntry[]>([]);
const nextBefore = ref<string | null>(null);
const loading = ref(false);
const error = ref("");
const live = ref(true);
const keys = ref<ApiKey[]>([]);

const filters = reactive({ kind: "", level: "", keyId: "", tool: "", q: "" });
const kinds = [
  { title: "전체", value: "" },
  { title: "MCP 요청", value: "request" },
  { title: "관리 작업", value: "audit" },
  { title: "시스템", value: "system" },
];
const levels = [
  { title: "전체", value: "" },
  { title: "정보", value: "info" },
  { title: "경고", value: "warn" },
  { title: "오류", value: "error" },
];
const toolOptions = ref<string[]>([]);

const headers = [
  { title: "시각", key: "ts", width: 150 },
  { title: "수준", key: "level", width: 90 },
  { title: "종류", key: "kind", width: 100 },
  { title: "내용", key: "message" },
  { title: "API 키", key: "keyName", width: 140 },
  { title: "상태", key: "status", width: 80, align: "end" as const },
  { title: "지연", key: "durationMs", width: 90, align: "end" as const },
];

const levelMeta: Record<LogLevel, { icon: string; color: string; text: string }> = {
  info: { icon: mdiInformationOutline, color: "info", text: "정보" },
  warn: { icon: mdiAlert, color: "warning", text: "경고" },
  error: { icon: mdiAlertOctagon, color: "error", text: "오류" },
};
const kindText: Record<LogKind, string> = { request: "MCP 요청", audit: "관리 작업", system: "시스템" };

function query(before?: string) {
  return { ...Object.fromEntries(Object.entries(filters).filter(([, v]) => v)), before, limit: 100 };
}

async function load(more = false) {
  loading.value = true;
  try {
    const res = await api<{ items: LogEntry[]; nextBefore: string | null }>("/logs", {
      query: query(more ? nextBefore.value ?? undefined : undefined),
    });
    items.value = more ? [...items.value, ...res.items] : res.items;
    nextBefore.value = res.nextBefore;
    error.value = "";
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    loading.value = false;
  }
}

// 실시간: 3초마다 최신 로그를 가져와 처음 보는 항목만 맨 위에 붙입니다 (Loki 는 요청 후 1~2초 안에 기록).
async function poll() {
  if (!live.value || loading.value) return;
  try {
    const res = await api<{ items: LogEntry[] }>("/logs", { query: query() });
    const seen = new Set(items.value.map((i) => i.id));
    const fresh = res.items.filter((i) => !seen.has(i.id));
    if (fresh.length) items.value = [...fresh, ...items.value].slice(0, 2000);
    for (const tool of fresh.map((i) => i.tool)) if (tool && !toolOptions.value.includes(tool)) toolOptions.value.push(tool);
  } catch {
    // 다음 주기에 다시 시도합니다.
  }
}

let debounce: ReturnType<typeof setTimeout> | undefined;
watch(filters, () => {
  clearTimeout(debounce);
  debounce = setTimeout(() => load(), 300);
});

let timer: ReturnType<typeof setInterval> | undefined;
onMounted(async () => {
  await load();
  timer = setInterval(poll, 3_000);
  try {
    keys.value = (await api<{ items: ApiKey[] }>("/keys")).items;
    toolOptions.value = (await api<{ items: { name: string }[] }>("/tools")).items.map((t) => t.name);
  } catch {
    // 필터 후보를 못 가져와도 로그 화면은 동작합니다.
  }
});
onBeforeUnmount(() => clearInterval(timer));
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-center ga-3 mb-4">
      <h1 class="page-title">로그</h1>
      <v-spacer />
      <v-btn
        :prepend-icon="live ? mdiPause : mdiPlay"
        :color="live ? 'primary' : undefined"
        variant="tonal"
        @click="live = !live"
      >
        {{ live ? "실시간 수신 중" : "실시간 꺼짐" }}
      </v-btn>
      <v-btn :icon="mdiRefresh" variant="text" :loading="loading" aria-label="새로고침" @click="load()" />
    </div>

    <v-card class="mb-4">
      <v-card-text>
        <v-row dense>
          <v-col cols="12" md="4">
            <v-text-field
              v-model="filters.q"
              :prepend-inner-icon="mdiMagnify"
              label="검색 (메시지, 키, 도구, IP, 상태)"
              clearable
              hide-details
              @click:clear="filters.q = ''"
            />
          </v-col>
          <v-col cols="6" md="2">
            <v-select v-model="filters.kind" :items="kinds" label="종류" hide-details />
          </v-col>
          <v-col cols="6" md="2">
            <v-select v-model="filters.level" :items="levels" label="수준" hide-details />
          </v-col>
          <v-col cols="6" md="2">
            <v-select
              v-model="filters.keyId"
              :items="[{ title: '전체', value: '' }, ...keys.map((k) => ({ title: k.name, value: k.id }))]"
              label="API 키"
              hide-details
            />
          </v-col>
          <v-col cols="6" md="2">
            <v-select
              v-model="filters.tool"
              :items="[{ title: '전체', value: '' }, ...toolOptions.map((t) => ({ title: t, value: t }))]"
              label="도구"
              hide-details
            />
          </v-col>
        </v-row>
      </v-card-text>
    </v-card>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

    <v-card>
      <v-data-table
        :headers="headers"
        :items="items"
        :loading="loading"
        item-value="id"
        density="compact"
        show-expand
        :items-per-page="-1"
        hide-default-footer
        no-data-text="로그가 없습니다."
      >
        <template #[`item.ts`]="{ item }">
          <span class="tabular text-no-wrap">{{ formatDateTime(item.ts) }}</span>
        </template>
        <template #[`item.level`]="{ item }">
          <v-chip :prepend-icon="levelMeta[item.level].icon" :color="levelMeta[item.level].color" size="small" variant="tonal">
            {{ levelMeta[item.level].text }}
          </v-chip>
        </template>
        <template #[`item.kind`]="{ item }">
          <span class="text-body-medium">{{ kindText[item.kind] }}</span>
        </template>
        <template #[`item.message`]="{ item }">
          <span class="text-body-medium">{{ item.message }}</span>
        </template>
        <template #[`item.keyName`]="{ item }">
          <span class="text-body-medium">{{ item.keyName ?? "—" }}</span>
        </template>
        <template #[`item.status`]="{ item }">
          <span class="tabular">{{ item.status ?? "—" }}</span>
        </template>
        <template #[`item.durationMs`]="{ item }">
          <span class="tabular">{{ item.durationMs === undefined ? "—" : formatMs(item.durationMs) }}</span>
        </template>
        <template #expanded-row="{ columns, item }">
          <tr>
            <td :colspan="columns.length" class="py-3">
              <v-row dense class="text-body-medium">
                <v-col cols="12" sm="6" md="3"><b>IP</b> · {{ item.ip || "—" }}</v-col>
                <v-col cols="12" sm="6" md="3"><b>HTTP</b> · {{ item.method ?? "—" }} {{ item.status ?? "" }}</v-col>
                <v-col cols="12" sm="6" md="3"><b>JSON-RPC</b> · {{ item.rpcMethod ?? "—" }}</v-col>
                <v-col cols="12" sm="6" md="3"><b>도구</b> · {{ item.tool ?? "—" }}</v-col>
                <v-col cols="12" sm="6" md="3">
                  <b>요청/응답</b> · {{ formatBytes(item.bytesIn ?? 0) }} / {{ formatBytes(item.bytesOut ?? 0) }}
                </v-col>
                <v-col cols="12" md="6"><b>User-Agent</b> · <span class="mono">{{ item.userAgent ?? "—" }}</span></v-col>
              </v-row>
            </td>
          </tr>
        </template>
      </v-data-table>
      <v-divider />
      <div class="d-flex align-center pa-3">
        <span class="text-body-small text-medium-emphasis">{{ formatNumber(items.length) }}건 표시</span>
        <v-spacer />
        <v-btn v-if="nextBefore" variant="text" :loading="loading" @click="load(true)">더 보기</v-btn>
      </div>
    </v-card>
  </div>
</template>
