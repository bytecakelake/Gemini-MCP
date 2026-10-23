<script setup lang="ts">
defineProps<{
  label: string;
  value: string;
  hint?: string;
  icon?: string;
  /** 상태 색 (error/warning/success). 값 텍스트가 아니라 아이콘에만 씁니다. */
  tone?: string;
}>();
</script>

<template>
  <v-card class="h-100">
    <v-card-text>
      <div class="d-flex align-center justify-space-between text-body-medium text-medium-emphasis">
        <span>{{ label }}</span>
        <v-icon v-if="icon" :icon="icon" size="20" :color="tone" />
      </div>
      <div class="text-headline-small font-weight-medium tabular mt-2">{{ value }}</div>
      <div v-if="hint" class="text-body-small text-medium-emphasis mt-1">{{ hint }}</div>
    </v-card-text>
  </v-card>
</template>
