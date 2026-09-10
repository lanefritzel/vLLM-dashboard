(function () {
  var STORAGE_KEY = 'theme-mode';

  function storedMode() {
    try { return localStorage.getItem(STORAGE_KEY) || 'system'; }
    catch (e) { return 'system'; }
  }

  function resolve(mode) {
    if (mode === 'system') {
      return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    }
    return mode === 'light' ? 'light' : 'dark';
  }

  function applyTheme(mode) {
    if (mode !== 'get') {
      try { localStorage.setItem(STORAGE_KEY, mode); }
      catch (e) {}
    }
    var current = mode === 'get' ? storedMode() : mode;
    document.documentElement.setAttribute('data-theme', resolve(current));
    return current;
  }

  function watchSystemTheme(cb) {
    if (!window.matchMedia) return null;
    var mq = window.matchMedia('(prefers-color-scheme: light)');
    var handler = function () {
      if (storedMode() === 'system') {
        applyTheme('system');
        if (cb) cb();
      }
    };
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else if (mq.addListener) mq.addListener(handler);
    return handler;
  }

  window.applyTheme = applyTheme;
  window.watchSystemTheme = watchSystemTheme;

  applyTheme('get');
  watchSystemTheme();
})();
