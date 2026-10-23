import vuetify, { transformAssetUrls } from "vite-plugin-vuetify";

// 관리 대시보드. 브라우저는 게이트웨이(/api/admin)와 같은 출처에서 이 앱을 받으므로 SPA 로 충분합니다.
// 서버는 Nitro deno-server 프리셋으로 빌드해 Deno 에서 실행합니다.
export default defineNuxtConfig({
  compatibilityDate: "2026-10-01",
  ssr: false,
  devtools: { enabled: false },
  telemetry: false,

  app: {
    head: {
      title: "Gemini MCP",
      htmlAttrs: { lang: "ko" },
      meta: [{ name: "robots", content: "noindex" }],
      link: [{ rel: "icon", type: "image/svg+xml", href: "/favicon.svg" }],
    },
  },

  css: [
    "@fontsource/roboto/400.css",
    "@fontsource/roboto/500.css",
    "@fontsource/roboto/700.css",
    "vuetify/styles",
    "~/assets/main.css",
  ],
  build: { transpile: ["vuetify"] },
  modules: [
    (_options, nuxt) => {
      nuxt.hooks.hook("vite:extendConfig", (config) => {
        config.plugins?.push(vuetify({ autoImport: true }));
      });
    },
  ],
  vite: {
    vue: { template: { transformAssetUrls } },
  },

  nitro: {
    preset: "deno-server",
    // `deno task dev` 로 대시보드만 띄울 때 관리 API 를 로컬 게이트웨이로 넘깁니다.
    devProxy: {
      "/api/admin": { target: "http://127.0.0.1:8080/api/admin", changeOrigin: false },
    },
  },

  typescript: { strict: true },
});
