<script setup lang="ts">
/**
 * 단일 Y축 시계열 선 차트 (SVG). 십자선 + 툴팁 호버, 2개 이상 시리즈면 범례를 표시합니다.
 * 색은 Vuetify 테마 변수(--v-theme-<color>)를 써서 라이트/다크 각각의 단계를 따릅니다.
 */
export interface ChartSeries {
  key: string;
  label: string;
  /** Vuetify 테마 색 이름 (예: series-1) */
  color: string;
}

const props = withDefaults(
  defineProps<{
    points: { t: number }[];
    series: ChartSeries[];
    step: number;
    height?: number;
    format?: (v: number) => string;
    summary: string;
  }>(),
  { height: 220, format: (v: number) => String(v) },
);

const PAD = { top: 12, right: 12, bottom: 26, left: 44 };
const value = (point: { t: number }, key: string) => Number((point as Record<string, unknown>)[key] ?? 0);
const root = ref<HTMLElement>();
const width = ref(600);
const hover = ref<number | null>(null);

let observer: ResizeObserver | undefined;
onMounted(() => {
  observer = new ResizeObserver(([entry]) => (width.value = Math.max(240, entry!.contentRect.width)));
  if (root.value) observer.observe(root.value);
});
onBeforeUnmount(() => observer?.disconnect());

function niceMax(max: number): number {
  if (max <= 0) return 4;
  const exp = Math.pow(10, Math.floor(Math.log10(max)));
  const n = max / exp;
  const nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
  return nice * exp;
}

const innerW = computed(() => width.value - PAD.left - PAD.right);
const innerH = computed(() => props.height - PAD.top - PAD.bottom);
const yMax = computed(() => niceMax(Math.max(0, ...props.points.flatMap((p) => props.series.map((s) => value(p, s.key))))));
const yTicks = computed(() => [0, 0.2, 0.4, 0.6, 0.8, 1].map((f) => f * yMax.value));

const x = (i: number) => PAD.left + (props.points.length <= 1 ? 0 : (i / (props.points.length - 1)) * innerW.value);
const y = (v: number) => PAD.top + innerH.value - (v / yMax.value) * innerH.value;

const paths = computed(() =>
  props.series.map((s) => ({
    ...s,
    d: props.points.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(value(p, s.key)).toFixed(1)}`).join(""),
  }))
);

const timeFormat = computed(() =>
  new Intl.DateTimeFormat("ko-KR", props.step >= 3_600_000
    ? { month: "numeric", day: "numeric", hour: "2-digit", hour12: false }
    : { hour: "2-digit", minute: "2-digit", hour12: false })
);

const xTicks = computed(() => {
  const n = props.points.length;
  if (n === 0) return [];
  const count = Math.min(6, Math.max(2, Math.floor(innerW.value / 110)));
  return Array.from({ length: count }, (_, k) => Math.round((k / (count - 1)) * (n - 1)));
});

function onMove(event: PointerEvent) {
  const rect = (event.currentTarget as SVGElement).getBoundingClientRect();
  const px = event.clientX - rect.left - PAD.left;
  const n = props.points.length;
  if (n === 0) return;
  hover.value = Math.min(n - 1, Math.max(0, Math.round((px / innerW.value) * (n - 1))));
}

const tooltip = computed(() => {
  if (hover.value === null) return null;
  const p = props.points[hover.value];
  if (!p) return null;
  const left = x(hover.value);
  return {
    left: Math.min(left + 12, width.value - 170),
    time: new Intl.DateTimeFormat("ko-KR", {
      month: "numeric",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      hour12: false,
    }).format(p.t),
    rows: props.series.map((s) => ({ ...s, value: props.format(value(p, s.key)) })),
  };
});
</script>

<template>
  <div ref="root" class="chart">
    <div v-if="series.length > 1" class="legend text-body-small text-medium-emphasis">
      <span v-for="s in series" :key="s.key" class="legend-item">
        <span class="swatch" :style="{ background: `rgb(var(--v-theme-${s.color}))` }" />{{ s.label }}
      </span>
    </div>
    <svg
      :width="width"
      :height="height"
      role="img"
      :aria-label="summary"
      @pointermove="onMove"
      @pointerleave="hover = null"
    >
      <g class="grid">
        <line
          v-for="tick in yTicks"
          :key="tick"
          :x1="PAD.left"
          :x2="width - PAD.right"
          :y1="y(tick)"
          :y2="y(tick)"
        />
      </g>
      <g class="axis">
        <text v-for="tick in yTicks" :key="tick" :x="PAD.left - 8" :y="y(tick) + 4" text-anchor="end">
          {{ format(tick) }}
        </text>
        <text
          v-for="i in xTicks"
          :key="i"
          :x="x(i)"
          :y="height - 6"
          :text-anchor="i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'"
        >
          {{ points[i] ? timeFormat.format(points[i]!.t) : "" }}
        </text>
      </g>
      <path
        v-for="p in paths"
        :key="p.key"
        :d="p.d"
        fill="none"
        :stroke="`rgb(var(--v-theme-${p.color}))`"
        stroke-width="2"
        stroke-linejoin="round"
        stroke-linecap="round"
      />
      <g v-if="hover !== null && points[hover]">
        <line class="crosshair" :x1="x(hover)" :x2="x(hover)" :y1="PAD.top" :y2="PAD.top + innerH" />
        <circle
          v-for="s in series"
          :key="s.key"
          :cx="x(hover)"
          :cy="y(value(points[hover]!, s.key))"
          r="4"
          :fill="`rgb(var(--v-theme-${s.color}))`"
          stroke="rgb(var(--v-theme-surface))"
          stroke-width="2"
        />
      </g>
    </svg>
    <div v-if="tooltip" class="tooltip text-body-small" :style="{ left: `${tooltip.left}px` }">
      <div class="text-medium-emphasis mb-1">{{ tooltip.time }}</div>
      <div v-for="row in tooltip.rows" :key="row.key" class="d-flex align-center ga-2">
        <span class="swatch" :style="{ background: `rgb(var(--v-theme-${row.color}))` }" />
        <span class="flex-grow-1">{{ row.label }}</span>
        <strong class="tabular">{{ row.value }}</strong>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chart {
  position: relative;
  width: 100%;
}
svg {
  display: block;
  touch-action: pan-y;
}
.grid line {
  stroke: rgba(var(--v-border-color), 0.12);
}
.axis text {
  font-size: 11px;
  fill: rgba(var(--v-theme-on-surface), 0.6);
  font-variant-numeric: tabular-nums;
}
.crosshair {
  stroke: rgba(var(--v-theme-on-surface), 0.35);
  stroke-dasharray: 3 3;
}
.legend {
  display: flex;
  gap: 16px;
  margin-bottom: 4px;
}
.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}
.tooltip {
  position: absolute;
  top: 24px;
  min-width: 150px;
  padding: 8px 10px;
  border-radius: 8px;
  background: rgb(var(--v-theme-surface));
  border: 1px solid rgba(var(--v-border-color), 0.2);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.15);
  pointer-events: none;
}
</style>
