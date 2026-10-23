<script setup lang="ts">
import {
  mdiChartLine,
  mdiCogOutline,
  mdiImageMultipleOutline,
  mdiKeyVariant,
  mdiLogout,
  mdiMenu,
  mdiTextBoxSearchOutline,
  mdiThemeLightDark,
} from "@mdi/js";
import { useDisplay, useTheme } from "vuetify";

const theme = useTheme();
const { mobile } = useDisplay();
const drawer = ref<boolean | null>(null);
const api = useApi();
const auth = useAuthState();

const items = [
  { title: "모니터링", icon: mdiChartLine, to: "/" },
  { title: "로그", icon: mdiTextBoxSearchOutline, to: "/logs" },
  { title: "API 키", icon: mdiKeyVariant, to: "/keys" },
  { title: "생성 미디어", icon: mdiImageMultipleOutline, to: "/media" },
  { title: "설정", icon: mdiCogOutline, to: "/settings" },
];

function toggleTheme() {
  const next = theme.current.value.dark ? "light" : "dark";
  theme.change(next);
  try {
    localStorage.setItem("gmcp-theme", next);
  } catch {
    // 저장소를 쓸 수 없으면 이번 세션에만 적용합니다.
  }
}

onMounted(() => {
  try {
    const saved = localStorage.getItem("gmcp-theme");
    if (saved === "light" || saved === "dark") theme.change(saved);
  } catch {
    // 무시
  }
});

async function logout() {
  await api("/auth/logout", { method: "POST" }).catch(() => {});
  auth.value = { setupRequired: false, authenticated: false };
  await navigateTo("/login");
}
</script>

<template>
  <v-app>
    <v-navigation-drawer v-model="drawer" :permanent="!mobile" width="240">
      <div class="d-flex align-center ga-2 px-4 py-4">
        <img src="/favicon.svg" width="28" height="28" alt="" />
        <div>
          <div class="text-title-medium font-weight-medium">Gemini MCP</div>
          <div class="text-body-small text-medium-emphasis">Gateway Dashboard</div>
        </div>
      </div>
      <v-divider />
      <v-list nav density="comfortable" color="primary">
        <v-list-item
          v-for="item in items"
          :key="item.to"
          :to="item.to"
          :prepend-icon="item.icon"
          :title="item.title"
          :exact="item.to === '/'"
          rounded="lg"
        />
      </v-list>
      <template #append>
        <div class="pa-3">
          <v-btn block variant="tonal" :prepend-icon="mdiLogout" @click="logout">로그아웃</v-btn>
        </div>
      </template>
    </v-navigation-drawer>

    <v-app-bar flat border="b" density="comfortable">
      <v-app-bar-nav-icon v-if="mobile" :icon="mdiMenu" @click="drawer = !drawer" />
      <v-spacer />
      <v-tooltip text="라이트/다크 전환" location="bottom">
        <template #activator="{ props }">
          <v-btn v-bind="props" icon variant="text" aria-label="라이트/다크 전환" @click="toggleTheme">
            <v-icon :icon="mdiThemeLightDark" />
          </v-btn>
        </template>
      </v-tooltip>
    </v-app-bar>

    <v-main>
      <v-container fluid class="pa-4 pa-md-6" style="max-width: 1440px">
        <slot />
      </v-container>
    </v-main>
  </v-app>
</template>
