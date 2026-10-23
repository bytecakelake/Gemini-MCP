<script setup lang="ts">
import {
  mdiAlertCircleOutline,
  mdiCheckCircle,
  mdiCloseCircle,
  mdiHelpCircleOutline,
  mdiKeyVariant,
  mdiRefresh,
  mdiSwapVertical,
  mdiTimerOutline,
  mdiTransfer,
} from "@mdi/js";
import type { Metrics, RangeName, Status } from "~/types/api";

const api = useApi();
const range = ref<RangeName>("24h");
const metrics = ref<Metrics>();
const status = ref<Status>();
const error = ref("");
const loading = ref(false);
const updatedAt = ref<number>();

async function refresh() {
  loading.value = true;
  try {
    [metrics.value, status.value] = await Promise.all([
      api<Metrics>("/metrics", { query: { range: range.value } }),
      api<Status>("/status"),
    ]);
    error.value = "";
    updatedAt.value = Date.now();
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    loading.value = false;
  }
}

watch(range, refresh);
let timer: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  refresh();
  timer = setInterval(refresh, 15_000);
});
onBeforeUnmount(() => clearInterval(timer));

const totals = computed(() => metrics.value?.totals);
const rangeLabel = computed(() => ({ "1h": "최근 1시간", "24h": "최근 24시간", "7d": "최근 7일" })[range.value]);

function healthIcon(healthy: boolean | null) {
  if (healthy === null) return { icon: mdiHelpCircleOutline, color: "grey", text: "확인 중" };
  return healthy
    ? { icon: mdiCheckCircle, color: "success", text: "정상" }
    : { icon: mdiCloseCircle, color: "error", text: "응답 없음" };
}

const maxTool = computed(() => Math.max(1, ...(metrics.value?.tools.map((t) => t.count) ?? [])));
const maxKey = computed(() => Math.max(1, ...(metrics.value?.keys.map((k) => k.count) ?? [])));
const maxHist = computed(() => Math.max(1, ...(metrics.value?.latencyHistogram.map((h) => h.count) ?? [])));

