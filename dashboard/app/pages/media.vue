<script setup lang="ts">
import {
  mdiDeleteOutline,
  mdiDownload,
  mdiFileOutline,
  mdiImageOutline,
  mdiInformationOutline,
  mdiMagnify,
  mdiMovieOpenOutline,
  mdiMusicNote,
  mdiRefresh,
} from "@mdi/js";
import type { MediaItem, MediaPage, MediaStats } from "~/types/api";

const api = useApi();
const items = ref<MediaItem[]>([]);
const nextCursor = ref<string | null>(null);
const stats = ref<MediaStats | null>(null);
const loading = ref(false);
const error = ref("");
const snackbar = ref("");
const selected = ref<string[]>([]);
const search = ref("");
const toolFilter = ref<string | null>(null);

const PAGE_SIZE = 50;

const headers = [
  { title: "", key: "preview", sortable: false, width: 72 },
  { title: "생성 시각", key: "lastModified", width: 150 },
  { title: "도구 / 모델", key: "tool" },
  { title: "프롬프트", key: "prompt", sortable: false },
  { title: "API 키", key: "keyName", width: 140 },
  { title: "크기", key: "size", align: "end" as const, width: 100 },
  { title: "", key: "actions", sortable: false, width: 120, align: "end" as const },
];

const toolLabels: Record<string, string> = {
  gemini_generate_image: "이미지",
  gemini_text_to_speech: "음성",
  gemini_generate_video: "영상",
};
const toolLabel = (tool?: string) => (tool ? toolLabels[tool] ?? tool : "—");
const toolOptions = computed(() =>
  [...new Set(items.value.map((i) => i.tool).filter((t): t is string => !!t))].map((t) => ({ title: toolLabel(t), value: t }))
);

const filtered = computed(() => {
  const q = search.value.trim().toLowerCase();
  return items.value.filter((item) =>
    (!toolFilter.value || item.tool === toolFilter.value)
    && (!q || [item.prompt, item.model, item.keyName, item.name].some((v) => v?.toLowerCase().includes(q)))
  );
});

const kind = (mime: string) => (mime.startsWith("image/") ? "image" : mime.startsWith("audio/") ? "audio" : mime.startsWith("video/") ? "video" : "file");
const kindIcon = { image: mdiImageOutline, audio: mdiMusicNote, video: mdiMovieOpenOutline, file: mdiFileOutline };
const contentUrl = (item: MediaItem, download = false) =>
  `/api/admin/media/${encodeURIComponent(item.name)}${download ? "?download=1" : ""}`;

async function loadStats() {
  try {
    stats.value = await api<MediaStats>("/media/stats");
  } catch {
    stats.value = null;
  }
}

async function load(more = false) {
  loading.value = true;
  try {
    const query: Record<string, string | number> = { limit: PAGE_SIZE };
    if (more && nextCursor.value) query.cursor = nextCursor.value;
    const page = await api<MediaPage>("/media", { query });
    items.value = more ? [...items.value, ...page.items] : page.items;
    nextCursor.value = page.nextCursor;
    if (!more) selected.value = [];
    error.value = "";
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    loading.value = false;
  }
}

function refresh() {
  load();
  loadStats();
}

onMounted(refresh);

// ---------- 상세 ----------
const detail = ref<MediaItem | null>(null);

// ---------- 삭제 ----------
const confirm = reactive({ open: false, names: [] as string[], running: false });

function askDelete(names: string[]) {
  if (names.length) Object.assign(confirm, { open: true, names, running: false });
}

