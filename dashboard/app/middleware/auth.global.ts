import type { AuthState } from "~/composables/useApi";

export default defineNuxtRouteMiddleware(async (to) => {
  const auth = useAuthState();
  if (!auth.value) {
    try {
      auth.value = await $fetch<AuthState>("/api/admin/auth/state");
    } catch {
      auth.value = { setupRequired: false, authenticated: false };
    }
  }
  if (!auth.value.authenticated && to.path !== "/login") return navigateTo("/login");
  if (auth.value.authenticated && to.path === "/login") return navigateTo("/");
});
