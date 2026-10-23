<script setup lang="ts">
import {
  mdiCheck,
  mdiContentCopy,
  mdiDeleteOutline,
  mdiDotsVertical,
  mdiKeyChange,
  mdiPencilOutline,
  mdiPlus,
} from "@mdi/js";
import type { ApiKey, IssuedKey, ToolInfo } from "~/types/api";

const api = useApi();
const keys = ref<ApiKey[]>([]);
const tools = ref<ToolInfo[]>([]);
const loading = ref(false);
const error = ref("");
const snackbar = ref("");

const headers = [
  { title: "이름", key: "name" },
  { title: "키", key: "prefix", sortable: false },
  { title: "상태", key: "status", width: 110 },
  { title: "허용 도구", key: "allowedTools", sortable: false },
  { title: "요청 수", key: "requestCount", align: "end" as const },
  { title: "마지막 사용", key: "lastUsedAt" },
  { title: "만료", key: "expiresAt" },
  { title: "", key: "actions", sortable: false, width: 120, align: "end" as const },
];

const statusMeta = {
  active: { color: "success", text: "활성" },
  disabled: { color: "grey", text: "비활성" },
  expired: { color: "warning", text: "만료" },
};

const expiryOptions = [
  { title: "만료 없음", value: 0 },
  { title: "7일", value: 7 },
  { title: "30일", value: 30 },
  { title: "90일", value: 90 },
  { title: "1년", value: 365 },
];

async function load() {
  loading.value = true;
  try {
    keys.value = (await api<{ items: ApiKey[] }>("/keys")).items;
    error.value = "";
  } catch (err) {
    error.value = errorMessage(err);
  } finally {
    loading.value = false;
  }
}

onMounted(async () => {
  await load();
  try {
    tools.value = (await api<{ items: ToolInfo[] }>("/tools")).items;
  } catch {
    // MCP 서버가 꺼져 있으면 도구 이름을 직접 입력합니다.
  }
});

// ---------- 발급 / 수정 ----------
const editor = reactive({
  open: false,
  id: "" as string,
  name: "",
  restrict: false,
  allowedTools: [] as string[],
  expiresInDays: 0,
  /** 수정 시 만료일을 바꾸지 않음 */
  keepExpiry: true,
  saving: false,
  error: "",
});

function openCreate() {
  Object.assign(editor, {
    open: true,
    id: "",
    name: "",
    restrict: false,
    allowedTools: [],
    expiresInDays: 90,
    keepExpiry: false,
    error: "",
  });
}

function openEdit(key: ApiKey) {
  Object.assign(editor, {
    open: true,
    id: key.id,
    name: key.name,
    restrict: key.allowedTools !== null,
    allowedTools: key.allowedTools ?? [],
    expiresInDays: 0,
    keepExpiry: true,
    error: "",
  });
}

const issued = ref<IssuedKey | null>(null);
const copied = ref(false);

async function save() {
  if (!editor.name.trim()) {
    editor.error = "이름을 입력하세요.";
    return;
  }
  if (editor.restrict && editor.allowedTools.length === 0) {
    editor.error = "허용할 도구를 하나 이상 고르세요.";
    return;
  }
  editor.saving = true;
  const body: Record<string, unknown> = {
    name: editor.name,
    allowedTools: editor.restrict ? editor.allowedTools : null,
  };
  if (!editor.id || !editor.keepExpiry) body.expiresInDays = editor.expiresInDays || null;
  try {
    if (editor.id) {
      await api(`/keys/${editor.id}`, { method: "PATCH", body });
      snackbar.value = "키를 수정했습니다.";
    } else {
      issued.value = await api<IssuedKey>("/keys", { method: "POST", body });
      copied.value = false;
    }
    editor.open = false;
    await load();
  } catch (err) {
    editor.error = errorMessage(err);
  } finally {
    editor.saving = false;
  }
}

async function toggle(key: ApiKey) {
  try {
    await api(`/keys/${key.id}`, { method: "PATCH", body: { disabled: !key.disabled } });
    await load();
  } catch (err) {
    error.value = errorMessage(err);
  }
}

// ---------- 재발급 / 삭제 ----------
const confirm = reactive({ open: false, action: "" as "rotate" | "delete" | "", key: null as ApiKey | null });

