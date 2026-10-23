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
    // 압축은 앞단 Caddy 한 곳에서만 합니다. Deno.serve 는 Accept-Encoding 에 따라 응답을 자동으로
    // gzip/brotli 압축하는데, 이미 Content-Encoding 이 있으면 건너뜁니다. 그래서 모든 응답에 identity 를 붙입니다
    // (Caddy 는 이 헤더를 지우고 zstd/gzip 으로 한 번만 압축합니다. 지우지 않으면 이중 압축으로 글자가 깨집니다).
    compressPublicAssets: false,
    routeRules: {
      "/**": { headers: { "content-encoding": "identity" } },
    },
  },

  typescript: { strict: true },
});
