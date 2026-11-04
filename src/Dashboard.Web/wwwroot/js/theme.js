// vLLM Dashboard theme (runs in <head> before first paint — no flash).
// Dark default; light is first-class. Persists the choice.
(function () {
  var STORAGE_KEY = "theme-mode";

  function storedMode() {
    try {
      var m = localStorage.getItem(STORAGE_KEY);
      if (m === "light" || m === "dark" || m === "system") return m;
    } catch (e) { /* storage unavailable */ }
    return "dark";
  }

  function resolve(mode) {
    if (mode === "light" || mode === "dark") return mode;
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: light)").matches
      ? "light" : "dark";
  }

  function updateThemeColor(theme) {
    var meta = document.querySelector('meta[name="theme-color"]');
    if (!meta) return;
    var root = document.documentElement;
    var computed =
      (window.getComputedStyle && getComputedStyle(root).getPropertyValue("--ui-bg")) || "";
    var bg = (computed || "").trim();
    if (!bg) bg = theme === "light" ? "#f4f6fb" : "#0a0e16";
    meta.setAttribute("content", bg);
  }

  function applyTheme(mode) {
    if (mode === "get") mode = storedMode();
    if (mode !== "light" && mode !== "dark") mode = resolve(mode);
    var theme = resolve(mode);
    var root = document.documentElement;
    if (theme === "dark") {
      root.setAttribute("data-theme", "dark");
    } else {
      root.setAttribute("data-theme", "light");
    }
    try {
      localStorage.setItem(STORAGE_KEY, mode === "light" || mode === "dark" ? mode : "system");
    } catch (e) { /* ignore */ }
    updateThemeColor(theme);
    window.dispatchEvent(new CustomEvent("themechange", { detail: { mode: mode, theme: theme } }));
    return theme;
  }

  function watchSystemTheme() {
    if (!window.matchMedia) return;
    var mq = window.matchMedia("(prefers-color-scheme: light)");
    var handler = function () {
      if (storedMode() === "system") applyTheme("system");
    };
    if (mq.addEventListener) mq.addEventListener("change", handler);
    else if (mq.addListener) mq.addListener(handler);
  }

  window.applyTheme = applyTheme;
  window.watchSystemTheme = watchSystemTheme;

  applyTheme("get");
  watchSystemTheme();
})();
