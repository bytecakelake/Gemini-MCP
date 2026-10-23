import { FetchError } from "ofetch";

export interface AuthState {
  setupRequired: boolean;
  authenticated: boolean;
}

/** 로그인 상태. 라우트 미들웨어가 처음 한 번 서버에서 읽고, 로그인/로그아웃 시 갱신합니다. */
export const useAuthState = () => useState<AuthState | null>("auth-state", () => null);

/** 서버 오류 응답({ error })을 사람이 읽을 메시지로 바꿉니다. */
export function errorMessage(err: unknown): string {
  if (err instanceof FetchError) return err.data?.data?.error ?? err.data?.error ?? err.data?.message ?? err.message;
  return err instanceof Error ? err.message : String(err);
}

/**
 * 대시보드 서버의 관리 API(/api/admin) 호출. 세션은 HttpOnly 쿠키로 자동 전송되며,
 * 세션이 만료되어 401 이 오면 로그인 화면으로 보냅니다.
 */
export function useApi() {
  const auth = useAuthState();
  return $fetch.create({
    baseURL: "/api/admin",
    credentials: "same-origin",
    async onResponseError({ response, request }) {
      if (response.status === 401 && !String(request).includes("/auth/")) {
        auth.value = { setupRequired: false, authenticated: false };
        await navigateTo("/login");
      }
    },
  });
}
