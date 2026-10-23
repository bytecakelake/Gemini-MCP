import vuetify, { transformAssetUrls } from "vite-plugin-vuetify";

// 관리 대시보드. 화면은 SPA 이고, 서버(server/, Nitro deno-server 프리셋, Deno)가 관리 API(/api/admin)로
// 관리자 인증, APISIX Admin API(라우트·API 키), Loki(로그·지표) 조회를 맡습니다.
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
  },

  typescript: { strict: true },
});