function ask(action: "rotate" | "delete", key: ApiKey) {
  Object.assign(confirm, { open: true, action, key });
}

async function runConfirmed() {
  const key = confirm.key!;
  confirm.open = false;
  try {
    if (confirm.action === "rotate") {
      issued.value = await api<IssuedKey>(`/keys/${key.id}/rotate`, { method: "POST" });
      copied.value = false;
    } else {
      await api(`/keys/${key.id}`, { method: "DELETE" });
      snackbar.value = `'${key.name}' 키를 삭제했습니다.`;
    }
    await load();
  } catch (err) {
    error.value = errorMessage(err);
  }
}

async function copySecret() {
  if (!issued.value) return;
  await navigator.clipboard.writeText(issued.value.secret);
  copied.value = true;
}

const endpoint = computed(() => (import.meta.client ? `${location.origin}/mcp` : "/mcp"));
const addCommand = computed(() =>
  issued.value
    ? `claude mcp add --transport http gemini ${endpoint.value} --header "Authorization: Bearer ${issued.value.secret}"`
    : ""
);
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-center ga-3 mb-4">
      <h1 class="page-title">API 키</h1>
      <v-spacer />
      <v-btn color="primary" :prepend-icon="mdiPlus" @click="openCreate">새 키 발급</v-btn>
    </div>

    <v-alert type="info" variant="tonal" density="compact" class="mb-4">
      MCP 클라이언트는 <span class="mono">Authorization: Bearer &lt;API 키&gt;</span> 헤더로
      <span class="mono">{{ endpoint }}</span> 에 접속합니다. 키 원문은 발급 직후 한 번만 보여 주며 서버에는 해시만 저장됩니다.
    </v-alert>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

    <v-card>
      <v-data-table
        :headers="headers"
        :items="keys"
        :loading="loading"
        item-value="id"
        :items-per-page="25"
        no-data-text="발급된 키가 없습니다. '새 키 발급'으로 만드세요."
      >
        <template #[`item.name`]="{ item }">
          <span class="font-weight-medium">{{ item.name }}</span>
          <div class="text-body-small text-medium-emphasis">{{ formatDateTime(item.createdAt) }} 발급</div>
        </template>
        <template #[`item.prefix`]="{ item }">
          <span class="mono">{{ item.prefix }}…</span>
        </template>
        <template #[`item.status`]="{ item }">
          <v-chip :color="statusMeta[item.status].color" size="small" variant="tonal">
            {{ statusMeta[item.status].text }}
          </v-chip>
        </template>
        <template #[`item.allowedTools`]="{ item }">
          <span v-if="!item.allowedTools" class="text-medium-emphasis">모든 도구</span>
          <div v-else class="d-flex flex-wrap ga-1 py-1">
            <v-chip v-for="t in item.allowedTools" :key="t" size="x-small" class="mono">{{ t }}</v-chip>
          </div>
        </template>
        <template #[`item.requestCount`]="{ item }">
          <span class="tabular">{{ formatNumber(item.requestCount) }}</span>
        </template>
        <template #[`item.lastUsedAt`]="{ item }">
          <span :title="item.lastUsedAt ? formatDateTime(item.lastUsedAt) : ''">{{ formatRelative(item.lastUsedAt) }}</span>
        </template>
        <template #[`item.expiresAt`]="{ item }">
          <span v-if="!item.expiresAt" class="text-medium-emphasis">없음</span>
          <span v-else :title="formatDateTime(item.expiresAt)">{{ formatRelative(item.expiresAt) }}</span>
        </template>
        <template #[`item.actions`]="{ item }">
          <v-switch
            :model-value="!item.disabled"
            color="primary"
            density="compact"
            hide-details
            inset
            class="d-inline-flex"
            :aria-label="item.disabled ? '활성화' : '비활성화'"
            @update:model-value="toggle(item)"
          />
          <v-menu>
            <template #activator="{ props }">
              <v-btn v-bind="props" :icon="mdiDotsVertical" variant="text" size="small" aria-label="키 메뉴" />
            </template>
            <v-list density="compact">
              <v-list-item :prepend-icon="mdiPencilOutline" title="수정" @click="openEdit(item)" />
              <v-list-item :prepend-icon="mdiKeyChange" title="재발급" @click="ask('rotate', item)" />
              <v-list-item :prepend-icon="mdiDeleteOutline" title="삭제" base-color="error" @click="ask('delete', item)" />
            </v-list>
          </v-menu>
        </template>
      </v-data-table>
    </v-card>

    <!-- 발급 / 수정 -->
    <v-dialog v-model="editor.open" max-width="560">
      <v-card :title="editor.id ? 'API 키 수정' : '새 API 키 발급'">
        <v-card-text>
          <v-alert v-if="editor.error" type="error" variant="tonal" density="compact" class="mb-4">{{ editor.error }}</v-alert>
          <v-text-field v-model="editor.name" label="이름" placeholder="예: 내 노트북 Claude Code" maxlength="80" counter autofocus />

          <v-switch v-model="editor.restrict" color="primary" label="사용할 수 있는 도구 제한" hide-details class="mb-2" />
          <v-combobox
            v-if="editor.restrict"
            v-model="editor.allowedTools"
            :items="tools.map((t) => t.name)"
            label="허용 도구"
            multiple
            chips
            closable-chips
            hint="목록에 없는 이름도 입력할 수 있습니다. 제한하면 다른 도구의 tools/call 은 403 으로 거부됩니다."
            persistent-hint
            class="mb-4"
          />

          <v-checkbox v-if="editor.id" v-model="editor.keepExpiry" label="만료일 유지" hide-details density="compact" />
          <v-select
            v-if="!editor.id || !editor.keepExpiry"
            v-model="editor.expiresInDays"
            :items="expiryOptions"
            label="만료"
            :hint="editor.id ? '지금부터 계산합니다.' : ''"
            persistent-hint
          />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="editor.open = false">취소</v-btn>
          <v-btn color="primary" variant="flat" :loading="editor.saving" @click="save">
            {{ editor.id ? "저장" : "발급" }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- 발급된 키 (한 번만 표시) -->
    <v-dialog :model-value="!!issued" max-width="640" persistent>
      <v-card v-if="issued" title="API 키가 발급되었습니다">
        <v-card-text>
          <v-alert type="warning" variant="tonal" density="compact" class="mb-4">
            이 창을 닫으면 키를 다시 볼 수 없습니다. 지금 복사해 안전한 곳에 보관하세요.
          </v-alert>
          <v-text-field
            :model-value="issued.secret"
            label="API 키"
            readonly
            class="mono"
            :append-inner-icon="copied ? mdiCheck : mdiContentCopy"
            @click:append-inner="copySecret"
          />
          <div class="text-body-medium mb-1">Claude Code 에 등록</div>
          <v-sheet border rounded="lg" class="pa-3 mono" style="word-break: break-all">{{ addCommand }}</v-sheet>
        </v-card-text>
        <v-card-actions>
          <v-btn :prepend-icon="copied ? mdiCheck : mdiContentCopy" @click="copySecret">{{ copied ? "복사됨" : "키 복사" }}</v-btn>
          <v-spacer />
          <v-btn color="primary" variant="flat" @click="issued = null">닫기</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- 재발급 / 삭제 확인 -->
    <v-dialog v-model="confirm.open" max-width="440">
      <v-card
        :title="confirm.action === 'rotate' ? '키를 재발급할까요?' : '키를 삭제할까요?'"
        :text="
          confirm.action === 'rotate'
            ? `'${confirm.key?.name}' 의 기존 키는 즉시 무효화되고 새 키가 발급됩니다. 이름·권한·통계는 유지됩니다.`
            : `'${confirm.key?.name}' 키로는 더 이상 접속할 수 없으며 되돌릴 수 없습니다.`
        "
      >
        <v-card-actions>
          <v-spacer />
          <v-btn @click="confirm.open = false">취소</v-btn>
          <v-btn :color="confirm.action === 'delete' ? 'error' : 'primary'" variant="flat" @click="runConfirmed">
            {{ confirm.action === "rotate" ? "재발급" : "삭제" }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar :model-value="!!snackbar" timeout="3000" @update:model-value="snackbar = ''">{{ snackbar }}</v-snackbar>
  </div>
</template>
