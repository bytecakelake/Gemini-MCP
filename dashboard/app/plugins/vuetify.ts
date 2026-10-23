import { createVuetify } from "vuetify";
import { aliases, mdi } from "vuetify/iconsets/mdi-svg";
import { ko } from "vuetify/locale";

// Material Design (Vuetify). 차트 색은 dataviz 기본 팔레트의 범주형 1·2번(파랑·주황)을
// 라이트/다크 각각에 맞춘 단계로 테마에 넣어 둡니다 (상태 색 error/warning 과 겹치지 않음).
export default defineNuxtPlugin((nuxtApp) => {
  const vuetify = createVuetify({
    ssr: false,
    locale: { locale: "ko", messages: { ko } },
    icons: { defaultSet: "mdi", aliases, sets: { mdi } },
    theme: {
      defaultTheme: "system",
      themes: {
        light: {
          dark: false,
          colors: {
            primary: "#1a73e8",
            secondary: "#5f6368",
            background: "#f6f8fc",
            surface: "#ffffff",
            "series-1": "#2a78d6",
            "series-2": "#eb6834",
          },
        },
        dark: {
          dark: true,
          colors: {
            primary: "#8ab4f8",
            secondary: "#bdc1c6",
            background: "#121316",
            surface: "#1e1f23",
            "series-1": "#3987e5",
            "series-2": "#d95926",
          },
        },
      },
    },
    defaults: {
      VCard: { rounded: "lg", variant: "flat", border: true },
      VTextField: { variant: "outlined", density: "comfortable" },
      VSelect: { variant: "outlined", density: "comfortable" },
      VAutocomplete: { variant: "outlined", density: "comfortable" },
      VBtn: { rounded: "lg" },
    },
  });
  nuxtApp.vueApp.use(vuetify);
});