function histLabel(le: number | null, index: number) {
  const prev = metrics.value?.latencyHistogram[index - 1]?.le;
  if (le === null) return `> ${formatMs(prev ?? 0)}`;
  return `≤ ${formatMs(le)}`;
}
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-center ga-3 mb-4">
      <h1 class="page-title">모니터링</h1>
      <v-spacer />
      <span v-if="updatedAt" class="text-body-small text-medium-emphasis">
        {{ formatDateTime(updatedAt) }} 갱신 · 15초마다 자동
      </span>
      <v-btn-toggle v-model="range" mandatory density="comfortable" variant="outlined" divided color="primary">
        <v-btn value="1h">1시간</v-btn>
        <v-btn value="24h">24시간</v-btn>
        <v-btn value="7d">7일</v-btn>
      </v-btn-toggle>
      <v-btn :icon="mdiRefresh" variant="text" :loading="loading" aria-label="새로고침" @click="refresh" />
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

    <!-- 서비스 상태 -->
    <v-row v-if="status" dense class="mb-2">
      <v-col v-for="u in status.upstreams" :key="u.name" cols="12" sm="6" lg="3">
        <v-card class="h-100">
          <v-card-text>
            <div class="d-flex align-center justify-space-between text-body-medium text-medium-emphasis">
              <span>{{ u.name }}</span>
              <v-chip
                :prepend-icon="healthIcon(u.healthy).icon"
                :color="healthIcon(u.healthy).color"
                size="small"
                variant="tonal"
              >
                {{ healthIcon(u.healthy).text }}
              </v-chip>
            </div>
            <div class="text-title-large tabular mt-2">{{ u.latencyMs === null ? "—" : formatMs(u.latencyMs) }}</div>
            <div class="text-body-small text-medium-emphasis text-truncate" :title="u.url">{{ u.url }}</div>
            <div class="health-strip mt-2" :aria-label="`${u.name} 최근 상태`">
              <v-tooltip
                v-for="h in u.history.slice(-60)"
                :key="h.t"
                location="top"
                :text="`${formatDateTime(h.t)} · ${h.ok ? '정상' : '실패'} · ${formatMs(h.ms)}`"
              >
                <template #activator="{ props }">
                  <span v-bind="props" class="tick" :class="h.ok ? 'bg-success' : 'bg-error'" />
                </template>
              </v-tooltip>
            </div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12" sm="6" lg="3">
        <StatTile
          label="API 키"
          :value="`${status.keys.active} 활성`"
          :hint="`전체 ${status.keys.total} · 비활성 ${status.keys.disabled} · 만료 ${status.keys.expired}`"
          :icon="mdiKeyVariant"
        />
      </v-col>
    </v-row>

    <!-- 요청 지표 -->
    <v-row v-if="totals" dense class="mb-2">
      <v-col cols="6" md="3">
        <StatTile label="요청" :value="formatNumber(totals.requests)" :hint="rangeLabel" :icon="mdiSwapVertical" />
      </v-col>
      <v-col cols="6" md="3">
        <StatTile
          label="오류율"
          :value="formatPercent(totals.errorRate)"
          :hint="`오류 응답(4xx·5xx) ${formatNumber(totals.errors)}`"
          :icon="mdiAlertCircleOutline"
          :tone="totals.errorRate > 0.05 ? 'error' : undefined"
        />
      </v-col>
      <v-col cols="6" md="3">
        <StatTile
          label="지연 시간 p95"
          :value="formatMs(totals.p95Ms)"
          :hint="`p50 ${formatMs(totals.p50Ms)} · p99 ${formatMs(totals.p99Ms)}`"
          :icon="mdiTimerOutline"
        />
      </v-col>
      <v-col cols="6" md="3">
        <StatTile
          label="응답 데이터"
          :value="formatBytes(totals.bytesOut)"
          :hint="`평균 지연 ${formatMs(totals.avgMs)}`"
          :icon="mdiTransfer"
        />
      </v-col>
    </v-row>

    <v-row v-if="metrics" dense>
      <v-col cols="12" lg="6">
        <v-card>
          <v-card-item title="요청 수" :subtitle="`${rangeLabel} · ${formatStep(metrics.step)} 단위`" />
          <v-card-text>
            <TimeSeriesChart
              :points="metrics.series"
              :series="[
                { key: 'requests', label: '요청', color: 'series-1' },
                { key: 'errors', label: '오류', color: 'series-2' },
              ]"
              :step="metrics.step"
              :format="formatCompact"
              summary="시간대별 요청 수와 오류 수"
            />
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12" lg="6">
        <v-card>
          <v-card-item title="지연 시간 p95" :subtitle="`${rangeLabel} · 구간별 95번째 백분위수`" />
          <v-card-text>
            <TimeSeriesChart
              :points="metrics.series"
              :series="[{ key: 'p95Ms', label: 'p95', color: 'series-1' }]"
              :step="metrics.step"
              :format="formatMs"
              summary="시간대별 지연 시간 p95"
            />
          </v-card-text>
        </v-card>
      </v-col>

      <v-col cols="12" md="6" lg="4">
        <v-card class="h-100">
          <v-card-item title="도구별 호출" :subtitle="rangeLabel" />
          <v-card-text>
            <div v-if="!metrics.tools.length" class="text-medium-emphasis text-body-medium">호출 기록이 없습니다.</div>
            <div v-for="t in metrics.tools" :key="t.name" class="mb-3">
              <div class="d-flex text-body-medium mb-1">
                <span class="mono">{{ t.name }}</span>
                <v-spacer />
                <span class="tabular">{{ formatNumber(t.count) }}</span>
                <span v-if="t.errors" class="tabular text-medium-emphasis ml-2">(오류 {{ t.errors }})</span>
              </div>
              <v-progress-linear :model-value="(t.count / maxTool) * 100" color="series-1" height="6" rounded />
            </div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12" md="6" lg="4">
        <v-card class="h-100">
          <v-card-item title="API 키별 요청" :subtitle="rangeLabel" />
          <v-card-text>
            <div v-if="!metrics.keys.length" class="text-medium-emphasis text-body-medium">요청 기록이 없습니다.</div>
            <div v-for="k in metrics.keys" :key="k.id" class="mb-3">
              <div class="d-flex text-body-medium mb-1">
                <span>{{ k.name }}</span>
                <v-spacer />
                <span class="tabular">{{ formatNumber(k.count) }}</span>
              </div>
              <v-progress-linear :model-value="(k.count / maxKey) * 100" color="series-1" height="6" rounded />
            </div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12" lg="4">
        <v-card class="h-100">
          <v-card-item title="지연 시간 분포" :subtitle="rangeLabel" />
          <v-card-text>
            <div v-for="(h, i) in metrics.latencyHistogram" :key="i" class="d-flex align-center ga-2 mb-1">
              <span class="text-body-small tabular text-medium-emphasis" style="width: 72px">{{ histLabel(h.le, i) }}</span>
              <v-progress-linear
                :model-value="(h.count / maxHist) * 100"
                color="series-1"
                height="10"
                rounded
                class="flex-grow-1"
              />
              <span class="text-body-small tabular" style="width: 48px; text-align: right">{{ formatNumber(h.count) }}</span>
            </div>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <v-skeleton-loader v-if="!metrics && !error" type="card, card" />
  </div>
</template>

<style scoped>
.health-strip {
  display: flex;
  gap: 2px;
  height: 14px;
}
.tick {
  flex: 1 1 0;
  max-width: 6px;
  border-radius: 2px;
}
</style>
