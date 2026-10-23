<script setup lang="ts">
import { mdiEye, mdiEyeOff } from "@mdi/js";

definePageMeta({ layout: "blank" });

const api = useApi();
const auth = useAuthState();
const setupRequired = computed(() => auth.value?.setupRequired ?? false);

const code = ref("");
const password = ref("");
const confirm = ref("");
const show = ref(false);
const loading = ref(false);
const error = ref("");

const rules = {
  required: (v: string) => !!v || "필수 항목입니다.",
  min: (v: string) => v.length >= 10 || "10자 이상 입력하세요.",
  same: (v: string) => v === password.value || "비밀번호가 일치하지 않습니다.",
};

async function submit() {
  error.value = "";
  if (setupRequired.value && (password.value.length < 10 || password.value !== confirm.value || !code.value)) return;
  loading.value = true;
  try {
    if (setupRequired.value) {
      await api("/auth/setup", { method: "POST", body: { code: code.value, password: password.value } });
    } else {
      await api("/auth/login", { method: "POST", body: { password: password.value } });
    }
    auth.value = { setupRequired: false, authenticated: true };
    await navigateTo("/");
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    loading.value = false;
  }
}
</script>

<template>
  <v-card class="pa-2" width="420" max-width="calc(100vw - 32px)">
    <v-card-item>
      <template #prepend>
        <img src="/favicon.svg" width="36" height="36" alt="" class="mr-2" />
      </template>
      <v-card-title>Gemini MCP</v-card-title>
      <v-card-subtitle>{{ setupRequired ? "관리자 계정 만들기" : "관리자 로그인" }}</v-card-subtitle>
    </v-card-item>

    <v-card-text>
      <v-alert v-if="setupRequired" type="info" variant="tonal" density="compact" class="mb-4">
        게이트웨이 로그에 출력된 <strong>설정 코드</strong>를 입력하세요.
        <div class="mono mt-1">docker logs gemini-mcp-gateway</div>
      </v-alert>
      <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-4">{{ error }}</v-alert>

      <v-form @submit.prevent="submit">
        <v-text-field
          v-if="setupRequired"
          v-model="code"
          label="설정 코드"
          placeholder="XXXX-XXXX"
          class="mb-2"
          autocomplete="one-time-code"
          :rules="[rules.required]"
          autofocus
        />
        <v-text-field
          v-model="password"
          :label="setupRequired ? '새 비밀번호' : '비밀번호'"
          :type="show ? 'text' : 'password'"
          :append-inner-icon="show ? mdiEyeOff : mdiEye"
          :autocomplete="setupRequired ? 'new-password' : 'current-password'"
          :rules="setupRequired ? [rules.required, rules.min] : [rules.required]"
          :autofocus="!setupRequired"
          class="mb-2"
          @click:append-inner="show = !show"
        />
        <v-text-field
          v-if="setupRequired"
          v-model="confirm"
          label="비밀번호 확인"
          :type="show ? 'text' : 'password'"
          autocomplete="new-password"
          :rules="[rules.required, rules.same]"
          class="mb-2"
        />
        <v-btn type="submit" color="primary" size="large" block :loading="loading">
          {{ setupRequired ? "계정 만들기" : "로그인" }}
        </v-btn>
      </v-form>
    </v-card-text>
  </v-card>
</template>
