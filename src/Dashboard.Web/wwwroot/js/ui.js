// Minimal JS interop for the P1.5 overlay/content primitives: dialog open/
// close, drawer focus capture/restore + scroll lock, copy-to-clipboard, and
// scrolling a terminal pane to its latest line. Everything else (focus trap,
// Escape-to-close, return focus for Dialog) is native <dialog> behavior.
(function () {
  var lastFocused = null;

  window.ui_showDialog = function (el) {
    if (el && typeof el.showModal === 'function' && !el.open) el.showModal();
  };

  window.ui_closeDialog = function (el) {
    if (el && el.open) el.close();
  };

  window.ui_captureFocus = function () {
    lastFocused = document.activeElement;
  };

  window.ui_restoreFocus = function () {
    if (lastFocused && typeof lastFocused.focus === 'function') lastFocused.focus();
    lastFocused = null;
  };

  window.ui_focusElement = function (el) {
    if (el && typeof el.focus === 'function') el.focus();
  };

  window.ui_lockScroll = function (lock) {
    document.body.style.overflow = lock ? 'hidden' : '';
  };

  window.ui_scrollToBottom = function (el) {
    if (el) el.scrollTop = el.scrollHeight;
  };

  window.ui_copyText = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(function () { return true; }).catch(function () { return false; });
    }
    try {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return Promise.resolve(ok);
    } catch (e) {
      return Promise.resolve(false);
    }
  };
})();