async function runDelete() {
  confirm.running = true;
  try {
    if (confirm.names.length === 1) {
      await api(`/media/${encodeURIComponent(confirm.names[0]!)}`, { method: "DELETE" });
      snackbar.value = "미디어를 삭제했습니다.";
    } else {
      const result = await api<{ deleted: number; failed: { name: string; error: string }[] }>("/media/delete", {
        method: "POST",
        body: { names: confirm.names },
      });
      snackbar.value = result.failed.length
        ? `${result.deleted}개 삭제, ${result.failed.length}개 실패: ${result.failed[0]!.error}`
        : `${result.deleted}개를 삭제했습니다.`;
    }
    const removed = new Set(confirm.names);
    items.value = items.value.filter((i) => !removed.has(i.name));
    selected.value = selected.value.filter((n) => !removed.has(n));
    if (detail.value && removed.has(detail.value.name)) detail.value = null;
    loadStats();
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    confirm.open = false;
    confirm.running = false;
  }
}
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-center ga-3 mb-4">
      <h1 class="page-title">생성 미디어</h1>
      <v-chip v-if="stats" variant="tonal" size="small">{{ formatNumber(stats.count) }}개 · {{ formatBytes(stats.bytes) }}</v-chip>
      <v-spacer />
      <v-btn
        v-if="selected.length"
        color="error"
        variant="flat"
        :prepend-icon="mdiDeleteOutline"
        @click="askDelete(selected)"
      >
        선택 {{ selected.length }}개 삭제
      </v-btn>
      <v-btn variant="tonal" :prepend-icon="mdiRefresh" :loading="loading" @click="refresh">새로 고침</v-btn>
    </div>

    <v-alert type="info" variant="tonal" density="compact" class="mb-4">
      이미지·음성·영상 생성 도구의 결과물은 자동으로 지워지지 않고 S3 저장소에 계속 보관됩니다.
      필요 없는 객체를 골라 삭제하세요. 삭제한 객체는 되돌릴 수 없고, 에이전트가 가진 링크도 더 이상 열리지 않습니다.
      <div v-if="stats" class="text-body-small text-medium-emphasis mt-1 mono">{{ stats.store }}</div>
    </v-alert>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

    <v-card>
      <div class="d-flex flex-wrap ga-3 pa-3">
        <v-text-field
          v-model="search"
          :prepend-inner-icon="mdiMagnify"
          label="프롬프트·모델·키 검색 (불러온 목록 안에서)"
          density="compact"
          hide-details
          clearable
          style="min-width: 260px; flex: 1 1 320px"
        />
        <v-select
          v-model="toolFilter"
          :items="toolOptions"
          label="도구"
          density="compact"
          hide-details
          clearable
          style="max-width: 200px; min-width: 140px"
        />
      </div>
      <v-data-table
        v-model="selected"
        :headers="headers"
        :items="filtered"
        :loading="loading"
        item-value="name"
        show-select
        :items-per-page="-1"
        hide-default-footer
        :no-data-text="loading ? '불러오는 중…' : '보관 중인 생성 미디어가 없습니다.'"
      >
        <template #[`item.preview`]="{ item }">
          <button type="button" class="thumb" :aria-label="`${item.name} 상세 보기`" @click="detail = item">
            <img v-if="kind(item.mimeType) === 'image'" :src="contentUrl(item)" alt="" loading="lazy" />
            <v-icon v-else :icon="kindIcon[kind(item.mimeType)]" size="28" />
          </button>
        </template>
        <template #[`item.lastModified`]="{ item }">
          <span class="tabular" :title="formatDateTime(item.lastModified)">{{ formatRelative(item.lastModified) }}</span>
          <div class="text-body-small text-medium-emphasis tabular">{{ formatDateTime(item.lastModified) }}</div>
        </template>
        <template #[`item.tool`]="{ item }">
          <v-chip size="small" variant="tonal">{{ toolLabel(item.tool) }}</v-chip>
          <div class="text-body-small text-medium-emphasis mono mt-1">{{ item.model ?? "—" }}</div>
        </template>
        <template #[`item.prompt`]="{ item }">
          <div class="prompt" :title="item.prompt">{{ item.prompt ?? "—" }}</div>
        </template>
        <template #[`item.keyName`]="{ item }">
          <span v-if="item.keyName">{{ item.keyName }}</span>
          <span v-else-if="item.consumer" class="text-medium-emphasis" :title="item.consumer">삭제된 키</span>
          <span v-else class="text-medium-emphasis">—</span>
        </template>
        <template #[`item.size`]="{ item }">
          <span class="tabular">{{ formatBytes(item.size) }}</span>
        </template>
        <template #[`item.actions`]="{ item }">
          <v-btn :icon="mdiInformationOutline" variant="text" size="small" aria-label="상세" @click="detail = item" />
          <v-btn :icon="mdiDownload" variant="text" size="small" aria-label="다운로드" :href="contentUrl(item, true)" />
          <v-btn :icon="mdiDeleteOutline" variant="text" size="small" color="error" aria-label="삭제" @click="askDelete([item.name])" />
        </template>
      </v-data-table>
      <div v-if="nextCursor" class="d-flex justify-center pa-3">
        <v-btn variant="tonal" :loading="loading" @click="load(true)">더 보기</v-btn>
      </div>
    </v-card>

    <!-- 상세 -->
    <v-dialog :model-value="!!detail" max-width="760" @update:model-value="detail = null">
      <v-card v-if="detail" :title="toolLabel(detail.tool) + ' · ' + formatDateTime(detail.lastModified)">
        <v-card-text>
          <div class="preview mb-4">
            <img v-if="kind(detail.mimeType) === 'image'" :src="contentUrl(detail)" :alt="detail.prompt ?? detail.name" />
            <audio v-else-if="kind(detail.mimeType) === 'audio'" :src="contentUrl(detail)" controls preload="metadata" />
            <video v-else-if="kind(detail.mimeType) === 'video'" :src="contentUrl(detail)" controls preload="metadata" />
            <v-icon v-else :icon="mdiFileOutline" size="48" />
          </div>
          <v-table density="compact">
            <tbody>
              <tr><td class="label">URI</td><td class="mono" style="word-break: break-all">gemini://media/{{ detail.name }}</td></tr>
              <tr><td class="label">형식 · 크기</td><td>{{ detail.mimeType }} · {{ formatBytes(detail.size) }}</td></tr>
              <tr><td class="label">도구</td><td class="mono">{{ detail.tool ?? "—" }}</td></tr>
              <tr><td class="label">모델</td><td class="mono">{{ detail.model ?? "—" }}</td></tr>
              <tr>
                <td class="label">API 키</td>
                <td>{{ detail.keyName ?? (detail.consumer ? `삭제된 키 (${detail.consumer})` : "— (게이트웨이를 거치지 않은 요청)") }}</td>
              </tr>
              <tr v-if="detail.interactionId"><td class="label">interaction_id</td><td class="mono">{{ detail.interactionId }}</td></tr>
              <tr><td class="label">프롬프트</td><td style="white-space: pre-wrap">{{ detail.prompt ?? "—" }}</td></tr>
            </tbody>
          </v-table>
        </v-card-text>
        <v-card-actions>
          <v-btn :prepend-icon="mdiDownload" :href="contentUrl(detail, true)">다운로드</v-btn>
          <v-btn color="error" :prepend-icon="mdiDeleteOutline" @click="askDelete([detail.name])">삭제</v-btn>
          <v-spacer />
          <v-btn color="primary" variant="flat" @click="detail = null">닫기</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- 삭제 확인 -->
    <v-dialog v-model="confirm.open" max-width="440">
      <v-card
        :title="confirm.names.length === 1 ? '미디어를 삭제할까요?' : `미디어 ${confirm.names.length}개를 삭제할까요?`"
        text="S3 저장소에서 영구히 지워지며 되돌릴 수 없습니다. 에이전트가 가진 링크(gemini://media/…)도 더 이상 열리지 않습니다."
      >
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="confirm.running" @click="confirm.open = false">취소</v-btn>
          <v-btn color="error" variant="flat" :loading="confirm.running" @click="runDelete">삭제</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar :model-value="!!snackbar" timeout="4000" @update:model-value="snackbar = ''">{{ snackbar }}</v-snackbar>
  </div>
</template>

<style scoped>
.thumb {
  width: 56px;
  height: 56px;
  border-radius: 8px;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(var(--v-theme-on-surface), 0.06);
  cursor: pointer;
}
.thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.prompt {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  max-width: 480px;
}
.preview {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 80px;
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-radius: 8px;
  padding: 8px;
}
.preview img,
.preview video {
  max-width: 100%;
  max-height: 60vh;
  border-radius: 4px;
}
.preview audio {
  width: 100%;
}
.label {
  width: 130px;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
  white-space: nowrap;
}
</style>
